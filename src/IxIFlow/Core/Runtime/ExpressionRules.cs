using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The purity rule for the expressions a workflow is built from. Conditions, mappings, wait predicates
/// and selectors are evaluated again after a recovery, so they may not read the clock or the environment, make values
/// that differ per run, wait, or do I/O. One that uses such a member fails the build, naming the step and the member.
/// Expression trees are read as trees; lambdas given as delegates, such as wait predicates, are read from their IL,
/// with the lambdas they contain. A helper method they call is not looked into; the analyzer covers what it can see.
/// </summary>
internal static class ExpressionRules
{
    /// <summary>Checks the rule and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        foreach (var step in ParallelRules.Flatten(steps, crossInline: false))
        {
            try
            {
                Check(step, step.ConditionExpression);
                Check(step, step.ParallelCompletionExpression);
                Check(step, step.RequestSubjectExpression);
                Check(step, step.ForEachIntoExpression);
                Check(step, step.ForEachItemsExpression);
                Check(step, step.ResultExpression);
                Check(step, step.ForEachCompletionExpression);
                Check(step, step.OutcomeExpression);
                Check(step, step.Assignment?.Target);
                Check(step, step.Assignment?.Value);
                Check(step, step.AfterExpression);
                foreach (var behavior in Delegates.Select(property => property.GetValue(step) as Delegate))
                    Check(step, DelegateCode.FirstImpure(behavior));
                foreach (var mapping in step.InputMappings.Concat(step.OutputMappings))
                    Check(step, mapping.Expression);
                if (step.StepMetadata.TryGetValue("CompensationActivities", out var value) &&
                    value is List<CompensationActivityInfo> compensations)
                    foreach (var mapping in compensations.SelectMany(item => item.InputMappings.Concat(item.OutputMappings)))
                        Check(step, mapping.Expression);
            }
            catch (WorkflowEngineException error)
            {
                throw WorkflowBuildError.At(step, error);
            }
        }
        return steps;
    }

    // The step's delegates that hold workflow code: conditions, predicates, selectors and item lists.
    private static readonly PropertyInfo[] Delegates = typeof(WorkflowStep)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => typeof(Delegate).IsAssignableFrom(property.PropertyType))
        .ToArray();

    internal static void CheckDemand(WorkflowDemand demand)
    {
        Check(new WorkflowStep { Name = "DemandFrom(" + demand.Key + ")" }, demand.Expression);
        var clock = new DemandClock();
        clock.Visit(demand.Expression);
        if (clock.Found)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"DemandFrom '{demand.Key}' cannot read the workflow clock; use start data");
    }

    internal static void CheckLabel(WorkflowStep site, LambdaExpression expression)
    {
        try { Check(site, expression); }
        catch (WorkflowEngineException error)
        {
            throw WorkflowBuildError.At(site, new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                site.SourceLocation + ": " + error.Message, error));
        }
    }

    private sealed class DemandClock : ExpressionVisitor
    {
        internal bool Found;
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.Name == "UtcNow" && node.Member.DeclaringType?.IsGenericType == true &&
                node.Member.DeclaringType.GetGenericTypeDefinition() == typeof(WorkflowContext<>)) Found = true;
            return base.VisitMember(node);
        }
    }

    private static void Check(WorkflowStep step, LambdaExpression? expression)
    {
        if (expression == null)
            return;
        var found = new ImpureMembers();
        found.Visit(expression);
        Check(step, found.First);
    }

    private static void Check(WorkflowStep step, string? member)
    {
        if (member != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Step '{step.Name}' uses {member} in an expression. Expressions are evaluated again after a " +
                "recovery, so they may not read the clock or the environment, make values that differ per run, " +
                "wait, do I/O, or mutate existing state; do that in an activity and map its output");
    }

    private sealed class ImpureMembers : ExpressionVisitor
    {
        public string? First { get; private set; }
        private bool _freshInitializerReceiver;

        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            var outer = _freshInitializerReceiver;
            _freshInitializerReceiver = true;
            try { return base.VisitMemberInit(node); }
            finally { _freshInitializerReceiver = outer; }
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            if (!_freshInitializerReceiver) First ??= $"{node.Member.Name} (initializer on existing member state)";
            return base.VisitMemberAssignment(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Assign or ExpressionType.AddAssign or ExpressionType.AddAssignChecked or
                ExpressionType.SubtractAssign or ExpressionType.SubtractAssignChecked or ExpressionType.MultiplyAssign or
                ExpressionType.MultiplyAssignChecked or ExpressionType.DivideAssign or ExpressionType.ModuloAssign or
                ExpressionType.AndAssign or ExpressionType.OrAssign or ExpressionType.ExclusiveOrAssign or
                ExpressionType.LeftShiftAssign or ExpressionType.RightShiftAssign or ExpressionType.PowerAssign)
                First ??= "a state assignment";
            return base.VisitBinary(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType is ExpressionType.PreIncrementAssign or ExpressionType.PostIncrementAssign or
                ExpressionType.PreDecrementAssign or ExpressionType.PostDecrementAssign)
                First ??= "a state increment or decrement";
            return base.VisitUnary(node);
        }

        protected override MemberListBinding VisitMemberListBinding(MemberListBinding node)
        {
            var outer = _freshInitializerReceiver;
            _freshInitializerReceiver = FreshValueField(node.Member);
            try
            {
                if (!_freshInitializerReceiver) First ??= "a collection initializer on existing member state";
                return base.VisitMemberListBinding(node);
            }
            finally { _freshInitializerReceiver = outer; }
        }

        private bool FreshValueField(MemberInfo member) => _freshInitializerReceiver &&
            member is FieldInfo { IsStatic: false, FieldType.IsValueType: true };

        protected override MemberMemberBinding VisitMemberMemberBinding(MemberMemberBinding node)
        {
            // Member bindings read the receiver's member; constructing the outer object does not
            // prove that this member owns fresh storage (a getter can return shared state).
            var outer = _freshInitializerReceiver;
            _freshInitializerReceiver = FreshValueField(node.Member);
            try
            {
                if (!_freshInitializerReceiver && node.Bindings.Count > 0)
                    First ??= $"{node.Member.Name} (initializer on existing member state)";
                return base.VisitMemberMemberBinding(node);
            }
            finally { _freshInitializerReceiver = outer; }
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Note(node.Member.DeclaringType, node.Member.Name);
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if ((node.Method.Name.StartsWith("set_", StringComparison.Ordinal) ||
                 Mutators.Contains(node.Method.Name) && !ReturnsCollection(node.Method) && node.Object != null &&
                 (typeof(System.Collections.IEnumerable).IsAssignableFrom(node.Object.Type) || node.Object.Type == typeof(System.Text.StringBuilder))) &&
                node.Object is not NewExpression and not MemberInitExpression and not ListInitExpression)
                First ??= $"{node.Method.DeclaringType?.Name}.{node.Method.Name} (state mutation)";
            if (Mutators.Contains(node.Method.Name) && !ReturnsCollection(node.Method) && node.Method.IsStatic && node.Arguments.Count > 0 &&
                (node.Method.DeclaringType == typeof(Array) || node.Method.DeclaringType?.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(node.Arguments[0].Type) &&
                node.Arguments[0] is not NewExpression and not NewArrayExpression and not ListInitExpression)
                First ??= $"{node.Method.Name} (state mutation)";
            if (node.Method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                First ??= $"{node.Method.Name} (ref/out write)";
            Note(node.Method.DeclaringType, node.Method.Name);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            // A stream reader or writer is I/O only when it opens a path; over a stream in memory it is pure.
            var type = node.Constructor?.DeclaringType ?? node.Type;
            if (OpensAPath(type, node.Constructor))
                First ??= $"new {type.Name}(path)";
            Note(type, ".ctor");
            return base.VisitNew(node);
        }

        private void Note(Type? type, string member)
        {
            if (First == null && type != null && Impure(type, member))
                First = member == ".ctor" ? $"new {type.Name}(...)" : $"{type.Name}.{member}";
        }
    }

    private static readonly HashSet<string> Mutators =
    [
        "Add", "AddRange", "TryAdd", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "TryRemove", "Clear",
        "Insert", "InsertRange", "Sort", "Reverse", "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop", "TryPop",
        "UnionWith", "IntersectWith", "ExceptWith", "SymmetricExceptWith", "Append", "AppendLine"
    ];

    private static bool ReturnsCollection(MethodInfo method) =>
        (method.ReturnType == method.DeclaringType || method.IsStatic && method.GetParameters() is [{ ParameterType: var receiver }, ..] &&
         method.ReturnType == receiver) && method.ReturnType != typeof(System.Text.StringBuilder) && method.ReturnType.Name != "Builder";

    private static bool OpensAPath(Type type, MethodBase? constructor) =>
        (type == typeof(StreamReader) || type == typeof(StreamWriter)) &&
        constructor?.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(string);

    /// <summary>The first impure member a delegate's code uses, named as a build error names it; none when pure.</summary>
    private static class DelegateCode
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

        public static string? FirstImpure(Delegate? behavior)
        {
            var seen = new HashSet<MethodBase>();
            foreach (var each in Reachable(behavior, 0))
                if (Scan(each.Method, seen) is { } found)
                    return found;
            return null;
        }

        // The delegate and those its closure holds, as the engine wraps a user's lambda in its own.
        private static IEnumerable<Delegate> Reachable(Delegate? behavior, int depth)
        {
            if (behavior == null || depth > 4)
                yield break;
            foreach (var each in behavior.GetInvocationList())
            {
                yield return each;
                if (each.Target == null)
                    continue;
                foreach (var field in each.Target.GetType().GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (field.GetValue(each.Target) is Delegate held)
                        foreach (var inner in Reachable(held, depth + 1))
                            yield return inner;
            }
        }

        private static string? Scan(MethodBase method, HashSet<MethodBase> seen)
        {
            // The engine's own wrappers, and lambdas compiled from expression trees, which are checked as trees.
            if (method.DeclaringType?.Assembly == typeof(ExpressionRules).Assembly || !seen.Add(method))
                return null;
            byte[]? il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (InvalidOperationException) { il = null; }
            if (il == null)
                return null;
            var position = 0;
            while (position < il.Length)
            {
                var value = (short)il[position++];
                if (value == 0xFE && position < il.Length)
                    value = unchecked((short)(0xFE00 | il[position++]));
                if (!OpCodesByValue.TryGetValue(value, out var code))
                    return null;
                if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField &&
                    position + 4 <= il.Length && Resolve(method, BitConverter.ToInt32(il, position)) is { } member)
                {
                    if (member is MethodBase called && (called.Name.Contains(">b__", StringComparison.Ordinal) ||
                                                        called.Name.Contains(">g__", StringComparison.Ordinal)))
                    {
                        if (Scan(called, seen) is { } nested)
                            return nested;
                    }
                    else if (Named(member) is { } impure)
                        return impure;
                }
                position += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch when position + 4 <= il.Length =>
                        4 + 4 * BitConverter.ToInt32(il, position),
                    _ => 4
                };
            }
            return null;
        }

        private static MemberInfo? Resolve(MethodBase method, int token)
        {
            try
            {
                return method.Module.ResolveMember(token,
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (Exception error) when (error is ArgumentException or BadImageFormatException)
            {
                return null;
            }
        }

        private static string? Named(MemberInfo member)
        {
            var type = member.DeclaringType;
            if (type == null)
                return null;
            if (member is ConstructorInfo constructor)
                return OpensAPath(type, constructor) ? $"new {type.Name}(path)"
                    : Impure(type, ".ctor") ? $"new {type.Name}(...)" : null;
            var name = member is MethodInfo { IsSpecialName: true } accessor &&
                       (accessor.Name.StartsWith("get_", StringComparison.Ordinal) ||
                        accessor.Name.StartsWith("set_", StringComparison.Ordinal))
                ? accessor.Name[4..]
                : member.Name;
            return Impure(type, name) ? $"{type.Name}.{name}" : null;
        }
    }

    private static readonly HashSet<string> ClockMembers = ["Now", "UtcNow", "Today"];

    // Whole types whose every member reads the environment, varies per run, waits or does I/O.
    private static readonly HashSet<string> ImpureTypes =
    [
        "System.Random", "System.Environment", "System.IO.File", "System.IO.Directory", "System.IO.FileInfo",
        "System.IO.DirectoryInfo", "System.IO.FileStream", "System.Net.Http.HttpMessageInvoker",
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.Dns", "System.Data.IDbConnection",
        "System.Data.IDbCommand", "System.Data.IDataReader"
    ];

    private static bool Impure(Type type, string member) =>
        (type == typeof(DateTime) || type == typeof(DateTimeOffset)) && ClockMembers.Contains(member) ||
        type == typeof(Guid) && member == nameof(Guid.NewGuid) ||
        type == typeof(Task) && member == nameof(Task.Delay) ||
        type == typeof(Thread) && member == nameof(Thread.Sleep) ||
        type == typeof(TimeProvider) && member == nameof(TimeProvider.System) ||
        type.Namespace == "System.Net.Sockets" ||
        InHierarchy(type, ImpureTypes);

    // The type, its bases, or an interface it implements, so a call through IDbCommand or a derived client counts.
    private static bool InHierarchy(Type type, HashSet<string> names)
    {
        for (var each = type; each != null; each = each.BaseType)
            if (each.FullName != null && names.Contains(each.FullName))
                return true;
        return type.GetInterfaces().Any(each => each.FullName != null && names.Contains(each.FullName));
    }
}
