using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
public sealed class ActivityTriggerTests
{
    [Fact]
    public async Task ActivityUsesNormalMappingsAndTheWinningValueForGuardAndAction()
    {
        await using var services = Services();

        var definition = Workflow.Create<Data>("ActivityTriggerMapping").Step<Incoming>()
            .StateMachine(m => m.Start("a").State("a", s => s.OnActivity<Work>(t => t
                .Input(x => x.Number).From(c => c.PreviousStep.Text.Length)
                .Output(x => x.Number).To(c => c.WorkflowData.Number)
                .When(c => c.PreviousStep.Number == 8 && c.WorkflowData.Number == 8, "end",
                    a => a.Step<Work>(w => w.Input(x => x.Number).From(c => c.PreviousStep.Number + 1)
                        .Output(x => x.Number).To(c => c.WorkflowData.Action)))))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(8, data.Number);
        Assert.Equal(9, data.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogCloneHashAndPickTypesIncludeBothFireFormsAndConditionalListener(bool mixed)
    {
        var definition = Definition(mixed);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        Assert.Equal(typeof(Incoming), scopes.Step("root/0/state/a/on/0/run/0").PreviousStepDataType);
        Assert.Equal(typeof(Work), scopes.Step("root/0/state/a/on/0/run/1").PreviousStepDataType);
        Assert.Equal(typeof(Work), scopes.Step("root/0/state/a/on/0/0").PreviousStepDataType);
        var fire = definition.StepTable.Single(s => s.CatalogId == "root/0/state/a/on/0/run/1");
        Assert.EndsWith("/a/on/0/run/fire", fire.AuthorId);
        Assert.False(string.IsNullOrWhiteSpace(fire.SourceLocation));
        Assert.Equal(mixed, definition.StepTable.Any(s => s.Kind == WorkflowStepType.Listen));

        if (mixed)
        {
            Assert.Equal(typeof(Incoming), scopes.Step("root/0/state/a/listen/0").PreviousStepDataType);
            Assert.Equal(typeof(object), scopes.Step("root/0/state/a/listen/1").PreviousStepDataType);
        }

        var clone = new WorkflowDefinition
        {
            Name = "Clone", Version = 1, WorkflowDataType = typeof(Data),
            Steps = [definition.Steps[0].Clone()]
        };

        Assert.Equal(WorkflowDefinitionValidator.Inspect(definition), WorkflowDefinitionValidator.Inspect(clone));
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(definition), WorkflowDefinitionValidator.Inspect(Definition(!mixed)));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("activity-output")]
    [InlineData("listener-output")]
    [InlineData("request")]
    public void ImplicitForkEnforcesCopyabilityAndNamedMergeTargetsWithoutAuthoredParallel(string shape)
    {
        Assert.Throws<WorkflowEngineException>(() =>
        {
            if (shape == "copy")
                return Workflow.Create<Uncopyable>("ActivityTriggerCopy").StateMachine(m => m.Start("a")
                    .State("a", s => s.OnActivity<Work>("end"))).Build();

            return Workflow.Create<Data>("ActivityTriggerInvalid").StateMachine(m => m.Start("a").State("a", s =>
            {
                if (shape == "request")
                    s.OnActivity<Ask>("end");
                else
                    s.OnActivity<Work>(t =>
                    {
                        if (shape == "activity-output")
                            t.Output(x => x.Number).To(c => c.WorkflowData.Ignored);

                        t.Otherwise("end");
                    });

                if (shape == "listener-output")
                    s.On<Signal>("go", t => t.Output(x => x.Number)
                        .To(c => c.WorkflowData.Ignored).Otherwise("end"));
            })).Build();
        });
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 1, true)]
    public void AdmissionCountsOnlyActualBranchesAndRefusesBeforeForkMutation(bool mixed, int cap, bool fails)
    {
        using var services = Services();
        var (checkpoint, scopes, transitions) = Armed(services, Definition(mixed), fork: false);
        var root = checkpoint.Continuations[0];
        var before = JsonSerializer.Serialize(checkpoint);

        if (fails)
        {
            Assert.Throws<BranchLimitExceededException>(() => transitions.Advance(scopes, checkpoint, root, cap, 100));
            Assert.Empty(checkpoint.Joins);
            Assert.Single(checkpoint.Continuations);
            Assert.Equal(ContinuationStatus.Active, root.Status);
            Assert.Equal(before, JsonSerializer.Serialize(checkpoint));
        }
        else
        {
            transitions.Advance(scopes, checkpoint, root, cap, 100);
            Assert.Equal(mixed ? 2 : 1, Assert.Single(checkpoint.Joins).ChildContinuationIds.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedAdmissionExcludesTheParentLeavingItsLeafSlot(bool mixed)
    {
        using var services = Services();

        var definition = new WorkflowBuilder<Data, Incoming>([], "ActivityTriggerNested")
            .Parallel(p => p.Do(b => b.StateMachine(m => m.Start("a").State("a", s =>
            {
                s.OnActivity<Work>("end");

                if (mixed)
                    s.On<Signal>("go", "end");
            })))).Build();

        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        checkpoint.RootData = new Data();
        var root = checkpoint.Continuations[0];
        root.SetPrevious(new Incoming());
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        transitions.Advance(scopes, checkpoint, root, 2, 100);
        var child = checkpoint.Continuations[1];
        transitions.Advance(scopes, checkpoint, child, 2, 100);
        transitions.Advance(scopes, checkpoint, child, 2, 100);
        transitions.Advance(scopes, checkpoint, child, mixed ? 2 : 1, 100);
        Assert.Equal(2, checkpoint.Joins.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredBatchSelectsPersistedFirstChildMergesOnlyItAndLateNotificationCannotReplaceFire(bool recover)
    {
        using var services = Services();
        var (checkpoint, scopes, _) = Armed(services, Definition(twoActivities: true));
        var join = Assert.Single(checkpoint.Joins);
        var children = checkpoint.BranchesOf(join);
        Complete(children[1], 22);
        Complete(children[0], 11);

        // Reverse the physical list: the persisted join order is the arbitration rule.
        checkpoint.Continuations.Reverse(1, 2);

        if (recover)
            checkpoint = RoundTrip(checkpoint, scopes);

        join = Assert.Single(checkpoint.Joins);
        Assert.True(ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint));
        Assert.Equal(join.ChildContinuationIds[0], join.WinningChildId);
        var graph = checkpoint.Continuations[0].Stack[0].Graph!;
        Assert.EndsWith("/on/0/0", graph.Fired!.Trigger);
        var winner = graph.Fired;
        var loser = checkpoint.ContinuationById(join.ChildContinuationIds[1])!;

        loser.PendingJoinFrame = new ScopePosition
        {
            Kind = FrameKind.Run,
            Fired = new GraphFired { Trigger = "late", Event = SerializedValue.From(new Work { Number = 99 }) }
        };

        ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
        Assert.Same(winner, graph.Fired);
        var conflicts = new List<(ContinuationState, JoinConflictException)>();
        Assert.True(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, conflicts));
        Assert.Empty(conflicts);
        Assert.Equal(11, Assert.IsType<Data>(checkpoint.RootData).Number);
        var parent = checkpoint.Continuations[0];
        Assert.Equal(1, parent.Stack[^1].NextStepIndex);
        Assert.Equal(2, parent.Stack[^1].ArmOccurrence);
        Assert.Equal(ContinuationStatus.Active, parent.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingListenerReceiptSurvivesBeforeDispatchAndAfterHeldCompletionWithBothOwnersMarked(bool completed)
    {
        using var services = Services();

        var definition = new WorkflowBuilder<Data, Incoming>([], "ActivityTriggerReceipt").StateMachine(m => m.Start("a")
            .State("a", s => s.Entry(e => e.Request<Ask>("answer")).OnActivity<Work>(t => t.When(c => false, "end"))
                .OnReply<Signal>("answer", "end"))).Build();

        var (checkpoint, scopes, _) = Armed(services, definition, entryRequest: true);
        var join = Assert.Single(checkpoint.Joins);
        var parent = checkpoint.Continuations[0];
        var listener = checkpoint.BranchesOf(join)[1];
        var frame = listener.Stack[0];

        var receipt = new BoundReplyState
        {
            ContinuationId = listener.Id, ScopeActivationId = parent.Stack[^1].ActivationId,
            StepId = "root/0/state/a/on/1/0", WaitId = "answered", EventId = "event", InvocationId = "request",
            Event = SerializedValue.From(new Signal { Number = 7 })!
        };

        frame.Fired = new GraphFired { Trigger = receipt.StepId, Event = receipt.Event, Reply = receipt };
        listener.AcceptedWait = new AcceptedWaitState { StepId = receipt.StepId, WaitId = receipt.WaitId, Event = receipt.Event };

        if (completed)
        {
            GraphTransitions.ReturnReplies(checkpoint, listener, frame, false);
            listener.Stack.Clear();
            listener.PendingJoinFrame = frame;
            listener.Status = ContinuationStatus.Completed;
        }

        var winner = checkpoint.BranchesOf(join)[0];
        Complete(winner, 1);
        checkpoint = RoundTrip(checkpoint, scopes);
        var saved = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"));
        checkpoint.TakeMarks();
        ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
        var answer = Assert.Single(checkpoint.BoundReplies);
        Assert.Equal(parent.Id, answer.ContinuationId);
        Assert.Equal("answered", answer.WaitId);
        Assert.Null(checkpoint.ContinuationById(listener.Id)!.AcceptedWait);
        var marks = checkpoint.TakeMarks();
        Assert.True(marks.Has(parent.Id));
        Assert.True(marks.Has(listener.Id));
        StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"), saved.Parts, marks, verify: true);
    }

    [Fact]
    public void RecordedWinnerWaitsForUnresolvedLoserAndRecoveryDoesNotReselectOrMergeEarly()
    {
        using var services = Services();
        var (checkpoint, scopes, _) = Armed(services, Definition(twoActivities: true));
        var join = Assert.Single(checkpoint.Joins);
        var children = checkpoint.BranchesOf(join);
        Assert.Equal("Armed", GraphFrameInfo.Of(scopes, checkpoint.Continuations[0], 1)!.Phase);
        Complete(children[0], 3);
        children[1].PendingActivity = new ActivityInvocationState { StepId = "root/0/state/a/on/1/run/0" };
        children[1].Status = ContinuationStatus.WaitingResolution;
        ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
        checkpoint = RoundTrip(checkpoint, scopes);
        join = Assert.Single(checkpoint.Joins);
        var root = checkpoint.Continuations[0];
        Assert.NotNull(root.Stack[0].Graph!.Fired);
        Assert.Equal(ContinuationStatus.Joining, root.Status);
        Assert.Equal("Settling", GraphFrameInfo.Of(scopes, root, 1)!.Phase);
        Assert.False(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Equal(0, Assert.IsType<Data>(checkpoint.RootData).Number);
        var loser = checkpoint.ContinuationById(join.ChildContinuationIds[1])!;
        loser.PendingActivity = null;
        loser.Status = ContinuationStatus.Cancelled;
        Assert.True(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Equal(3, Assert.IsType<Data>(checkpoint.RootData).Number);
        Assert.Equal(2, root.Stack[^1].ArmOccurrence);
        Assert.Equal("Fired", GraphFrameInfo.Of(scopes, root, 1)!.Phase);
    }

    [Fact]
    public void ReplyHandoffPreservesOpenWaitAndBoundAnswerIdentityAndMarksBothStorageOwners()
    {
        using var services = Services();
        var (checkpoint, scopes, transitions) = Armed(services, Definition(mixed: true), fork: false);
        var parent = checkpoint.Continuations[0];
        var activation = parent.Stack[^1].ActivationId;

        var wait = new WaitState
        {
            Id = "original", ContinuationId = parent.Id, IsReply = true,
            ScopeActivationId = activation, StepId = "root/0/state/a/on/1/0", InvocationId = "invocation", Subject = "subject"
        };

        checkpoint.Waits.Add(wait);
        transitions.Advance(scopes, checkpoint, parent, 2, 100);
        var listener = checkpoint.BranchesOf(Assert.Single(checkpoint.Joins))[1];
        Assert.Equal(listener.Id, wait.ContinuationId);

        var answer = new BoundReplyState
        {
            WaitId = "bound", ContinuationId = listener.Id, ScopeActivationId = activation,
            StepId = wait.StepId, EventId = "event", Event = SerializedValue.From(new Signal { Number = 7 })!
        };

        checkpoint.BoundReplies.Add(answer);
        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"));
        checkpoint.TakeMarks();
        GraphTransitions.ReturnReplies(checkpoint, listener, listener.Stack[0], false);
        Assert.Same(wait, Assert.Single(checkpoint.Waits));
        Assert.Equal(parent.Id, wait.ContinuationId);
        Assert.Equal("invocation", wait.InvocationId);
        Assert.Equal("subject", wait.Subject);
        Assert.Same(answer, Assert.Single(checkpoint.BoundReplies));
        Assert.Equal(parent.Id, answer.ContinuationId);
        var marks = checkpoint.TakeMarks();
        Assert.True(marks.Has(parent.Id));
        Assert.True(marks.Has(listener.Id));
        StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"), stored.Parts, marks, verify: true);
    }

    [Fact]
    public void CancellationAfterWinnerSelectionClearsSpeculativeFireAndMergesNothing()
    {
        using var services = Services();
        var (checkpoint, scopes, _) = Armed(services, Definition(twoActivities: true));
        var join = Assert.Single(checkpoint.Joins);
        var children = checkpoint.BranchesOf(join);
        Complete(children[0], 11);
        children[1].Status = ContinuationStatus.WaitingResolution;
        children[1].PendingActivity = new ActivityInvocationState { StepId = "root/0/state/a/on/1/run/0" };
        ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
        var parent = checkpoint.Continuations[0];
        Assert.NotNull(parent.Stack[0].Graph!.Fired);
        ContinuationTransitions.CancelTree(checkpoint, parent);
        Assert.Null(parent.Stack[0].Graph!.Fired);
        children[1].Status = ContinuationStatus.Cancelled;
        children[1].PendingActivity = null;
        Assert.True(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Equal(0, Assert.IsType<Data>(checkpoint.RootData).Number);
        Assert.Equal(ContinuationStatus.Cancelling, parent.Status);
        Assert.Equal(1, parent.Stack[^1].ArmOccurrence);
    }

    internal static WorkflowDefinition Definition(bool mixed = false, bool twoActivities = false) =>
        new WorkflowBuilder<Data, Incoming>([], "ActivityTriggerShape").StateMachine(m => m.Start("a").State("a", s =>
        {
            s.OnActivity<Work>(t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));

            if (twoActivities)
                s.OnActivity<Work>(t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));

            if (mixed)
                s.On<Signal>("go", "end");
        })).Build();

    private static (ExecutionCheckpoint, WorkflowScopeCatalog, ContinuationTransitions) Armed(IServiceProvider services,
        WorkflowDefinition definition, bool fork = true, bool entryRequest = false)
    {
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        checkpoint.RootData = new Data();
        var root = checkpoint.Continuations[0];
        root.SetPrevious(new Incoming());
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        transitions.Advance(scopes, checkpoint, root, 100, 100);
        transitions.Advance(scopes, checkpoint, root, 100, 100);

        if (entryRequest)
            root.Stack[^1].NextStepIndex++;

        GraphTransitions.PreparePick(scopes, root);

        if (fork)
            transitions.Advance(scopes, checkpoint, root, 100, 100);

        return (checkpoint, scopes, transitions);
    }

    private static void Complete(ContinuationState child, int number)
    {
        Assert.IsType<Data>(child.Data).Number = number;
        child.DataChanged = true;
        var frame = child.Stack[0];
        frame.Fired = new GraphFired { Trigger = frame.ScopeId[..^4] + "/0", Event = SerializedValue.From(new Work { Number = number }) };
        frame.NextStepIndex = 2;
        child.Stack.Clear();
        child.PendingJoinFrame = frame;
        child.Status = ContinuationStatus.Completed;
    }

    private static ExecutionCheckpoint RoundTrip(ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        var data = checkpoint.RootData;

        foreach (var child in checkpoint.Continuations.Where(c => c.ParentJoinId != null && c.Data != null))
        {
            child.DataChanges = BranchData.Changes(BranchData.Node(data!), child.Data!, data!.GetType());
            child.DataChanged = false;
        }

        var copy = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        copy.RootData = data;
        copy.Trace.Configure(new WorkflowInstance(), copy, ExecutionLimits.Default, null, scopes);

        return copy;
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public int Number { get; set; }
        public int Action { get; set; }
        [JsonIgnore] public int Ignored { get; set; }
    }

    public sealed class Uncopyable
    {
        public List<int> Values { get; } = [];
    }

    public sealed class Signal
    {
        public int Number { get; set; }
    }

    public sealed class Incoming : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Work : IAsyncActivity
    {
        public int Number { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Ask : IRequestActivity<Signal>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}