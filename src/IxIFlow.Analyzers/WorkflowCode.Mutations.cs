using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace IxIFlow.Analyzers;

internal sealed partial class WorkflowCode
{
    private static readonly HashSet<string> Mutators = new()
    {
        "Add", "AddRange", "TryAdd", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "TryRemove", "Clear",
        "Insert", "InsertRange", "Sort", "Reverse", "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop", "TryPop",
        "UnionWith", "IntersectWith", "ExceptWith", "SymmetricExceptWith", "Append", "AppendLine"
    };

    private static bool ReturnsCollection(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol result &&
        (SymbolEqualityComparer.Default.Equals(result, method.ContainingType) ||
         method.IsStatic && method.Parameters.Length > 0 && SymbolEqualityComparer.Default.Equals(result, method.Parameters[0].Type)) &&
        result.ToDisplayString() != "System.Text.StringBuilder" && result.Name != "Builder";

    private static bool MutatesCollection(IMethodSymbol method) => Mutators.Contains(method.Name) && !ReturnsCollection(method) &&
        (method.ContainingType.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable) ||
         method.ContainingType.SpecialType == SpecialType.System_Collections_IEnumerable ||
         method.ContainingType.Name is "ICollection" or "IList" or "IDictionary" or "ISet" ||
         method.ContainingType.ToDisplayString() == "System.Text.StringBuilder");

    // Freshness is a proof, not an assumption. Unresolved aliases and calls are treated as existing state.
    public IEnumerable<(IOperation Use, string Member)> MutationUses(IOperation operation, CancellationToken cancellation, bool destination = false)
    {
        var results = new List<(IOperation, string)>();
        var path = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var aliases = new MutationAliases();
        var callbackEffects = 0;
        if (destination && operation is IAnonymousFunctionOperation { Body.Operations.Length: 1 } lambda &&
            lambda.Body.Operations[0] is IReturnOperation { ReturnedValue: { } returned })
        {
            var target = EngineSymbols.Unwrapped(returned);
            VisitDestination(target, aliases);
        }
        else Analyze(operation, aliases, false, 0, null);

        void VisitDestination(IOperation target, MutationAliases bindings)
        {
            target = EngineSymbols.Unwrapped(target);
            if (target is IPropertyReferenceOperation property)
            {
                if (property.Instance != null) Visit(property.Instance, bindings, false, 0, null);
                foreach (var argument in property.Arguments) Visit(argument.Value, bindings, false, 0, null);
            }
            else if (target is IArrayElementReferenceOperation array)
            {
                Visit(array.ArrayReference, bindings, false, 0, null);
                foreach (var index in array.Indices) Visit(index, bindings, false, 0, null);
            }
            else Visit(target, bindings, false, 0, null);
            // The compiler permits the location writes themselves; both scans follow the same
            // executed setter chain, stopping as soon as a reference receiver ends write-back.
            foreach (var link in DestinationWrites(target).OfType<IPropertyReferenceOperation>())
                Through(link.Property.SetMethod, [], link.Instance, bindings, false, 0, link, receiverFresh: true);
        }
        return results.Distinct();

        void Analyze(IOperation body, MutationAliases bindings, bool instance, int depth, IOperation? site)
        {
            if (MutationGraph(body, cancellation) is { } graph)
                AnalyzeGraph(graph, bindings, instance, depth, site);
            else Visit(body, bindings, instance, depth, site);
        }

        void AnalyzeGraph(ControlFlowGraph graph, MutationAliases bindings, bool instance, int depth, IOperation? site) =>
            MutationFlow(graph, bindings, (each, state, exceptional) => Visit(each, state, instance, depth, site, exceptional, graph),
                (condition, truth, state) => Match(condition, truth, state, instance), cancellation);

        bool Fresh(IOperation? value, MutationAliases bindings, bool instance)
        {
            if (value == null) return false;
            if (value is IParenthesizedOperation parenthesized) return Fresh(parenthesized.Operand, bindings, instance);
            if (value is IConversionOperation conversion)
            {
                var kind = conversion.GetConversion();
                // CFGs wrap target-typed construction in conversion nodes. These allocate storage;
                // they do not transfer an arbitrary operand's freshness through a conversion.
                if (kind.IsObjectCreation && conversion.Operand is IObjectCreationOperation) return true;
                if (kind.IsCollectionExpression && conversion.Operand is ICollectionExpressionOperation)
                    return Fresh(conversion.Operand, bindings, instance);
                return !kind.IsUserDefined && (kind.IsIdentity || kind.IsReference || kind.IsBoxing || kind.IsNullable) &&
                    Fresh(conversion.Operand, bindings, instance);
            }
            return value switch
            {
                IObjectCreationOperation or IArrayCreationOperation or IAnonymousObjectCreationOperation => true,
                ICollectionExpressionOperation collection => collection.Type is IArrayTypeSymbol ||
                    collection.Type is INamedTypeSymbol { Name: "List", Arity: 1 } list &&
                    list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" &&
                    collection.ConstructMethod?.MethodKind == MethodKind.Constructor,
                IInstanceReferenceOperation => instance,
                IFlowCaptureReferenceOperation capture => bindings.Captures.TryGetValue(capture.Id, out var captured) && captured,
                IFieldReferenceOperation field => !field.Field.IsStatic && field.Field.Type.IsValueType && Fresh(field.Instance, bindings, instance),
                ISimpleAssignmentOperation assignment => Fresh(assignment.Value, bindings, instance),
                ILocalReferenceOperation local => local.Local.RefKind == RefKind.None && bindings.TryGetValue(local.Local, out var fresh) && fresh,
                IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.None && bindings.TryGetValue(parameter.Parameter, out var fresh) && fresh,
                IConditionalOperation conditional => Fresh(conditional.WhenTrue, bindings, instance) && Fresh(conditional.WhenFalse, bindings, instance),
                _ => false
            };
        }

        bool WritableFresh(IOperation target, MutationAliases bindings, bool instance) => target switch
        {
            ITupleOperation tuple => tuple.Elements.All(element => WritableFresh(element, bindings, instance)),
            IDeclarationExpressionOperation declaration => WritableFresh(declaration.Expression, bindings, instance),
            ILocalReferenceOperation local => local.Local.RefKind == RefKind.None,
            IDiscardOperation => true,
            IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.None || bindings.Writable.Contains(parameter.Parameter),
            IFieldReferenceOperation field => !field.Field.IsStatic && Fresh(field.Instance, bindings, instance),
            IPropertyReferenceOperation property => !IsRefStorage(property) && !property.Property.IsStatic && Fresh(property.Instance, bindings, instance),
            IArrayElementReferenceOperation array => Fresh(array.ArrayReference, bindings, instance),
            _ => false
        };

        void Through(IMethodSymbol? method, IEnumerable<IArgumentOperation> arguments, IOperation? receiver,
            MutationAliases bindings, bool instance, int depth, IOperation site, bool construction = false, bool deferred = false, bool[]? callBindings = null, bool? receiverFresh = null)
        {
            if (method == null) { bindings.Forget(); return; }
            var effectsBefore = callbackEffects;
            var args = arguments.ToArray();
            // Callback targets and their captured writes are deliberately not modeled. Forget the
            // caller's proofs, including when this effect is found in a transitive helper body.
            if (method.MethodKind == MethodKind.DelegateInvoke || method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate) ||
                args.Any(a => a.Value.DescendantsAndSelf().Any(o => o.Type?.TypeKind == TypeKind.Delegate)))
            {
                callbackEffects++;
                bindings.Forget();
            }
            (HashSet<ISymbol> Captured, HashSet<ISymbol> Written, bool Imprecise)? closure = method.MethodKind == MethodKind.LocalFunction ? Closure(method) : null;
            if (depth >= MaxDepth || Key(method) is not IMethodSymbol key || !path.Add(key))
            {
                if (closure != null) bindings.Forget();
                else if (method.MethodKind == MethodKind.DelegateInvoke) bindings.Forget();
                return;
            }
            // Captures are caller bindings, but CFG capture IDs belong to one graph only.
            var passed = new MutationAliases();
            if (closure != null)
                foreach (var symbol in closure.Value.Captured)
                    passed[symbol] = !deferred && bindings.TryGetValue(symbol, out var fresh) && fresh;
            for (var index = 0; index < method.Parameters.Length; index++)
                if (bindings.TryGetValue(method.Parameters[index], out var bound)) passed[key.Parameters[index]] = bound;
            if (callBindings != null)
                for (var index = 0; index < callBindings.Length && index < key.Parameters.Length; index++)
                    passed[key.Parameters[index]] = callBindings[index];
            foreach (var argument in args)
                if (callBindings == null && argument.Parameter is { } parameter && parameter.Ordinal < key.Parameters.Length)
                    passed[key.Parameters[parameter.Ordinal]] = Fresh(argument.Value, bindings, instance);
            // Ref/out storage is separate from the freshness of the value stored in it.
            // Implicit Deconstruct outputs are compiler temporaries, then converted/stored.
            for (var index = 0; index < method.Parameters.Length; index++)
                if (method.Parameters[index].RefKind is RefKind.Ref or RefKind.Out &&
                    (args.Length == 0 && callBindings != null || args.Any(a => a.Parameter?.Ordinal == index && WritableFresh(a.Value, bindings, instance))))
                    passed.Writable.Add(key.Parameters[index]);
            foreach (var body in Bodies(key, withInitializers: true, cancellation))
                Analyze(body, passed, construction || (receiverFresh ?? Fresh(receiver, bindings, instance)), depth + 1, site);
            if (ImplicitBase(key, cancellation) is { } parent)
                Through(parent, [], null, passed, construction, depth + 1, site, construction);
            path.Remove(key);
            if (callbackEffects != effectsBefore) bindings.Forget();
            if (closure is { Imprecise: true }) bindings.Forget();
            else if (closure != null) Forget(closure.Value.Written, bindings);
        }

        void Visit(IOperation each, MutationAliases bindings, bool instance, int depth, IOperation? callSite, System.Action<MutationAliases>? exceptional = null, ControlFlowGraph? graph = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (each is INameOfOperation or ITypeOfOperation or ILocalFunctionOperation) return;
            void Report(string member) => results.Add((callSite ?? each, member + " (state mutation)"));
            var evaluated = new Dictionary<IOperation, bool>();
            bool Bound(IOperation? value, MutationAliases state) => value != null && (evaluated.TryGetValue(value, out var fresh) ? fresh : Fresh(value, state, instance));
            bool WritableTarget(IOperation target) => target switch
            {
                ITupleOperation tuple => tuple.Elements.All(WritableTarget),
                IDeclarationExpressionOperation declaration => WritableTarget(declaration.Expression),
                IPropertyReferenceOperation property => !IsRefStorage(property) && !property.Property.IsStatic && Bound(property.Instance, bindings),
                IFieldReferenceOperation field => !field.Field.IsStatic && Bound(field.Instance, bindings),
                IArrayElementReferenceOperation array => Bound(array.ArrayReference, bindings),
                _ => WritableFresh(target, bindings, instance)
            };
            if (each is not (IArrayCreationOperation or IMemberInitializerOperation or IConditionalOperation or
                ISwitchExpressionOperation or ICoalesceOperation or IFlowCaptureOperation))
                Evaluate(MethodSteps(each), bindings);

            void Evaluate(IEnumerable<CallStep> steps, MutationAliases state)
            {
                foreach (var step in steps)
                {
                    if (step.Branch != null)
                    {
                        var condition = each is IBinaryOperation { OperatorMethod: not null } ? null : MutationBoolean(step.Condition, state);
                        if (condition == !step.WhenTrue) continue;
                        var skipped = condition == null ? state.Copy() : null;
                        Evaluate(step.Branch, state);
                        if (skipped != null) state.Merge(skipped);
                        continue;
                    }
                    if (step.Value is { } operand)
                    {
                        Visit(operand, state, instance, depth, callSite, exceptional, graph);
                        evaluated[operand] = Fresh(operand, state, instance);
                        if (operand is IArgumentOperation argument) evaluated[argument.Value] = Fresh(argument.Value, state, instance);
                        continue;
                    }
                    if (step.Method is not { } method) continue;
                    var receiver = step.Construction || Bound(step.Receiver, state);
                    if ((MutatesCollection(method) || method.MethodKind == MethodKind.PropertySet) && !receiver)
                        Report(method.ContainingType.Name + "." + method.Name);
                    if (Mutators.Contains(method.Name) && !ReturnsCollection(method) && method.IsStatic && step.Arguments.Length > 0 &&
                        (method.ContainingType.SpecialType == SpecialType.System_Array || method.ContainingNamespace.ToDisplayString().StartsWith("System.Collections", System.StringComparison.Ordinal)) &&
                        !Bound(step.Arguments[0], state)) Report(method.Name);
                    foreach (var argument in step.RefArguments)
                        if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out &&
                            !WritableFresh(EngineSymbols.Unwrapped(argument.Value), state, instance)) Report(method.Name + " ref/out");
                    exceptional?.Invoke(state.Copy());
                    Through(method, step.RefArguments, step.Receiver, state, instance, depth, callSite ?? each,
                        construction: step.Construction, callBindings: step.Arguments.Select(value => Bound(value, state)).ToArray(), receiverFresh: receiver);
                    ForgetRefArguments(step.RefArguments, state);
                    exceptional?.Invoke(state.Copy());
                }
            }
            // Exceptional edges leave the evaluated operands but precede the local store. Calls and
            // accessors can fail after changing ref/out arguments, so those aliases also escape unproven.
            if (each is not (IObjectCreationOperation or IArrayCreationOperation) && MayThrow(each)) exceptional?.Invoke(bindings.Copy());
            switch (each)
            {
                case IFlowAnonymousFunctionOperation anonymous when graph != null:
                    var nested = graph.GetAnonymousFunctionControlFlowGraph(anonymous, cancellation);
                    var captured = Closure(anonymous.Symbol);
                    var unknown = new MutationAliases();
                    if (depth < MaxDepth) AnalyzeGraph(nested, unknown, false, depth + 1, callSite ?? each);
                    if (captured.Imprecise) bindings.Forget();
                    else Forget(captured.Written, bindings);
                    return;
                case IConditionalOperation conditional:
                    Visit(conditional.Condition, bindings, instance, depth, callSite, exceptional, graph);
                    Alternatives(new[] { conditional.WhenTrue, conditional.WhenFalse });
                    return;
                case ISwitchExpressionOperation choice:
                    Visit(choice.Value, bindings, instance, depth, callSite, exceptional, graph);
                    Alternatives(choice.Arms.Cast<IOperation?>());
                    return;
                case ICoalesceOperation coalesce:
                    Visit(coalesce.Value, bindings, instance, depth, callSite, exceptional, graph);
                    Alternatives(new IOperation?[] { coalesce.WhenNull, null });
                    return;
                case IFlowCaptureOperation capture:
                    Visit(capture.Value, bindings, instance, depth, callSite, exceptional, graph);
                    bindings.Captures[capture.Id] = Fresh(capture.Value, bindings, instance);
                    if (MutationBoolean(capture.Value, bindings) is { } capturedBoolean) bindings.Booleans[capture.Id] = capturedBoolean;
                    else bindings.Booleans.Remove(capture.Id);
                    return;
                case IVariableDeclaratorOperation variable:
                    bindings[variable.Symbol] = Fresh(variable.Initializer?.Value, bindings, instance);
                    break;
                case IAssignmentOperation assignment:
                    // A ref declaration/rebinding changes the alias, not the referred storage.
                    if (assignment is ISimpleAssignmentOperation { IsRef: true })
                    {
                        if (assignment.Target is ILocalReferenceOperation refLocal) bindings[refLocal.Local] = false;
                        break;
                    }
                    if (!WritableTarget(assignment.Target)) Report("assignment");
                    if (assignment is IDeconstructionAssignmentOperation)
                    {
                        InvalidateTarget(assignment.Target);
                        break;
                    }
                    if (IsRefStorage(assignment.Target)) bindings.Forget();
                    var assignedSymbol = assignment.Target switch
                    {
                        ILocalReferenceOperation local => (ISymbol)local.Local,
                        IParameterReferenceOperation parameter => parameter.Parameter,
                        _ => null
                    };
                    if (assignedSymbol != null)
                        bindings[assignedSymbol] = assignment is ISimpleAssignmentOperation && Fresh(assignment.Value, bindings, instance);
                    if (BooleanKey(assignment.Target) is { } key)
                    {
                        if (assignment is ISimpleAssignmentOperation && MutationBoolean(assignment.Value, bindings) is { } boolean) bindings.Booleans[key] = boolean;
                        else bindings.Booleans.Remove(key);
                    }
                    break;
                case IIncrementOrDecrementOperation increment:
                    if (!WritableTarget(increment.Target)) Report("increment/decrement");
                    InvalidateTarget(increment.Target);
                    break;
                case IMethodReferenceOperation reference:
                    // A delegate can run after its captured bindings change, just like a lambda.
                    Through(ResolveImplementation(reference.Method, reference.Instance), [], reference.Instance, bindings, instance, depth, callSite ?? each, deferred: true);
                    break;
                case IArrayCreationOperation array:
                    foreach (var size in array.DimensionSizes) Visit(size, bindings, instance, depth, callSite, exceptional, graph);
                    exceptional?.Invoke(bindings.Copy());
                    if (array.Initializer != null) Visit(array.Initializer, bindings, instance, depth, callSite, exceptional, graph);
                    return;
                case IObjectCreationOperation creation:
                    if (creation.Initializer != null) Visit(creation.Initializer, bindings, true, depth, callSite, exceptional, graph);
                    return;
                case IMemberInitializerOperation memberInitializer:
                    Visit(memberInitializer.InitializedMember, bindings, instance, depth, callSite, exceptional, graph);
                    // A new outer object can expose an existing collection through a field/getter.
                    Visit(memberInitializer.Initializer, bindings, Fresh(memberInitializer.InitializedMember, bindings, instance), depth, callSite, exceptional, graph);
                    return;
            }

            void InvalidateTarget(IOperation target)
            {
                if (target is ITupleOperation tuple)
                    foreach (var element in tuple.Elements) InvalidateTarget(element);
                else if (target is IDeclarationExpressionOperation declaration) InvalidateTarget(declaration.Expression);
                else
                {
                    if (IsRefStorage(target)) bindings.Forget();
                    if (target is ILocalReferenceOperation local) bindings[local.Local] = false;
                    if (target is IParameterReferenceOperation parameter) bindings[parameter.Parameter] = false;
                    if (BooleanKey(target) is { } changed) bindings.Booleans.Remove(changed);
                }
            }

            void ForgetRefArguments(IEnumerable<IArgumentOperation> arguments, MutationAliases state)
            {
                foreach (var argument in arguments.Where(a => a.Parameter?.RefKind is RefKind.Ref or RefKind.Out))
                {
                    var value = EngineSymbols.Unwrapped(argument.Value);
                    if (IsRefStorage(value)) state.Forget();
                    var symbol = value switch { ILocalReferenceOperation local => (ISymbol)local.Local, IParameterReferenceOperation parameter => parameter.Parameter, _ => null };
                    if (symbol != null) state[symbol] = false;
                    if (BooleanKey(value) is { } changed) state.Booleans.Remove(changed);
                }
            }

            void Alternatives(IEnumerable<IOperation?> branches)
            {
                MutationAliases? merged = null;
                foreach (var branch in branches)
                {
                    var fork = bindings.Copy();
                    if (branch != null) Visit(branch, fork, instance, depth, callSite, exceptional, graph);
                    if (merged == null) merged = fork;
                    else merged.Merge(fork);
                }
                if (merged != null) bindings.Replace(merged);
            }
        }

        static bool IsRefStorage(IOperation value) => value is ILocalReferenceOperation { Local.RefKind: not RefKind.None } or
            IParameterReferenceOperation { Parameter.RefKind: not RefKind.None } or
            IPropertyReferenceOperation { Property.ReturnsByRef: true } or IPropertyReferenceOperation { Property.ReturnsByRefReadonly: true } or
            IInvocationOperation { TargetMethod.ReturnsByRef: true } or IInvocationOperation { TargetMethod.ReturnsByRefReadonly: true };

        static void Forget(IEnumerable<ISymbol> symbols, MutationAliases bindings)
        {
            foreach (var symbol in symbols) { bindings[symbol] = false; bindings.Booleans.Remove(symbol); }
            // Lowered captures can retain an alias to a changed binding.
            bindings.Captures.Clear();
        }

        (HashSet<ISymbol> Captured, HashSet<ISymbol> Written, bool Imprecise) Closure(IMethodSymbol function)
        {
            var captured = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var written = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var scanning = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var imprecise = false;
            Scan(function);
            if (imprecise) written.UnionWith(captured);
            return (captured, written, imprecise);

            bool External(ISymbol symbol)
            {
                for (var owner = symbol.ContainingSymbol; owner != null; owner = owner.ContainingSymbol)
                    if (SymbolEqualityComparer.Default.Equals(owner, function)) return false;
                return symbol is ILocalSymbol or IParameterSymbol;
            }
            void Scan(IMethodSymbol current)
            {
                if (!scanning.Add(current)) { imprecise = true; return; }
                if (!seen.Add(current)) { scanning.Remove(current); return; }
                foreach (var body in Bodies(current, false, cancellation))
                    foreach (var operation in body.DescendantsAndSelf())
                    {
                        var symbol = operation switch
                        {
                            ILocalReferenceOperation local => (ISymbol)local.Local,
                            IParameterReferenceOperation parameter => parameter.Parameter,
                            _ => null
                        };
                        if (symbol != null && External(symbol))
                        {
                            captured.Add(symbol);
                            var target = operation;
                            while (target.Parent is ITupleOperation or IDeclarationExpressionOperation) target = target.Parent;
                            if (target.Parent is IAssignmentOperation assignment && assignment.Target == target ||
                                target.Parent is IIncrementOrDecrementOperation ||
                                target.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }) written.Add(symbol);
                        }
                        if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.LocalFunction } call) Scan(call.TargetMethod);
                        if (operation is IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } reference) Scan(reference.Method);
                        if (operation is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke } or IDynamicInvocationOperation ||
                            operation is IAssignmentOperation write && IsRefStorage(write.Target) ||
                            operation is IIncrementOrDecrementOperation increment && IsRefStorage(increment.Target) ||
                            operation is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument && IsRefStorage(argument.Value)) imprecise = true;
                    }
                scanning.Remove(current);
            }
        }

        void Match(IOperation condition, bool truth, MutationAliases bindings, bool instance)
        {
            condition = BooleanOperand(condition);
            if (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } negated)
                Match(negated.Operand, !truth, bindings, instance);
            else if (condition is IIsPatternOperation test) Pattern(test.Pattern, Fresh(test.Value, bindings, instance), truth);

            void Pattern(IPatternOperation pattern, bool fresh, bool success)
            {
                switch (pattern)
                {
                    case INegatedPatternOperation negated: Pattern(negated.Pattern, fresh, !success); break;
                    case IDeclarationPatternOperation { DeclaredSymbol: { } symbol } when success: bindings[symbol] = fresh; break;
                    case IRecursivePatternOperation recursive when success:
                        if (recursive.DeclaredSymbol != null) bindings[recursive.DeclaredSymbol] = fresh;
                        foreach (var positional in recursive.DeconstructionSubpatterns) Pattern(positional, false, true);
                        foreach (var property in recursive.PropertySubpatterns)
                            Pattern(property.Pattern, fresh && ValueFields(property.Member), true);
                        break;
                    case IBinaryPatternOperation binary when success && binary.OperatorKind == BinaryOperatorKind.And:
                        Pattern(binary.LeftPattern, fresh, true); Pattern(binary.RightPattern, fresh, true); break;
                }
            }
            bool ValueFields(IOperation member) => member is IFieldReferenceOperation { Field.IsStatic: false, Field.Type.IsValueType: true } field &&
                (field.Instance is IInstanceReferenceOperation || field.Instance != null && ValueFields(field.Instance));
        }
    }
}
