using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of <c>ForEach</c> (D-34): a handle is read only inside its body; items are read-only, so
/// no output writes through <c>ctx.Item(...)</c>; a result ends a body, or the timeout branch of a wait that
/// leaves the body; results stored with <c>Into</c> come from a result of exactly their type on every way
/// out of the body; and nothing in a body writes where its step stores its results. Checked when a workflow
/// is built and when it is registered.
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
        ValidateBody(steps, new HashSet<ItemScope>(ReferenceEqualityComparer.Instance), [], resultEndsIt: false,
            isForEachBody: false);

    private static void ValidateBody(IReadOnlyList<WorkflowStep> body, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing, bool resultEndsIt, bool isForEachBody)
    {
        for (var index = 0; index < body.Count; index++)
        {
            var step = body[index];
            if (step.StepType == WorkflowStepType.ForEachResult && (!resultEndsIt || index != body.Count - 1))
                throw Invalid("A ForEach result must be the last step of a ForEach body, or of the timeout " +
                              "branch of a wait in that body");
            foreach (var behavior in Behaviors(step))
                foreach (var handle in ItemCaptures.Of(behavior))
                    if (!inScope.Contains(handle))
                        throw Invalid($"{handle} is used outside its ForEach body, in step '{step.Name}'");
            foreach (var mapping in Mappings(step).Where(mapping => mapping.Direction == PropertyMappingDirection.Output))
                if (mapping.TargetPath?.StartsWith(MemberPath.ItemRoot, StringComparison.Ordinal) == true)
                    throw Invalid($"Step '{step.Name}' writes a ForEach item; items are read-only, so write " +
                                  "workflow data instead");
            foreach (var owner in enclosing.Where(owner => owner.ForEachIntoPath != null))
            {
                var writes = Mappings(step).Where(mapping => mapping.Direction == PropertyMappingDirection.Output)
                    .Select(mapping => mapping.TargetPath)
                    .Append(step.StepType == WorkflowStepType.ForEach ? step.ForEachIntoPath : null);
                if (writes.Any(path => MemberPath.Overlaps(path, owner.ForEachIntoPath!)))
                    throw Invalid($"Step '{step.Name}' writes '{owner.ForEachIntoPath}', where its ForEach stores " +
                                  "its results");
            }
            if (step.StepType == WorkflowStepType.ForEach)
                ValidateForEach(step, inScope, enclosing);
            foreach (var nested in Bodies(step))
                ValidateBody(nested, inScope, enclosing, resultEndsIt: false, isForEachBody: false);
            // The timeout branch of a wait directly in a ForEach body leaves the body, so it may end with a result.
            ValidateBody(step.TimeoutSteps, inScope, enclosing, resultEndsIt: isForEachBody, isForEachBody: false);
        }
    }

    private static void ValidateForEach(WorkflowStep step, HashSet<ItemScope> inScope,
        IReadOnlyList<WorkflowStep> enclosing)
    {
        if (step.ForEachItem == null || step.ForEachItems == null)
            throw Invalid($"ForEach step '{step.Name}' has no item list");
        if (step.ForEachInto != null)
        {
            // Every way out of the body gives a result of the stored type: its end, and every timeout branch
            // that leaves it.
            var exits = step.ForEachBodySteps.Where(nested => nested.TimeoutSteps.Count > 0)
                .Select(nested => (IReadOnlyList<WorkflowStep>)nested.TimeoutSteps)
                .Prepend(step.ForEachBodySteps);
            foreach (var exit in exits)
                if (exit.LastOrDefault() is not { StepType: WorkflowStepType.ForEachResult } result ||
                    result.ResultType != step.ResultType)
                    throw Invalid($"ForEach step '{step.Name}' stores {step.ResultType?.Name} results, so its body " +
                                  "and every timeout branch that leaves it must end with a result of that type");
        }
        var withItem = new HashSet<ItemScope>(inScope, ReferenceEqualityComparer.Instance) { step.ForEachItem };
        ValidateBody(step.ForEachBodySteps, withItem, [.. enclosing, step], resultEndsIt: true, isForEachBody: true);
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
        new IReadOnlyList<WorkflowStep>[]
            {
                step.SequenceSteps, step.ThenSteps, step.ElseSteps, step.LoopBodySteps, step.FinallySteps,
                step.CatchBlocks
            }
            .Concat(step.ParallelBranches)
            .Concat(step.OutcomeBranches.Select(branch => branch.Steps));

    private static WorkflowEngineException Invalid(string message) =>
        new(WorkflowErrorKind.InvalidDefinition, message);
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
