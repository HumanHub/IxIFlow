using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The purity rule for the expressions a workflow is built from (D-07, D-42). Conditions and mappings are evaluated
/// again after a recovery, so they may not read the clock or the environment, make values that differ per run, wait,
/// or do I/O. An expression that uses such a member fails the build, naming the step and the member. A helper method
/// the expression calls is not looked into; the analyzer covers what it can see.
/// </summary>
internal static class ExpressionRules
{
    /// <summary>Checks the rule and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        foreach (var step in ParallelRules.Flatten(steps))
        {
            Check(step, step.ConditionExpression);
            Check(step, step.ForEachIntoExpression);
            foreach (var mapping in step.InputMappings.Concat(step.OutputMappings))
                Check(step, mapping.Expression);
            if (step.StepMetadata.TryGetValue("CompensationActivities", out var value) &&
                value is List<CompensationActivityInfo> compensations)
                foreach (var mapping in compensations.SelectMany(item => item.InputMappings.Concat(item.OutputMappings)))
                    Check(step, mapping.Expression);
        }
        return steps;
    }

    private static void Check(WorkflowStep step, LambdaExpression? expression)
    {
        if (expression == null)
            return;
        var found = new ImpureMembers();
        found.Visit(expression);
        if (found.First is { } member)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Step '{step.Name}' uses {member} in an expression. Expressions are evaluated again after a " +
                "recovery, so they may not read the clock or the environment, make values that differ per run, " +
                "wait, or do I/O (D-07); do that in an activity and map its output");
    }

    private sealed class ImpureMembers : ExpressionVisitor
    {
        public string? First { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            Note(node.Member.DeclaringType, node.Member.Name);
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Note(node.Method.DeclaringType, node.Method.Name);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            Note(node.Constructor?.DeclaringType ?? node.Type, ".ctor");
            return base.VisitNew(node);
        }

        private void Note(Type? type, string member)
        {
            if (First == null && type != null && Impure(type, member))
                First = member == ".ctor" ? $"new {type.Name}(...)" : $"{type.Name}.{member}";
        }
    }

    private static readonly HashSet<string> ClockMembers = ["Now", "UtcNow", "Today"];

    // Whole types whose every member reads the environment, varies per run, waits or does I/O.
    private static readonly HashSet<string> ImpureTypes =
    [
        "System.Random", "System.Environment", "System.IO.File", "System.IO.Directory", "System.IO.FileInfo",
        "System.IO.DirectoryInfo", "System.IO.FileStream", "System.IO.StreamReader", "System.IO.StreamWriter",
        "System.Net.Http.HttpClient", "System.Net.WebClient", "System.Net.Dns", "System.Data.Common.DbConnection",
        "System.Data.Common.DbCommand", "System.Data.Common.DbDataReader"
    ];

    private static bool Impure(Type type, string member) =>
        (type == typeof(DateTime) || type == typeof(DateTimeOffset)) && ClockMembers.Contains(member) ||
        type == typeof(Guid) && member == nameof(Guid.NewGuid) ||
        type == typeof(Task) && member == nameof(Task.Delay) ||
        type == typeof(Thread) && member == nameof(Thread.Sleep) ||
        type == typeof(TimeProvider) && member == nameof(TimeProvider.System) ||
        type.Namespace == "System.Net.Sockets" ||
        InHierarchy(type, ImpureTypes);

    private static bool InHierarchy(Type? type, HashSet<string> names)
    {
        for (; type != null; type = type.BaseType)
            if (type.FullName != null && names.Contains(type.FullName))
                return true;
        return false;
    }
}
