using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of parallel data (D-14, spec E-04, E-05): under <c>WaitAll</c> or
/// <c>WaitConditionally</c>, two branches whose outputs write the same property, or a property and one
/// inside it, are a build error unless a combiner on that property or one above it merges them. A write
/// into a value the join merges whole, such as a list, writes that value; a write into a dictionary is
/// merged per key by the join. An output whose target the rules cannot name, or that the serializer does
/// not see, is refused in such a branch. Under <c>WaitAny</c> only one branch is merged, so it is exempt;
/// joins nested in its branches are still checked. Every combiner declared on the data type must fit its
/// property, and a data type with parallel steps must be one whose branch copies can be merged.
/// </summary>
internal static class ParallelRules
{
    /// <summary>Checks the rule and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        Validate(steps);
        return steps;
    }

    public static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        var all = Flatten(steps).ToList();
        var parallel = all.Where(step => step.StepType == WorkflowStepType.Parallel ||
                                         step is { StepType: WorkflowStepType.ForEach, ForEachParallel: true }).ToList();
        foreach (var dataType in all.Select(step => step.WorkflowDataType).OfType<Type>().Distinct())
            BranchData.Validate(dataType, branches: parallel.Any(step => step.WorkflowDataType == dataType));
        foreach (var step in parallel)
            if (step.ParallelJoinMode == ParallelJoinMode.WaitAny)
            {
                // One branch is merged, so branches may write the same data, but every write must reach it.
                var bodies = step.StepType == WorkflowStepType.Parallel ? step.ParallelBranches : [step.ForEachBodySteps];
                foreach (var body in bodies)
                    _ = Writes(step, body).ToList();
            }
            else if (step.StepType == WorkflowStepType.Parallel)
                Check(step);
            else
                CheckItems(step);
    }

    // Every item of a parallel ForEach runs the same body, so any write it makes, other than the results
    // the engine stores, could be made by more than one item and needs a combiner (D-34).
    private static void CheckItems(WorkflowStep forEach)
    {
        foreach (var path in Distinct(Writes(forEach, forEach.ForEachBodySteps)))
            if (!Combined(forEach.WorkflowDataType, path, forEach.ParallelCombiners))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Items of parallel ForEach step '{forEach.Name}' all write '{string.Join('.', path)}' and it " +
                    "has no combiner; give the property a combiner, keep per-item values in the results, or join " +
                    "with WaitAny");
    }

    private static void Check(WorkflowStep parallel)
    {
        var writes = parallel.ParallelBranches.Select(branch => Distinct(Writes(parallel, branch))).ToList();
        for (var first = 0; first < writes.Count; first++)
            for (var second = first + 1; second < writes.Count; second++)
                foreach (var one in writes[first])
                    foreach (var other in writes[second].Where(other => Overlaps(one, other)))
                    {
                        var shorter = one.Count <= other.Count ? one : other;
                        if (!Combined(parallel.WorkflowDataType, shorter, parallel.ParallelCombiners))
                            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                                $"Branches {first + 1} and {second + 1} of parallel step '{parallel.Name}' both " +
                                $"write '{string.Join('.', shorter)}' and it has no combiner; give the property a " +
                                "combiner, write separate properties, or join with WaitAny");
                    }
    }

    // The workflow data paths a branch body can write, including nested bodies and joins, as the JSON paths
    // the join merges them at (BranchData.MergePath); a write the join merges per key is left out (null).
    private static IEnumerable<List<string>?> Writes(WorkflowStep parallel, IReadOnlyList<WorkflowStep> body)
    {
        var dataType = parallel.WorkflowDataType;
        foreach (var step in Flatten(body))
        {
            foreach (var mapping in Mappings(step).Where(mapping => mapping.Direction == PropertyMappingDirection.Output))
            {
                if (mapping.TargetPath == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"Step '{step.Name}' in a branch of parallel step '{parallel.Name}' writes a target that is " +
                        "not a chain of members, indexes and casts, so it cannot be shown not to conflict with " +
                        "another branch; write a named property, or join with WaitAny");
                if (dataType != null)
                    yield return BranchData.MergePath(dataType, mapping.TargetPath,
                        () => $"Step '{step.Name}' in a branch of parallel step '{parallel.Name}'");
            }
            if (step.ForEachIntoPath != null && dataType != null)
                yield return BranchData.MergePath(dataType, step.ForEachIntoPath,
                    () => $"ForEach step '{step.Name}' in a branch of parallel step '{parallel.Name}'");
        }
    }

    private static List<List<string>> Distinct(IEnumerable<List<string>?> paths) =>
        paths.OfType<List<string>>().DistinctBy(BranchData.Key).ToList();

    // One path contains the other.
    private static bool Overlaps(List<string> one, List<string> other) =>
        one.Count <= other.Count
            ? other.Take(one.Count).SequenceEqual(one)
            : one.Take(other.Count).SequenceEqual(other);

    // True when a property on the JSON path has a combiner: one the step chooses or one its type declares.
    private static bool Combined(Type? dataType, List<string> path, IReadOnlyDictionary<string, Type> overrides) =>
        dataType != null && Enumerable.Range(1, path.Count)
            .Any(length => BranchData.CombinerAt(dataType, path.Take(length).ToList(), overrides) != null);

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

    /// <summary>The combiners a data type declares where a join uses them, for the fingerprint (spec E-05).</summary>
    public static string CombinerSignature(Type? dataType) => BranchData.CombinerSignature(dataType);
}
