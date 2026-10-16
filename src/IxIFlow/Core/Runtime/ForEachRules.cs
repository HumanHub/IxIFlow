using System.Reflection;
using System.Runtime.CompilerServices;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of <c>ForEach</c> (D-34): a handle is read only inside its body, a result ends a
/// body, results stored with <c>Into</c> come from a result of their type, and a body never writes the
/// list its step stores its results in. Checked when a workflow is built and when it is registered.
/// </summary>
internal static class ForEachRules
{
    /// <summary>Checks the rules and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Paired(List<WorkflowStep> steps)
    {
        Validate(steps);
        return steps;
    }

    public static void Validate(IReadOnlyList<WorkflowStep> steps) =>
        ValidateBody(steps, new HashSet<ItemScope>(ReferenceEqualityComparer.Instance), [], isForEachBody: false);

    private static void ValidateBody(IReadOnlyList<WorkflowStep> body, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing, bool isForEachBody)
    {
        for (var index = 0; index < body.Count; index++)
        {
            var step = body[index];
            if (step.StepType == WorkflowStepType.ForEachResult && (!isForEachBody || index != body.Count - 1))
                throw Invalid("A ForEach result must be the last step of a ForEach body");
            foreach (var behavior in Behaviors(step))
                foreach (var handle in ItemCaptures.Of(behavior))
                    if (!inScope.Contains(handle))
                        throw Invalid($"{handle} is used outside its ForEach body, in step '{step.Name}'");
            foreach (var owner in enclosing.Where(owner => owner.ForEachIntoPath != null))
                if (step.OutputMappings.Any(mapping => MemberPath.Writes(mapping.TargetPath, owner.ForEachIntoPath!)))
                    throw Invalid($"Step '{step.Name}' writes '{owner.ForEachIntoPath}', where its ForEach stores " +
                                  "its results");
            if (step.StepType == WorkflowStepType.ForEach)
                ValidateForEach(step, inScope, enclosing);
            foreach (var nested in Bodies(step))
                ValidateBody(nested, inScope, enclosing, isForEachBody: false);
        }
    }

    private static void ValidateForEach(WorkflowStep step, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing)
    {
        if (step.ForEachItem == null || step.ForEachItems == null)
            throw Invalid($"ForEach step '{step.Name}' has no item list");
        var result = step.ForEachBodySteps.LastOrDefault() is { StepType: WorkflowStepType.ForEachResult } last
            ? last : null;
        if (step.ForEachInto != null && (result == null || !step.ResultType!.IsAssignableFrom(result.ResultType)))
            throw Invalid($"ForEach step '{step.Name}' stores {step.ResultType?.Name} results, so its body must " +
                          $"end with a result of that type");
        var withItem = new HashSet<ItemScope>(inScope, ReferenceEqualityComparer.Instance) { step.ForEachItem };
        ValidateBody(step.ForEachBodySteps, withItem, [.. enclosing, step], isForEachBody: true);
    }

    // The code a step runs with a workflow context, where a handle can be read.
    private static IEnumerable<Delegate?> Behaviors(WorkflowStep step) =>
        step.InputMappings.Concat(step.OutputMappings)
            .SelectMany(mapping => new Delegate?[] { mapping.SourceFunction, mapping.TargetAssignmentFunction })
            .Concat([
                step.CompiledCondition, step.OutcomeSelector, step.ParallelCompletionCondition,
                step.RequestSubject, step.ResultSelector, step.ForEachItems, step.ForEachInto
            ]);

    private static IEnumerable<IReadOnlyList<WorkflowStep>> Bodies(WorkflowStep step) =>
        new IReadOnlyList<WorkflowStep>[]
            {
                step.SequenceSteps, step.ThenSteps, step.ElseSteps, step.LoopBodySteps, step.FinallySteps,
                step.TimeoutSteps, step.CatchBlocks
            }
            .Concat(step.ParallelBranches)
            .Concat(step.OutcomeBranches.Select(branch => branch.Steps));

    private static WorkflowEngineException Invalid(string message) =>
        new(WorkflowErrorKind.InvalidDefinition, message);
}

/// <summary>
/// The <c>ForEach</c> handles a delegate captures, found by walking its closures: compiler-generated
/// closure classes, compiled expression closures and nested delegates. Other objects are not walked.
/// </summary>
internal static class ItemCaptures
{
    private const int MaxDepth = 12;

    public static IReadOnlyCollection<ItemScope> Of(Delegate? behavior)
    {
        var found = new HashSet<ItemScope>(ReferenceEqualityComparer.Instance);
        if (behavior != null)
            Collect(behavior, found, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return found;
    }

    private static void Collect(object? value, HashSet<ItemScope> found, HashSet<object> visited, int depth)
    {
        if (value == null || depth > MaxDepth || value is string or Type || value.GetType().IsPrimitive ||
            value.GetType().IsEnum)
            return;
        if (value is ItemScope handle)
        {
            found.Add(handle);
            return;
        }
        if (!visited.Add(value))
            return;
        switch (value)
        {
            case Delegate behavior:
                foreach (var invocation in behavior.GetInvocationList())
                    Collect(invocation.Target, found, visited, depth + 1);
                return;
            case IStrongBox box:
                Collect(box.Value, found, visited, depth + 1);
                return;
            case object?[] array:
                foreach (var element in array)
                    Collect(element, found, visited, depth + 1);
                return;
        }
        // A compiled expression's closure keeps what the expression captured in its fields.
        var type = value.GetType();
        if (!type.IsDefined(typeof(CompilerGeneratedAttribute)) &&
            type.FullName != "System.Runtime.CompilerServices.Closure")
            return;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Collect(field.GetValue(value), found, visited, depth + 1);
    }
}
