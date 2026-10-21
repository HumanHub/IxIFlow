using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed partial class ChildScopeBaselineTests
{
    [Fact]
    public void CatalogMatchesMainBeforeChildScopes()
    {
        var snapshots = Fixtures().Select(definition =>
        {
            var catalog = new WorkflowScopeCatalog(definition);
            var scopes = (Dictionary<string, IReadOnlyList<WorkflowStep>>)typeof(WorkflowScopeCatalog)
                .GetField("_scopes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
            Assert.Equal(catalog.AllSteps.Select(pair => pair.Key), catalog.StepTable.Select(entry => entry.CatalogId));
            return new Snapshot(definition.Name, catalog.StructureHash, scopes.Keys.ToArray(),
                catalog.AllSteps.Select(pair => pair.Key).ToArray());
        }).ToArray();
        var expected = JsonSerializer.Deserialize<Snapshot[]>(MainBaseline)!;
        Assert.Equal(expected.Length, snapshots.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, snapshots[index].Name);
            Assert.Equal(expected[index].Hash, snapshots[index].Hash);
            Assert.Equal(expected[index].ScopeIds, snapshots[index].ScopeIds);
            Assert.Equal(expected[index].StepIds, snapshots[index].StepIds);
        }
    }

    [Fact]
    public void PinnedTimeoutBodyLeavesItsParentScope()
    {
        var catalog = new WorkflowScopeCatalog(Fixtures().Last());
        var (stepId, step) = Assert.Single(catalog.AllSteps, pair => pair.Value.TimeoutSteps.Count > 0);
        var parent = new ScopePosition
        {
            ScopeId = stepId[..stepId.LastIndexOf('/')],
            NextStepIndex = int.Parse(stepId[(stepId.LastIndexOf('/') + 1)..])
        };
        var continuation = new ContinuationState { Status = ContinuationStatus.Waiting, Stack = [parent] };
        var wait = new WaitState { ContinuationId = continuation.Id, StepId = stepId };
        var checkpoint = new ExecutionCheckpoint { Continuations = [continuation], Waits = [wait] };
        checkpoint.Inbox.Due([wait.Id]);
        checkpoint.Trace.Configure(new WorkflowInstance { TracingEnabled = false }, checkpoint,
            new ExecutionLimits(), TimeProvider.System, catalog);
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.True(new ContinuationTransitions(services, TimeProvider.System).ProcessDueWaits(checkpoint, catalog));

        Assert.Equal(2, continuation.Stack.Count);
        Assert.Same(parent, continuation.Stack[0]);
        var timeout = continuation.Stack[1];
        Assert.Equal(catalog.TimeoutScope(step), timeout.ScopeId);
        Assert.Same(step.TimeoutSteps, catalog.Steps(timeout.ScopeId));
        Assert.True(timeout.ExitParentScopeOnExit);
        Assert.True(timeout.RestorePreviousOnExit);
    }

    private static IEnumerable<WorkflowDefinition> Fixtures()
    {
        yield return Definition("Empty", []);
        yield return Definition("Every step", Enum.GetValues<WorkflowStepType>().Select(Step).ToList());

        var request = Step(WorkflowStepType.Activity);
        request.RequestName = "approval";
        request.ReplyType = typeof(string);
        request.StepMetadata["IsSagaStep"] = true;
        request.StepMetadata["CompensationActivities"] = new List<CompensationActivityInfo>
        {
            new() { ActivityType = typeof(object) },
            new() { ActivityType = typeof(string), PreviousChainActivityType = typeof(object) }
        };
        var outcomeSource = Step(WorkflowStepType.Activity);
        outcomeSource.StepMetadata["IsSagaStep"] = true;
        var saga = Step(WorkflowStepType.Saga);
        saga.SequenceSteps = [request, Step(WorkflowStepType.SuspendResume)];
        saga.SequenceSteps[1].ReplyToRequest = "approval";
        saga.SequenceSteps[1].StepMetadata["WaitKey"] = "approval";
        request.OutcomeBranches =
        [
            new() { Value = "accepted", Steps = [outcomeSource] },
            new() { IsDefault = true }
        ];
        saga.CatchBlocks = [Step(WorkflowStepType.CatchBlock)];
        saga.CatchBlocks[0].SequenceSteps = [Step(WorkflowStepType.Activity)];

        var each = Step(WorkflowStepType.ForEach);
        each.ForEachBodySteps = [Step(WorkflowStepType.SuspendResume), Step(WorkflowStepType.ForEachResult)];
        each.ForEachBodySteps[0].TimeoutSteps = [Step(WorkflowStepType.ForEachResult)];
        var loop = Step(WorkflowStepType.Loop);
        loop.LoopBodySteps = [each, Step(WorkflowStepType.Loop)];
        loop.LoopBodySteps[1].LoopType = LoopType.WhileDo;

        var condition = Step(WorkflowStepType.Conditional);
        condition.ThenSteps = [loop];
        condition.ElseSteps = [Step(WorkflowStepType.WorkflowInvocation)];
        var attempt = Step(WorkflowStepType.TryCatch);
        attempt.SequenceSteps = [condition];
        attempt.CatchBlocks = [Step(WorkflowStepType.CatchBlock), Step(WorkflowStepType.CatchBlock)];
        attempt.CatchBlocks[0].ExceptionType = typeof(InvalidOperationException);
        attempt.CatchBlocks[0].SequenceSteps = [Step(WorkflowStepType.Sequence)];
        attempt.CatchBlocks[0].SequenceSteps[0].SequenceSteps = [Step(WorkflowStepType.Activity)];
        attempt.FinallySteps = [Step(WorkflowStepType.Delay)];
        var parallel = Step(WorkflowStepType.Parallel);
        parallel.ParallelBranches = [[attempt], [], [saga]];
        var sequence = Step(WorkflowStepType.Sequence);
        sequence.SequenceSteps = [parallel];
        yield return Definition("Nested bodies", [sequence]);
    }

    private static WorkflowDefinition Definition(string name, List<WorkflowStep> steps) => new()
    {
        Name = name, Version = 1, WorkflowDataType = typeof(object), Steps = steps
    };

    private static WorkflowStep Step(WorkflowStepType type) => new()
    {
        Id = type.ToString(), Name = type.ToString(), StepType = type,
        ActivityType = type == WorkflowStepType.Activity ? typeof(object) : null,
        WorkflowDataType = typeof(object), PreviousStepDataType = typeof(object),
        WorkflowName = type == WorkflowStepType.WorkflowInvocation ? "Child" : null,
        WorkflowVersion = type == WorkflowStepType.WorkflowInvocation ? 2 : null,
        InvokedDataType = type == WorkflowStepType.WorkflowInvocation ? typeof(object) : null,
        ResumeEventType = type == WorkflowStepType.SuspendResume ? typeof(string) : null,
        WaitTimeout = type == WorkflowStepType.SuspendResume ? TimeSpan.FromSeconds(5) : null,
        DelayDuration = type == WorkflowStepType.Delay ? TimeSpan.FromSeconds(1) : null,
        ResultType = type == WorkflowStepType.ForEachResult ? typeof(int) : null
    };

    private sealed record Snapshot(string Name, string Hash, string[] ScopeIds, string[] StepIds);
}
