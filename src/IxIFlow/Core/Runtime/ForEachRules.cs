using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of <c>ForEach</c>: a handle is read only inside its body; items are read-only, so
/// no output writes through <c>ctx.Item(...)</c>; a result ends a body, or a timeout branch that leaves the
/// body; results stored with <c>Into</c> come from a result of exactly their type on every way out of the
/// body; and nothing in a body writes where its step stores its results, or anywhere the rules cannot
/// name. Checked when a workflow is built and when it is registered.
/// <para>
/// A timeout branch ends the scope its wait is in, so the timeout branch of a wait directly in a body
/// leaves the body, and so does the timeout branch of a wait directly in such a branch.
/// </para>
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
        ValidateBody(steps, new HashSet<ItemScope>(ReferenceEqualityComparer.Instance), [], leavesItem: false);

    // leavesItem: the body is a ForEach body, or a timeout branch that leaves one, so a result may end it.
    private static void ValidateBody(IReadOnlyList<WorkflowStep> body, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing, bool leavesItem)
    {
        for (var index = 0; index < body.Count; index++)
        {
            var step = body[index];
            if (step.StepType == WorkflowStepType.ForEachResult && (!leavesItem || index != body.Count - 1))
                throw WorkflowBuildError.Invalid(step, "A ForEach result must be the last step of a ForEach body, or of a timeout " +
                              "branch that leaves the body");
            foreach (var behavior in Behaviors(step))
                foreach (var handle in ItemCaptures.Of(behavior))
                    if (!inScope.Contains(handle))
                        throw WorkflowBuildError.Invalid(step, $"{handle} is used outside its ForEach body, in step '{step.Name}'");
            var outputs = Mappings(step).Where(mapping => mapping.Direction == PropertyMappingDirection.Output)
                .ToArray();
            if (outputs.Any(mapping => mapping.TargetPath?.StartsWith(MemberPath.ItemRoot, StringComparison.Ordinal) == true))
                throw WorkflowBuildError.Invalid(step, $"Step '{step.Name}' writes a ForEach item; items are read-only, so write " +
                              "workflow data instead");
            // In a body, a target must be named to show that it writes neither an item nor stored results.
            if (enclosing.Count > 0 && outputs.Any(mapping => mapping.TargetPath == null))
                throw WorkflowBuildError.Invalid(step, $"Step '{step.Name}' in a ForEach body writes a target that is not a chain of " +
                              "members and indexes, so it cannot be shown to write workflow data");
            foreach (var owner in enclosing.Where(owner => owner.ForEachIntoPath != null))
            {
                var writes = outputs.Select(mapping => mapping.TargetPath)
                    .Append(step.StepType == WorkflowStepType.ForEach ? step.ForEachIntoPath : null);
                if (writes.Any(path => MemberPath.Overlaps(path, owner.ForEachIntoPath!)))
                    throw WorkflowBuildError.Invalid(step, $"Step '{step.Name}' writes '{owner.ForEachIntoPath}', where its ForEach stores " +
                                  "its results");
            }
            if (step.StepType == WorkflowStepType.ForEach)
                ValidateForEach(step, inScope, enclosing);
            foreach (var nested in Bodies(step))
                ValidateBody(nested, inScope, enclosing, leavesItem: false);
            foreach (var scope in step.ChildScopes().Where(scope => scope.Kind == "timeout"))
                ValidateBody(scope.Steps, inScope, enclosing, leavesItem);
        }
    }

    // The bodies that end an item: the ForEach body and every timeout branch that leaves it.
    private static IEnumerable<IReadOnlyList<WorkflowStep>> Exits(IReadOnlyList<WorkflowStep> body) =>
        body.Where(step => step.TimeoutSteps.Count > 0)
            .SelectMany(step => step.ChildScopes().Where(scope => scope.Kind == "timeout"))
            .SelectMany(scope => Exits(scope.Steps))
            .Prepend(body);

    private static void ValidateForEach(WorkflowStep step, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing)
    {
        if (step.ForEachIntoExpression != null && step.ForEachInto == null)
            throw WorkflowBuildError.Invalid(step,
                $"A ForEach stores its results in a settable property of the workflow data, not of an item: {step.ForEachIntoExpression}");
        if (step.ForEachStoredResultType != null && step.ForEachStoredResultType != step.ResultType)
            throw WorkflowBuildError.Invalid(step,
                $"ForEach step '{step.Name}' decides on {step.ResultType?.Name} results, so it stores them");
        if (step.ForEachConditionResultType != null && step.ForEachConditionResultType != step.ResultType)
            throw WorkflowBuildError.Invalid(step,
                $"ForEach step '{step.Name}' stores {step.ResultType?.Name} results, so its condition reads them");
        if (step.ForEachItem == null || step.ForEachItems == null)
            throw WorkflowBuildError.Invalid(step, $"ForEach step '{step.Name}' has no item list");
        if (!step.ForEachParallel && step.ParallelJoinMode != ParallelJoinMode.WaitAll)
            throw WorkflowBuildError.Invalid(step, $"ForEach step '{step.Name}' has a join, which only a parallel ForEach has");
        if (!step.ForEachParallel && step.ParallelCombiners.Count > 0)
            throw WorkflowBuildError.Invalid(step, $"ForEach step '{step.Name}' chooses a combiner, which only a parallel ForEach's join uses");
        if (ForEachTransitions.StoresResults(step))
        {
            // Every way out of the body gives a result of the stored type.
            foreach (var exit in Exits(step.ForEachBodySteps))
                if (exit.LastOrDefault() is not { StepType: WorkflowStepType.ForEachResult } result ||
                    result.ResultType != step.ResultType)
                    throw WorkflowBuildError.Invalid(step, $"ForEach step '{step.Name}' stores {step.ResultType?.Name} results, so its body " +
                                  "and every timeout branch that leaves it must end with a result of that type");
        }
        var withItem = new HashSet<ItemScope>(inScope, ReferenceEqualityComparer.Instance) { step.ForEachItem };
        ValidateBody(step.ForEachBodySteps, withItem, [.. enclosing, step], leavesItem: true);
    }

    // The mappings a step runs with a workflow context: its own, and those of the compensations it declares.
    private static IEnumerable<PropertyMapping> Mappings(WorkflowStep step) =>
        step.InputMappings.Concat(step.OutputMappings)
            .Concat(step.StepMetadata.TryGetValue("CompensationActivities", out var value) &&
                    value is List<CompensationActivityInfo> compensations
                ? compensations.SelectMany(compensation => compensation.InputMappings.Concat(compensation.OutputMappings))
                : []);

    // The code a step runs with a workflow context, where a handle can be read.
    private static IEnumerable<Delegate?> Behaviors(WorkflowStep step) =>
        Mappings(step)
            .SelectMany(mapping => new Delegate?[] { mapping.SourceFunction, mapping.TargetAssignmentFunction })
            .Concat([
                step.CompiledCondition, step.OutcomeSelector, step.ParallelCompletionCondition,
                step.RequestSubject, step.ResultSelector, step.ForEachItems, step.ForEachInto
            ]);

    private static IEnumerable<IReadOnlyList<WorkflowStep>> Bodies(WorkflowStep step) =>
        step.ChildScopes().Where(scope => scope.Kind is not ("foreach" or "timeout"))
            .Select(scope => scope.Steps);

}

/// <summary>
/// The <c>ForEach</c> handles a delegate captures, found by walking its closures: compiler-generated
/// closure classes, compiled and interpreted expression closures, and nested delegates. Other objects
/// are not walked, so a handle kept inside one is found only when it is read.
/// </summary>
internal static class ItemCaptures
{
    private const int MaxDepth = 32;

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
        // A compiled expression keeps what it captured in a Closure; an interpreted one in its interpreter.
        var type = value.GetType();
        if (!type.IsDefined(typeof(CompilerGeneratedAttribute)) &&
            type.FullName != "System.Runtime.CompilerServices.Closure" &&
            type.Namespace != "System.Linq.Expressions.Interpreter")
            return;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Collect(field.GetValue(value), found, visited, depth + 1);
    }
}
