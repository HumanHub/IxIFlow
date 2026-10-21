using System.Collections;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class ChildScopeTests
{
    [Fact]
    public void EveryChildCollectionIsEnumeratedOnceIncludingEmptyBodies()
    {
        var bodies = new List<List<WorkflowStep>>();
        var step = PopulatedStep(bodies);
        var scopes = step.ChildScopes().ToArray();

        Assert.Equal(bodies.Count, scopes.Length);
        foreach (var body in bodies)
            Assert.Single(scopes, scope => ReferenceEquals(scope.Steps, body));
        Assert.Equal(["sequence", "then", "else", "loop", "foreach", "finally", "timeout", "catch"],
            scopes.Take(8).Select(scope => scope.Kind));
        Assert.All(scopes.Take(8), scope => Assert.Null(scope.Key));
        Assert.Equal([("branch", "0"), ("branch", "1"), ("outcome", "0"), ("outcome", "1")],
            scopes.Skip(8).Select(scope => (scope.Kind, scope.Key)));
        Assert.All(new WorkflowStep().ChildScopes(), scope => Assert.Empty(scope.Steps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloneCopiesAllChildBodiesAndRetainsScopeSegments(bool nullableMetadata)
    {
        var step = PopulatedStep([]);
        var shared = step.SequenceSteps;
        shared[0].ThenSteps = [new WorkflowStep()];
        step.ThenSteps = shared;
        step.ParallelBranches = [shared, shared, []];
        var value = nullableMetadata ? null : new List<string> { "outcome" };
        var sagaStep = new SagaStepInfo { StepId = shared[0].Id, ActivityType = typeof(object), Order = 3 };
        var sagaSteps = new List<SagaStepInfo> { sagaStep };
        step.OutcomeBranches =
        [
            new() { Value = value, IsDefault = false, Steps = shared, SagaSteps = sagaSteps },
            new() { Value = value, IsDefault = true, Steps = shared, SagaSteps = sagaSteps }
        ];
        var metadata = new List<string> { "shared metadata" };
        step.StepMetadata["shared"] = metadata;
        step.SourceLocation = nullableMetadata ? null : "clone.yaml:3";
        step.WorkflowName = nullableMetadata ? null : "child";
        step.DefaultPayload = nullableMetadata ? null : metadata;
        step.CompiledCondition = nullableMetadata ? null : _ => true;
        step.ParallelCombiners["Total"] = typeof(object);
        var mapping = new PropertyMapping { TargetProperty = "Total", SourceFunction = _ => metadata };
        step.InputMappings = [mapping];
        step.OutputMappings = [mapping];

        var original = step.ChildScopes().ToArray();
        var clone = step.Clone();
        var copied = clone.ChildScopes().ToArray();

        Assert.NotSame(step, clone);
        Assert.Equal(original.Length, copied.Length);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Kind, copied[index].Kind);
            Assert.Equal(original[index].Key, copied[index].Key);
            Assert.NotSame(original[index].Steps, copied[index].Steps);
            Assert.Equal(original[index].Steps.Select(child => child.Id), copied[index].Steps.Select(child => child.Id));
            foreach (var (source, target) in original[index].Steps.Zip(copied[index].Steps))
                Assert.NotSame(source, target);
        }

        // Main clones each occurrence independently, even when the source body is shared.
        for (var index = 0; index < copied.Length; index++)
            for (var other = index + 1; other < copied.Length; other++)
            {
                Assert.NotSame(copied[index].Steps, copied[other].Steps);
                foreach (var child in copied[index].Steps)
                    Assert.DoesNotContain(copied[other].Steps, candidate => ReferenceEquals(child, candidate));
            }
        foreach (var scope in copied.Where(scope => scope.Steps.Count > 0 && scope.Steps[0].Id == shared[0].Id))
        {
            Assert.NotSame(shared[0].ThenSteps, scope.Steps[0].ThenSteps);
            Assert.NotSame(shared[0].ThenSteps[0], Assert.Single(scope.Steps[0].ThenSteps));
        }

        Assert.NotSame(step.ParallelBranches, clone.ParallelBranches);
        Assert.NotSame(step.OutcomeBranches, clone.OutcomeBranches);
        for (var index = 0; index < step.OutcomeBranches.Count; index++)
        {
            var source = step.OutcomeBranches[index];
            var target = clone.OutcomeBranches[index];
            Assert.NotSame(source, target);
            Assert.Same(source.Value, target.Value);
            Assert.Equal(source.IsDefault, target.IsDefault);
            Assert.NotSame(source.SagaSteps, target.SagaSteps);
            Assert.Same(sagaStep, Assert.Single(target.SagaSteps));
        }
        Assert.NotSame(clone.OutcomeBranches[0].SagaSteps, clone.OutcomeBranches[1].SagaSteps);
        Assert.NotSame(step.StepMetadata, clone.StepMetadata);
        Assert.Same(metadata, clone.StepMetadata["shared"]);
        Assert.Equal(step.SourceLocation, clone.SourceLocation);
        Assert.Equal(step.WorkflowName, clone.WorkflowName);
        Assert.Same(step.DefaultPayload, clone.DefaultPayload);
        Assert.Same(step.CompiledCondition, clone.CompiledCondition);
        Assert.NotSame(step.ParallelCombiners, clone.ParallelCombiners);
        Assert.Equal(typeof(object), clone.ParallelCombiners["Total"]);
        Assert.NotSame(step.InputMappings, clone.InputMappings);
        Assert.NotSame(step.OutputMappings, clone.OutputMappings);
        var input = Assert.Single(clone.InputMappings);
        var output = Assert.Single(clone.OutputMappings);
        Assert.NotSame(mapping, input);
        Assert.NotSame(mapping, output);
        Assert.NotSame(input, output);
        Assert.Equal(mapping.TargetProperty, input.TargetProperty);
        Assert.Equal(mapping.TargetProperty, output.TargetProperty);
        Assert.Same(mapping.SourceFunction, input.SourceFunction);
        Assert.Same(mapping.SourceFunction, output.SourceFunction);
    }

    [Theory]
    [InlineData(nameof(WorkflowStep.SequenceSteps))]
    [InlineData(nameof(WorkflowStep.ThenSteps))]
    [InlineData(nameof(WorkflowStep.ElseSteps))]
    [InlineData(nameof(WorkflowStep.LoopBodySteps))]
    [InlineData(nameof(WorkflowStep.ForEachBodySteps))]
    [InlineData(nameof(WorkflowStep.FinallySteps))]
    [InlineData(nameof(WorkflowStep.TimeoutSteps))]
    [InlineData(nameof(WorkflowStep.CatchBlocks))]
    [InlineData(nameof(WorkflowStep.ParallelBranches))]
    [InlineData(nameof(WorkflowStep.OutcomeBranches))]
    public void CloneRejectsNullChildCollectionsLikeMain(string property)
    {
        var step = new WorkflowStep();
        typeof(WorkflowStep).GetProperty(property)!.SetValue(step, null);

        Assert.Throws<ArgumentNullException>(() => step.Clone());
    }

    [Fact]
    public void CloneRejectsNullBranchBodiesAndSagaMetadataLikeMain()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkflowStep { ParallelBranches = [null!] }.Clone());
        Assert.Throws<ArgumentNullException>(() => new WorkflowStep { OutcomeBranches = [new() { Steps = null! }] }.Clone());
        Assert.Throws<ArgumentNullException>(() => new WorkflowStep { OutcomeBranches = [new() { SagaSteps = null! }] }.Clone());
        Assert.Throws<NullReferenceException>(() => new WorkflowStep { OutcomeBranches = [null!] }.Clone());
    }

    [Fact]
    public void FlattenPreservesItsDiagnosticOrder()
    {
        var step = PopulatedStep([]);
        foreach (var scope in step.ChildScopes())
            foreach (var child in scope.Steps)
                child.Id = scope.Kind + scope.Key;

        Assert.Equal([step.Id, "sequence", "then", "else", "loop", "finally", "catch", "timeout", "foreach", "branch0", "outcome0", "outcome1"],
            ParallelRules.Flatten([step]).Select(child => child.Id));
    }

    [Fact]
    public void RequestDiagnosticsStillVisitTimeoutsBeforeCatchesAndItemBodies()
    {
        var step = PopulatedStep([]);
        foreach (var scope in step.ChildScopes().Where(scope => scope.Kind is "timeout" or "catch" or "foreach"))
            foreach (var child in scope.Steps)
            {
                child.RequestName = scope.Kind;
                child.ReplyType = typeof(object);
            }

        var error = Assert.Throws<WorkflowEngineException>(() => RequestPairing.Pair([step]));
        Assert.Contains("Request 'timeout' needs exactly one WaitFor", error.Message);
    }

    private static WorkflowStep PopulatedStep(List<List<WorkflowStep>> bodies)
    {
        var step = new WorkflowStep();
        Populate(step);
        return step;

        void Populate(object owner)
        {
            foreach (var property in owner.GetType().GetProperties().Where(property => ContainsSteps(property.PropertyType)))
                property.SetValue(owner, Make(property.PropertyType));
        }

        object Make(Type type)
        {
            if (type == typeof(WorkflowStep)) return new WorkflowStep();
            var value = Activator.CreateInstance(type)!;
            if (value is List<WorkflowStep> body)
            {
                bodies.Add(body);
                body.Add(new WorkflowStep());
            }
            else if (value is IList list)
            {
                var element = type.GetGenericArguments()[0];
                list.Add(Make(element));
                var empty = Activator.CreateInstance(element)!;
                list.Add(empty);
                if (empty is List<WorkflowStep> emptyBody) bodies.Add(emptyBody);
                else Populate(empty);
            }
            else Populate(value);
            return value;
        }
    }

    private static bool ContainsSteps(Type type) => type == typeof(WorkflowStep) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? ContainsSteps(type.GetGenericArguments()[0])
            : type.IsClass && type.Assembly == typeof(WorkflowStep).Assembly &&
                type.GetProperties().Any(property => ContainsSteps(property.PropertyType)));
}
