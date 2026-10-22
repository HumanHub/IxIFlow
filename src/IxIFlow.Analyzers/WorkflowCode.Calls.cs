using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

internal sealed partial class WorkflowCode
{
    // One evaluation plan for explicit and implicit calls. Null operands are method results
    // (including out values), for which we have no value/freshness summary.
    private sealed class CallStep(IOperation? value, IMethodSymbol? method = null,
        IOperation? receiver = null, IOperation?[]? arguments = null, bool construction = false,
        IArgumentOperation[]? refArguments = null, ITypeSymbol? receiverType = null,
        IOperation? condition = null, bool whenTrue = true, CallStep[]? branch = null)
    {
        public IOperation? Value { get; } = value;
        public IMethodSymbol? Method { get; } = method == null ? null : ResolveImplementation(method, receiver, receiverType);
        public IOperation? Receiver { get; } = receiver;
        public IOperation?[] Arguments { get; } = arguments ?? [];
        public bool Construction { get; } = construction;
        public IArgumentOperation[] RefArguments { get; } = refArguments ?? [];
        public IOperation? Condition { get; } = condition;
        public bool WhenTrue { get; } = whenTrue;
        public CallStep[]? Branch { get; } = branch;
    }

    private static IMethodSymbol ResolveImplementation(IMethodSymbol method, IOperation? receiver, ITypeSymbol? receiverType = null)
    {
        while (true)
        {
            if (receiver is IConversionOperation conversion && !conversion.Conversion.IsUserDefined) receiver = conversion.Operand;
            else if (receiver is IParenthesizedOperation parentheses) receiver = parentheses.Operand;
            else break;
        }
        return method.ContainingType.TypeKind == TypeKind.Interface && (receiver?.Type ?? receiverType) is INamedTypeSymbol concrete &&
            concrete.FindImplementationForInterfaceMember(method) is IMethodSymbol implementation ? implementation : method;
    }

    // Purity examines every possible call; mutation evaluation also preserves the branch.
    private static IEnumerable<CallStep> AllSteps(IEnumerable<CallStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Branch != null)
                foreach (var nested in AllSteps(step.Branch)) yield return nested;
        }
    }

    private IEnumerable<CallStep> MethodSteps(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation invocation:
                if (invocation.Instance != null) yield return new(invocation.Instance);
                foreach (var argument in invocation.Arguments) yield return new(argument);
                yield return Call(invocation.TargetMethod, invocation.Instance, invocation.Arguments);
                break;
            case IObjectCreationOperation creation:
                foreach (var argument in creation.Arguments) yield return new(argument);
                if (creation.Constructor != null) yield return Call(creation.Constructor, null, creation.Arguments, true);
                break; // The initializer executes after construction, with a fresh implicit receiver.
            case IPropertyReferenceOperation property:
                foreach (var step in PropertyInputs(property)) yield return step;
                if (property.Property.GetMethod != null) yield return Call(property.Property.GetMethod, property.Instance, property.Arguments);
                break;
            case IAssignmentOperation assignment:
                var compound = assignment as ICompoundAssignmentOperation;
                foreach (var step in Target(assignment.Target, compound != null)) yield return step;
                if (compound?.InConversion.MethodSymbol is { } input) yield return new(null, input, arguments: [compound.Target]);
                yield return new(assignment.Value);
                if (compound != null)
                {
                    if (compound.OperatorMethod is { } op) yield return new(null, op,
                        arguments: [compound.InConversion.IsUserDefined ? null : compound.Target, compound.Value]);
                    if (compound.OutConversion.MethodSymbol is { } output) yield return new(null, output, arguments: [null]);
                }
                if (assignment is IDeconstructionAssignmentOperation deconstruction)
                {
                    // Roslyn retains deconstruction in the CFG, rather than lowering its calls.
                    // Its public semantic info supplies the methods and element conversions.
                    var model = Model(operation.Syntax.SyntaxTree);
                    var info = operation.Syntax switch
                    {
                        AssignmentExpressionSyntax syntax => model.GetDeconstructionInfo(syntax),
                        ForEachVariableStatementSyntax syntax => model.GetDeconstructionInfo(syntax),
                        _ => default
                    };
                    foreach (var step in Deconstruct(info, deconstruction.Value, false)) yield return step;
                    foreach (var step in Deconstruct(info, deconstruction.Value, true)) yield return step;
                }
                foreach (var step in Store(assignment.Target, compound == null ? assignment.Value : null)) yield return step;
                break;
            case IIncrementOrDecrementOperation increment:
                foreach (var step in Target(increment.Target, true)) yield return step;
                if (increment.OperatorMethod != null) yield return new(null, increment.OperatorMethod, arguments: [increment.Target]);
                foreach (var step in Store(increment.Target, null)) yield return step;
                break;
            case IBinaryOperation binary:
                yield return new(binary.LeftOperand);
                if (binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
                {
                    var and = binary.OperatorKind == BinaryOperatorKind.ConditionalAnd;
                    if (binary.OperatorMethod is { } conditionalOperator)
                    {
                        // Outside a CFG Roslyn retains &&/|| as one binary operation.
                        // The truth operator runs before deciding whether to evaluate RHS.
                        var parameterType = conditionalOperator.Parameters[0].Type;
                        for (var type = parameterType as INamedTypeSymbol; type != null; type = type.BaseType)
                            if (type.GetMembers(and ? "op_False" : "op_True").OfType<IMethodSymbol>()
                                .FirstOrDefault(method => method.Parameters.Length == 1 &&
                                    SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, parameterType)) is { } truth)
                            {
                                yield return new(null, truth, arguments: [binary.LeftOperand]);
                                break;
                            }
                    }
                    var branch = new List<CallStep> { new(binary.RightOperand) };
                    if (binary.OperatorMethod != null)
                        branch.Add(new(null, binary.OperatorMethod, arguments: [binary.LeftOperand, binary.RightOperand]));
                    yield return new(null, condition: binary.LeftOperand, whenTrue: and, branch: branch.ToArray());
                    break;
                }
                yield return new(binary.RightOperand);
                if (binary.OperatorMethod != null) yield return new(null, binary.OperatorMethod, arguments: [binary.LeftOperand, binary.RightOperand]);
                break;
            case IUnaryOperation unary:
                yield return new(unary.Operand);
                if (unary.OperatorMethod != null) yield return new(null, unary.OperatorMethod, arguments: [unary.Operand]);
                break;
            case IConversionOperation conversion:
                yield return new(conversion.Operand);
                if (conversion.OperatorMethod != null) yield return new(null, conversion.OperatorMethod, arguments: [conversion.Operand]);
                break;
            case ICollectionExpressionOperation collection:
                foreach (var element in collection.Elements) yield return new(element);
                if (collection.ConstructMethod is { } construct)
                    yield return new(null, construct, arguments: [null], construction: construct.MethodKind == MethodKind.Constructor);
                break;
            case IRecursivePatternOperation pattern:
                if (pattern.DeconstructSymbol is IMethodSymbol deconstruct)
                {
                    var value = pattern.Parent is IIsPatternOperation test ? test.Value : null;
                    yield return new(null, deconstruct, deconstruct.IsStatic ? null : value,
                        deconstruct.IsStatic ? new[] { value } : null);
                }
                foreach (var child in pattern.ChildOperations) yield return new(child);
                break;
            case IForEachLoopOperation loop when operation.Syntax is CommonForEachStatementSyntax syntax:
                var enumeration = Model(syntax.SyntaxTree).GetForEachStatementInfo(syntax);
                yield return new(loop.Collection);
                if (enumeration.GetEnumeratorMethod is { } getEnumerator)
                    yield return new(null, getEnumerator, getEnumerator.IsStatic ? null : loop.Collection,
                        getEnumerator.IsStatic ? new[] { loop.Collection } : null);
                if (enumeration.MoveNextMethod is { } moveNext) yield return new(null, moveNext);
                if (enumeration.CurrentProperty?.GetMethod is { } current) yield return new(null, current);
                if (enumeration.CurrentConversion.MethodSymbol is { } currentConversion) yield return new(null, currentConversion, arguments: [null]);
                if (enumeration.ElementConversion.MethodSymbol is { } elementConversion) yield return new(null, elementConversion, arguments: [null]);
                yield return new(loop.Body);
                if (enumeration.DisposeMethod is { } dispose) yield return new(null, dispose, receiverType: enumeration.GetEnumeratorMethod?.ReturnType);
                break;
            default:
                // In particular, foreach is lowered by Roslyn into ordinary invocations and
                // property references: GetEnumerator, MoveNext, Current and finally Dispose.
                foreach (var child in operation.ChildOperations) yield return new(child);
                break;
        }

        static CallStep Call(IMethodSymbol method, IOperation? receiver, IEnumerable<IArgumentOperation> arguments, bool construction = false)
        {
            var args = arguments.ToArray();
            var values = new IOperation?[method.Parameters.Length];
            foreach (var argument in args)
                if (argument.Parameter is { } parameter && parameter.Ordinal < values.Length) values[parameter.Ordinal] = argument.Value;
            return new(null, method, receiver, values, construction, args);
        }

        static IEnumerable<CallStep> PropertyInputs(IPropertyReferenceOperation property)
        {
            if (property.Instance != null) yield return new(property.Instance);
            foreach (var argument in property.Arguments) yield return new(argument);
        }

        IEnumerable<CallStep> Target(IOperation target, bool read)
        {
            if (target is IPropertyReferenceOperation property)
            {
                foreach (var step in PropertyInputs(property)) yield return step;
                if ((read || property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly) && property.Property.GetMethod is { } get)
                    yield return Call(get, property.Instance, property.Arguments);
            }
            else if (target is ITupleOperation or IDeclarationExpressionOperation)
            {
                foreach (var child in target.ChildOperations)
                    foreach (var step in Target(child, read)) yield return step;
            }
            else yield return new(target);
        }

        static IEnumerable<CallStep> Store(IOperation target, IOperation? value)
        {
            if (target is IPropertyReferenceOperation { Property.ReturnsByRef: false, Property.ReturnsByRefReadonly: false } property &&
                property.Property.SetMethod is { } set)
            {
                var args = new IOperation?[set.Parameters.Length];
                foreach (var argument in property.Arguments)
                    if (argument.Parameter is { } parameter) args[parameter.Ordinal] = argument.Value;
                args[args.Length - 1] = value;
                yield return new(null, set, property.Instance, args);
            }
            else if (target is ITupleOperation or IDeclarationExpressionOperation)
                foreach (var child in target.ChildOperations)
                    foreach (var step in Store(child, null)) yield return step;
        }

        static IEnumerable<CallStep> Deconstruct(DeconstructionInfo info, IOperation? value, bool conversions)
        {
            if (!conversions && info.Method is { } method)
                yield return new(null, method, method.IsStatic ? null : value,
                    method.IsStatic ? new[] { value } : null);
            if (conversions && info.Conversion?.MethodSymbol is { } conversion) yield return new(null, conversion, arguments: [value]);
            for (var i = 0; i < info.Nested.Length; i++)
                foreach (var step in Deconstruct(info.Nested[i], info.Method == null && value is ITupleOperation tuple ? tuple.Elements[i] : null, conversions))
                    yield return step;
        }
    }
}
