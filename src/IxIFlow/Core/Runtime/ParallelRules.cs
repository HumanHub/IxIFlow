using System.Collections;
using System.Reflection;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rule of parallel data (D-14, spec E-04): under <c>WaitAll</c> or <c>WaitConditionally</c>,
/// two branches whose outputs write the same property, or a property and one inside it, are a build
/// error unless a combiner on that property merges them. Under <c>WaitAny</c> only one branch is merged,
/// so it is exempt; joins nested in its branches are still checked. Outputs the rules cannot name are left
/// to the join, which refuses the conflict when it happens.
/// </summary>
internal static class ParallelRules
{
    private const string Root = "WorkflowData";

    /// <summary>Checks the rule and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        Validate(steps);
        return steps;
    }

    public static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in Flatten(steps))
            if (step.StepType == WorkflowStepType.Parallel && step.ParallelJoinMode != ParallelJoinMode.WaitAny)
                Check(step);
    }

    private static void Check(WorkflowStep parallel)
    {
        var writes = parallel.ParallelBranches.Select(branch => Writes(branch).Distinct().ToList()).ToList();
        for (var first = 0; first < writes.Count; first++)
            for (var second = first + 1; second < writes.Count; second++)
                foreach (var one in writes[first])
                    foreach (var other in writes[second].Where(other => MemberPath.Overlaps(one, other)))
                    {
                        var shorter = one.Length <= other.Length ? one : other;
                        if (!Combined(parallel.WorkflowDataType, shorter, parallel.ParallelCombiners))
                            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                                $"Branches {first + 1} and {second + 1} of parallel step '{parallel.Name}' both " +
                                $"write '{shorter}' and it has no combiner; give the property a combiner, write " +
                                "separate properties, or join with WaitAny");
                    }
    }

    // The workflow data paths a branch body can write, including nested bodies and joins.
    private static IEnumerable<string> Writes(IReadOnlyList<WorkflowStep> body) =>
        Flatten(body).SelectMany(step => Mappings(step)
                .Where(mapping => mapping.Direction == PropertyMappingDirection.Output)
                .Select(mapping => mapping.TargetPath)
                .Append(step.ForEachIntoPath))
            .Where(path => path != null && path.StartsWith(Root + ".", StringComparison.Ordinal))
            .Select(path => path!);

    private static IEnumerable<PropertyMapping> Mappings(WorkflowStep step) =>
        step.OutputMappings.Concat(step.StepMetadata.TryGetValue("CompensationActivities", out var value) &&
                                   value is List<CompensationActivityInfo> compensations
            ? compensations.SelectMany(compensation => compensation.OutputMappings)
            : []);

    private static IEnumerable<WorkflowStep> Flatten(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            var nested = new IReadOnlyList<WorkflowStep>[]
                {
                    step.SequenceSteps, step.ThenSteps, step.ElseSteps, step.LoopBodySteps, step.FinallySteps,
                    step.CatchBlocks, step.TimeoutSteps, step.ForEachBodySteps
                }
                .Concat(step.ParallelBranches)
                .Concat(step.OutcomeBranches.Select(branch => branch.Steps));
            foreach (var body in nested)
                foreach (var inner in Flatten(body))
                    yield return inner;
        }
    }

    // True when a property on the path, such as WorkflowData.Order.Total, has a combiner: one the step
    // chooses, by JSON path, or one its type declares.
    private static bool Combined(Type? dataType, string path, IReadOnlyDictionary<string, Type> overrides)
    {
        var current = dataType;
        var jsonPath = new List<string>();
        foreach (var segment in path.Split('.').Skip(1))
        {
            if (current == null)
                return false;
            var name = segment.Replace("[]", "", StringComparison.Ordinal);
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                return false;
            jsonPath.Add(property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name
                         ?? property.Name);
            if (property.GetCustomAttribute<CombineAttribute>() != null ||
                overrides.ContainsKey(string.Join('.', jsonPath)))
                return true;
            // A list is merged whole (spec E-03), so nothing inside one has a combiner of its own.
            if (segment.EndsWith("[]", StringComparison.Ordinal))
                return false;
            current = property.PropertyType;
        }
        return false;
    }

    /// <summary>The combiners a data type declares, for the fingerprint (spec E-05).</summary>
    public static string CombinerSignature(Type? dataType)
    {
        var found = new List<string>();
        Collect(dataType, "", found, new HashSet<Type>(), 0);
        return string.Join(';', found);
    }

    private static void Collect(Type? type, string path, List<string> found, HashSet<Type> visiting, int depth)
    {
        if (type == null || depth > 8 || type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.IsGenericType ||
            !visiting.Add(type))
            return;
        try
        {
            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                foreach (var argument in type.GetGenericArguments())
                    Collect(argument, path + "[]", found, visiting, depth + 1);
                return;
            }
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var at = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (property.GetCustomAttribute<CombineAttribute>() is { } combine)
                    found.Add(at + "=" + WorkflowTypeIdentity.StableName(combine.CombinerType));
                Collect(property.PropertyType, at, found, visiting, depth + 1);
            }
        }
        finally
        {
            visiting.Remove(type);
        }
    }
}
