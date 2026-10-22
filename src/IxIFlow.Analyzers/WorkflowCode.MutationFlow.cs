using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

internal sealed partial class WorkflowCode
{
    private sealed class MutationAliases : Dictionary<ISymbol, bool>
    {
        public MutationAliases() : base(SymbolEqualityComparer.Default) { }
        public HashSet<ISymbol> Writable { get; } = new(SymbolEqualityComparer.Default);
        public Dictionary<CaptureId, bool> Captures { get; } = new();
        public Dictionary<object, bool> Booleans { get; } = new(new MutationValueComparer());
        public void Forget()
        {
            foreach (var symbol in Keys.ToArray()) this[symbol] = false;
            Captures.Clear(); Booleans.Clear();
        }
        public MutationAliases Copy()
        {
            var copy = new MutationAliases();
            copy.Replace(this);
            return copy;
        }
        public void Replace(MutationAliases other)
        {
            Clear(); Captures.Clear(); Booleans.Clear(); Writable.Clear();
            Writable.UnionWith(other.Writable);
            foreach (var pair in other) Add(pair.Key, pair.Value);
            foreach (var pair in other.Captures) Captures.Add(pair.Key, pair.Value);
            foreach (var pair in other.Booleans) Booleans.Add(pair.Key, pair.Value);
        }
        // Must-fresh lattice: a binding is fresh only on every reaching path. Missing means unknown.
        public bool Merge(MutationAliases other)
        {
            var changed = false;
            var writableCount = Writable.Count;
            Writable.IntersectWith(other.Writable);
            changed |= Writable.Count != writableCount;
            foreach (var key in Keys.Concat(other.Keys).Distinct(SymbolEqualityComparer.Default).ToArray())
            {
                var next = TryGetValue(key, out var a) && a && other.TryGetValue(key, out var b) && b;
                if (!ContainsKey(key) || this[key] != next) { this[key] = next; changed = true; }
            }
            foreach (var key in Captures.Keys.Concat(other.Captures.Keys).Distinct().ToArray())
            {
                var next = Captures.TryGetValue(key, out var a) && a && other.Captures.TryGetValue(key, out var b) && b;
                if (!Captures.ContainsKey(key) || Captures[key] != next) { Captures[key] = next; changed = true; }
            }
            foreach (var key in Booleans.Keys.ToArray())
                if (!other.Booleans.TryGetValue(key, out var value) || value != Booleans[key])
                { Booleans.Remove(key); changed = true; }
            return changed;
        }
    }

    private ControlFlowGraph? MutationGraph(IOperation body, CancellationToken cancellation)
    {
        // Only lower a complete body. A label (including a local initializer inside a
        // callback) must not expand into the enclosing authoring function's graph.
        if (body.Parent != null && body is not (IAnonymousFunctionOperation or ILocalFunctionOperation) &&
            body is not IBlockOperation { Parent: IAnonymousFunctionOperation or ILocalFunctionOperation }) return null;
        // Roslyn lowers decisions, guards, short circuiting and abrupt transfers to real edges.
        // Nested functions have their own graphs, retrieved through their enclosing graph.
        var functions = new Stack<IOperation>();
        var root = body;
        for (; root.Parent != null; root = root.Parent)
            if (root is IAnonymousFunctionOperation or ILocalFunctionOperation) functions.Push(root);
        if (body != root && functions.Count == 0) return null; // An individual label asset, not its author's method.
        if (root is not (IMethodBodyOperation or IConstructorBodyOperation or IBlockOperation or
            IFieldInitializerOperation or IPropertyInitializerOperation or IParameterInitializerOperation)) return null;
        var graph = ControlFlowGraph.Create(root.Syntax, Model(root.Syntax.SyntaxTree), cancellation);
        while (graph != null && functions.Count > 0)
        {
            var function = functions.Pop();
            if (function is ILocalFunctionOperation local)
                graph = graph.GetLocalFunctionControlFlowGraph(local.Symbol, cancellation);
            else
            {
                var symbol = ((IAnonymousFunctionOperation)function).Symbol;
                var lowered = graph.Blocks.SelectMany(b => b.Operations.Concat(b.BranchValue == null ?
                        Enumerable.Empty<IOperation>() : new[] { b.BranchValue }))
                    .SelectMany(o => o.DescendantsAndSelf()).OfType<IFlowAnonymousFunctionOperation>()
                    .SingleOrDefault(f => SymbolEqualityComparer.Default.Equals(f.Symbol, symbol));
                if (lowered == null) return null;
                graph = graph.GetAnonymousFunctionControlFlowGraph(lowered, cancellation);
            }
        }
        return graph;
    }

    private sealed class MutationValueComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => x is ISymbol a && y is ISymbol b ? SymbolEqualityComparer.Default.Equals(a, b) : object.Equals(x, y);
        public int GetHashCode(object value) => value is ISymbol symbol ? SymbolEqualityComparer.Default.GetHashCode(symbol) : value.GetHashCode();
    }

    private static object? BooleanKey(IOperation operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFlowCaptureReferenceOperation capture => capture.Id,
        _ => null
    };

    // Boolean facts survive only conversions which preserve the boolean value.
    private static IOperation BooleanOperand(IOperation operation)
    {
        while (true)
        {
            if (operation is IParenthesizedOperation parentheses) operation = parentheses.Operand;
            else if (operation is IConversionOperation conversion && !conversion.Conversion.IsUserDefined &&
                (conversion.Conversion.IsIdentity || conversion.Conversion.IsNullable)) operation = conversion.Operand;
            else return operation;
        }
    }

    private static bool? MutationBoolean(IOperation? operation, MutationAliases state)
    {
        if (operation == null) return null;
        operation = BooleanOperand(operation);
        if (operation.ConstantValue is { HasValue: true, Value: bool constant }) return constant;
        if (BooleanKey(operation) is { } key && state.Booleans.TryGetValue(key, out var bound)) return bound;
        return operation switch
        {
            IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } unary => !MutationBoolean(unary.Operand, state),
            IIsPatternOperation { Pattern: IConstantPatternOperation pattern } test when MutationBoolean(pattern.Value, state) is { } value =>
                MutationBoolean(test.Value, state) is { } tested ? tested == value : null,
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } binary when
                MutationBoolean(binary.LeftOperand, state) is { } left && MutationBoolean(binary.RightOperand, state) is { } right =>
                (left == right) == (binary.OperatorKind == BinaryOperatorKind.Equals),
            _ => null
        };
    }

    private static void Assume(IOperation operation, bool truth, MutationAliases state)
    {
        operation = BooleanOperand(operation);
        if (BooleanKey(operation) is { } key) state.Booleans[key] = truth;
        else if (operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } unary) Assume(unary.Operand, !truth, state);
        else if (operation is IIsPatternOperation { Pattern: IConstantPatternOperation pattern } test && MutationBoolean(pattern.Value, state) is { } value)
            Assume(test.Value, truth == value, state);
        else if (operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } binary)
        {
            if (MutationBoolean(binary.RightOperand, state) is { } right) Assume(binary.LeftOperand, truth == (right == (binary.OperatorKind == BinaryOperatorKind.Equals)), state);
            else if (MutationBoolean(binary.LeftOperand, state) is { } left) Assume(binary.RightOperand, truth == (left == (binary.OperatorKind == BinaryOperatorKind.Equals)), state);
        }
    }

    private sealed class MutationExit(int? destination, ControlFlowBranchSemantics semantics, MutationAliases state, IEnumerable<ControlFlowRegion>? unwind = null)
    {
        public int? Destination { get; } = destination;
        public ControlFlowBranchSemantics Semantics { get; } = semantics;
        public MutationAliases State { get; } = state;
        public ControlFlowRegion[] Unwind { get; } = unwind?.ToArray() ?? [];
    }

    private static void MutationFlow(ControlFlowGraph graph, MutationAliases input,
        Action<IOperation, MutationAliases, Action<MutationAliases>> visit,
        Action<IOperation, bool, MutationAliases> match, CancellationToken cancellation)
    {
        var unhandled = new Queue<MutationExit>(Execute(graph.Root, graph.Root.FirstBlockOrdinal, input).Where(IsThrow));
        while (unhandled.Count > 0)
            foreach (var exit in Unwind(unhandled.Dequeue()))
                if (IsThrow(exit) && exit.Unwind.Length > 0) unhandled.Enqueue(exit);

        List<MutationExit> Execute(ControlFlowRegion region, int entry, MutationAliases initial)
        {
            cancellation.ThrowIfCancellationRequested();
            if (region.Kind == ControlFlowRegionKind.TryAndFinally)
            {
                var body = region.NestedRegions[0];
                var finalizer = region.NestedRegions[1];
                var exits = new List<MutationExit>();
                foreach (var pending in Execute(body, entry, initial))
                    if (IsThrow(pending))
                        Add(exits, new MutationExit(null, pending.Semantics, pending.State, pending.Unwind.Append(finalizer)));
                    else
                        foreach (var final in Execute(finalizer, finalizer.FirstBlockOrdinal, pending.State))
                            Add(exits, final.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling
                                ? new MutationExit(pending.Destination, pending.Semantics, final.State) : final);
                return exits;
            }
            if (region.Kind == ControlFlowRegionKind.TryAndCatch)
            {
                var exits = new List<MutationExit>();
                var search = new Queue<MutationExit>();
                foreach (var outcome in Execute(region.NestedRegions[0], entry, initial))
                    if (IsThrow(outcome)) search.Enqueue(outcome);
                    else Add(exits, outcome);
                while (search.Count > 0)
                {
                    var thrown = new List<MutationExit> { search.Dequeue() };
                    foreach (var handler in region.NestedRegions.Skip(1))
                    {
                        var remaining = new List<MutationExit>();
                        foreach (var exception in thrown)
                        {
                            // First pass: matching and filters run before any intervening finally.
                            if (handler.ExceptionType?.SpecialType != SpecialType.System_Object &&
                                handler.ExceptionType?.ToDisplayString() != "System.Exception") Add(remaining, exception);
                            if (handler.Kind == ControlFlowRegionKind.FilterAndHandler)
                            {
                                var filter = handler.NestedRegions[0];
                                var caught = handler.NestedRegions[1];
                                foreach (var decision in Execute(filter, filter.FirstBlockOrdinal, exception.State))
                                    if (decision.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
                                        Add(remaining, new MutationExit(null, ControlFlowBranchSemantics.Throw, decision.State, exception.Unwind));
                                    else Catch(caught, decision.Destination ?? caught.FirstBlockOrdinal,
                                        new MutationExit(null, exception.Semantics, decision.State, exception.Unwind));
                            }
                            else Catch(handler, handler.FirstBlockOrdinal, exception);
                        }
                        thrown = remaining;
                    }
                    foreach (var exception in thrown) Add(exits, exception);
                }
                return exits;

                void Catch(ControlFlowRegion handler, int start, MutationExit exception)
                {
                    // Second pass: unwind before entering the selected handler. A new exception
                    // from a finalizer restarts search and carries only the still-pending finalizers.
                    foreach (var ready in Unwind(exception))
                        if (IsThrow(ready)) search.Enqueue(ready);
                        else foreach (var outcome in Execute(handler, start, ready.State)) Add(exits, outcome);
                }
            }

            var output = new List<MutationExit>();
            var states = new Dictionary<int, MutationAliases>();
            var pendingBlocks = new Queue<int>();
            Arrive(new MutationExit(entry, ControlFlowBranchSemantics.Regular, initial));
            while (pendingBlocks.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var ordinal = pendingBlocks.Dequeue();
                var state = states[ordinal].Copy();
                var nested = region.NestedRegions.FirstOrDefault(r => Contains(r, ordinal));
                if (nested != null)
                {
                    foreach (var exit in Execute(nested, ordinal, state)) Arrive(exit);
                    continue;
                }
                var block = graph.Blocks[ordinal];
                void Exceptional(MutationAliases at) => Add(output, new MutationExit(null,
                    region.Kind == ControlFlowRegionKind.Filter ? ControlFlowBranchSemantics.StructuredExceptionHandling :
                    ControlFlowBranchSemantics.Throw, at));
                foreach (var operation in block.Operations) visit(operation, state, Exceptional);
                if (block.BranchValue != null) visit(block.BranchValue, state, Exceptional);
                var condition = MutationBoolean(block.BranchValue, state);
                var takeConditional = block.ConditionKind == ControlFlowConditionKind.WhenTrue ? condition :
                    block.ConditionKind == ControlFlowConditionKind.WhenFalse ? !condition : null;
                if (takeConditional != false) Follow(block.ConditionalSuccessor, block.ConditionKind == ControlFlowConditionKind.WhenTrue);
                if (takeConditional != true) Follow(block.FallThroughSuccessor, block.ConditionKind == ControlFlowConditionKind.WhenFalse);

                void Follow(ControlFlowBranch? edge, bool truth)
                {
                    if (edge == null || edge.Semantics == ControlFlowBranchSemantics.None) return;
                    // Finally execution is handled at the region boundary, once for each transfer.
                    if (edge.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
                        Exceptional(state.Copy());
                    else
                    {
                        var next = state.Copy();
                        if (block.ConditionKind != ControlFlowConditionKind.None && block.BranchValue != null)
                        {
                            Assume(block.BranchValue, truth, next);
                            match(block.BranchValue, truth, next);
                        }
                        Arrive(new MutationExit(edge.Destination?.Ordinal, edge.Semantics, next));
                    }
                }
            }
            if (region.Kind == ControlFlowRegionKind.Filter)
                for (var i = 0; i < output.Count; i++)
                    if (IsThrow(output[i])) output[i] = new MutationExit(null, ControlFlowBranchSemantics.StructuredExceptionHandling, output[i].State);
            return output;

            void Arrive(MutationExit exit)
            {
                if (exit.Destination is not { } destination || !Contains(region, destination)) { Add(output, exit); return; }
                if (!graph.Blocks[destination].IsReachable) return;
                if (!states.TryGetValue(destination, out var previous))
                { states.Add(destination, exit.State.Copy()); pendingBlocks.Enqueue(destination); }
                else if (previous.Merge(exit.State)) pendingBlocks.Enqueue(destination);
            }
        }

        List<MutationExit> Unwind(MutationExit exception)
        {
            var outcomes = new List<MutationExit> { new(null, ControlFlowBranchSemantics.Regular, exception.State) };
            var thrown = new List<MutationExit>();
            for (var index = 0; index < exception.Unwind.Length; index++)
            {
                var finalizer = exception.Unwind[index];
                var next = new List<MutationExit>();
                foreach (var prior in outcomes)
                    foreach (var final in Execute(finalizer, finalizer.FirstBlockOrdinal, prior.State))
                        if (final.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
                            Add(next, new MutationExit(null, ControlFlowBranchSemantics.Regular, final.State));
                        else Add(thrown, new MutationExit(final.Destination, final.Semantics, final.State,
                            final.Unwind.Concat(exception.Unwind.Skip(index + 1))));
                outcomes = next;
            }
            return outcomes.Concat(thrown).ToList();
        }

        static bool Contains(ControlFlowRegion region, int ordinal) => ordinal >= region.FirstBlockOrdinal && ordinal <= region.LastBlockOrdinal;
        static bool IsThrow(MutationExit exit) => exit.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow;
        static void Add(List<MutationExit> exits, MutationExit next)
        {
            var previous = exits.FirstOrDefault(e => e.Destination == next.Destination && e.Semantics == next.Semantics && e.Unwind.SequenceEqual(next.Unwind));
            if (previous == null) exits.Add(new MutationExit(next.Destination, next.Semantics, next.State.Copy(), next.Unwind));
            else previous.State.Merge(next.State);
        }
    }

    private static bool MayThrow(IOperation operation) => operation is
        IInvocationOperation or IObjectCreationOperation or IAnonymousObjectCreationOperation or IArrayCreationOperation or IPropertyReferenceOperation or
        IArrayElementReferenceOperation or IAwaitOperation or IDynamicInvocationOperation or IDynamicMemberReferenceOperation or
        IFieldReferenceOperation or
        IAssignmentOperation { Target: IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation } or
        IIncrementOrDecrementOperation { IsChecked: true } or ICompoundAssignmentOperation { IsChecked: true } or
        IIncrementOrDecrementOperation { OperatorMethod: not null } or
        ICompoundAssignmentOperation { OperatorMethod: not null } or
        IConversionOperation { IsTryCast: false } or IBinaryOperation { OperatorKind: BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder } or
        IBinaryOperation { IsChecked: true } or IUnaryOperation { IsChecked: true } or
        IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null };
}
