using System.Text;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>The same review regressions at the engine and profile-store boundaries.</summary>
public static class ActivityTriggerReviewCases
{
    public const long HydrationBudget = 192 * 1024;
    private const int LargeLength = 128 * 1024;

    public static async Task ReplyIntakeDefersSelectedFireUntilJoinCompletes(IServiceProvider host,
        IWorkflowStateRepository repository)
    {
        var probe = host.GetRequiredService<Probe>();
        var definition = new WorkflowBuilder<Data, object>([], "ActivityTriggerReplyIntakeHydration")
            .StateMachine(m => m.Start("a").State("a", s => s.Entry(e => e.Request<AskForAnswer>("answer"))
                .OnActivity<Work>(t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => c.PreviousStep.Number == 11 && c.PreviousStep.Text.Length == LargeLength,
                        "end", a => a.Step<Mark>(w => w.Input(x => x.Number).From(c => c.PreviousStep.Number)
                            .Output(x => x.Number).To(c => c.WorkflowData.Action))))
                .OnActivity<Work>("end")
                .OnReply<Answer>("answer", "end"))).Build();
        var (instance, checkpoint, scopes, transitions) = New(definition, host);
        var parent = checkpoint.Continuations[0];
        while (true)
        {
            var position = parent.Stack[^1];
            var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
            if (next?.StepType == WorkflowStepType.Arm) break;
            if (next?.RequestName != null)
                position.NextStepIndex++; // Seed the completed entry request and its original wait below.
            else Advance(transitions, scopes, checkpoint, parent);
        }
        var wait = new WaitState
        {
            ContinuationId = parent.Id, IsReply = true, InvocationId = "entry-request", Subject = "review",
            ScopeActivationId = parent.Stack[^1].ActivationId, StepId = parent.Stack[^1].ScopeId + "/on/2/0",
            EventType = WorkflowTypeIdentity.SavedName(typeof(Answer)), RegisteredAtUtc = DateTime.UtcNow
        };
        checkpoint.AddWait(wait);
        Advance(transitions, scopes, checkpoint, parent);
        var join = Assert.Single(checkpoint.Joins);
        var children = checkpoint.BranchesOf(join);
        var winner = children[0];
        var loser = children[1];
        Assert.Equal(children[2].Id, wait.ContinuationId);
        var invocation = new ActivityInvocationState { StepId = loser.Stack[0].ScopeId + "/0" };
        loser.PendingActivity = invocation;
        loser.Status = ContinuationStatus.WaitingResolution;

        var fire = SerializedValue.From(new Work(probe) { Number = 11, Text = new string('a', LargeLength) })!;
        fire.Json = await Reference(repository, fire.Json);
        Assert.IsType<Data>(winner.Data).Number = 11;
        winner.DataChanged = true;
        var frame = winner.Stack[0];
        frame.NextStepIndex = 2;
        frame.Fired = new GraphFired { Trigger = frame.ScopeId[..^"/run".Length] + "/0", Event = fire };
        winner.PendingJoinFrame = frame;
        winner.Stack.Clear();
        winner.Status = ContinuationStatus.Completed;
        Assert.True(ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint));
        Assert.Equal(winner.Id, join.WinningChildId);
        Assert.Equal(parent.Id, wait.ContinuationId);
        await Seed(host, repository, definition, instance, checkpoint, scopes);

        var client = host.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(instance.InstanceId)).Status);
        var recovered = await Read(repository, instance.InstanceId);
        Assert.Equal(ContinuationStatus.Joining, recovered.Continuations[0].Status);
        Assert.Equal(winner.Id, Assert.Single(recovered.Joins).WinningChildId);
        Assert.Equal(wait.Id, Assert.Single(recovered.Waits).Id);

        var answer = SerializedValue.From(new Answer { Number = 22, Text = new string('b', LargeLength) })!;
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(new(instance.InstanceId,
            "answer", answer.Type, answer.Json.InlineText, TargetWaitId: wait.Id, Subject: wait.Subject)
            { PayloadThresholdBytes = 1024 })).Status);
        var storedAnswer = Assert.Single((await repository.GetPendingEventsAsync(instance.InstanceId, 0, 100)).Events);
        Assert.NotNull(storedAnswer.PayloadJson.Reference);
        Assert.True(fire.Json.Bytes < HydrationBudget);
        Assert.True(storedAnswer.PayloadJson.Bytes < HydrationBudget);
        Assert.True(fire.Json.Bytes + storedAnswer.PayloadJson.Bytes > HydrationBudget);
        Assert.NotEqual(fire.Json.Reference, storedAnswer.PayloadJson.Reference);

        var intake = await client.RecoverAsync(instance.InstanceId);
        Assert.True(intake.Status == WorkflowExecutionStatus.NeedsResolution, intake.ErrorMessage);
        var bound = await Read(repository, instance.InstanceId);
        Assert.True(bound.OperatorStop == null, bound.OperatorStop?.Reason);
        Assert.Equal(ContinuationStatus.Joining, bound.Continuations[0].Status);
        Assert.Equal(winner.Id, Assert.Single(bound.Joins).WinningChildId);
        Assert.Equal(ContinuationStatus.WaitingResolution, bound.ContinuationById(loser.Id)!.Status);
        Assert.Equal(invocation.Id, bound.ContinuationById(loser.Id)!.PendingActivity!.Id);
        var receipt = Assert.Single(bound.BoundReplies);
        Assert.Equal(wait.Id, receipt.WaitId);
        Assert.Equal("answer", receipt.EventId);
        Assert.Equal(parent.Id, receipt.ContinuationId);
        Assert.Equal(wait.InvocationId, receipt.InvocationId);
        Assert.Equal(wait.ScopeActivationId, receipt.ScopeActivationId);
        Assert.Equal(wait.Subject, receipt.Subject);
        Assert.Equal(storedAnswer.PayloadJson.Reference, receipt.Event.Json.Identity);
        Assert.Equal(fire.Json.Reference, bound.Continuations[0].Stack.Single(f => f.Graph != null).Graph!.Fired!.Event!.Json.Reference);
        Assert.False((await repository.GetWaitAsync(instance.InstanceId, wait.Id))!.IsOpen);
        Assert.Empty(bound.Waits);
        Assert.Empty((await repository.GetPendingEventsAsync(instance.InstanceId, 0, 100)).Events);
        Assert.Equal(0, Assert.IsType<Data>(intake.WorkflowData).Number);
        Assert.Equal(0, probe.MarkCalls);

        var result = await client.ResolveAsync(instance.InstanceId, invocation.Id,
            ActivityResolution.Completed(new Dictionary<string, object?> { ["Number"] = 99 }));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(11, data.Number);
        Assert.Equal(11, data.Action);
        Assert.Equal(0, probe.WorkCalls);
        Assert.Equal(1, probe.MarkCalls);
        var finished = await Read(repository, instance.InstanceId);
        Assert.Empty(finished.Joins);
        Assert.Empty(finished.Waits);
        Assert.Empty(finished.BoundReplies);
    }

    public static async Task CancellationPreservesCleanupWinner(IServiceProvider host, IWorkflowStateRepository repository)
    {
        var probe = host.GetRequiredService<Probe>();
        var definition = new WorkflowBuilder<Data, object>([], "ActivityTriggerFinallyWinner")
            .Try(_ => { }).Finally(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .OnActivity<Work>(t => t.Input(x => x.Number).From(c => 11)
                    .Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => c.PreviousStep.Number == 11, "end", a => a.Step<Mark>(w => w
                        .Input(x => x.Number).From(c => c.PreviousStep.Number)
                        .Output(x => x.Number).To(c => c.WorkflowData.Action))))
                .OnActivity<Work>("end")))).Build();
        var (instance, checkpoint, scopes, transitions) = New(definition, host);
        var parent = checkpoint.Continuations[0];
        while (checkpoint.Joins.Count == 0) Advance(transitions, scopes, checkpoint, parent);
        Assert.True(TryScopeTransitions.IsInsideFinally(parent));
        var join = Assert.Single(checkpoint.Joins);
        var loser = checkpoint.BranchesOf(join)[1];
        var invocation = new ActivityInvocationState { StepId = loser.Stack[0].ScopeId + "/0" };
        loser.PendingActivity = invocation;
        loser.Status = ContinuationStatus.WaitingResolution;
        await Seed(host, repository, definition, instance, checkpoint, scopes);

        var client = host.GetRequiredService<IWorkflowClient>();
        var first = await client.RecoverAsync(instance.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, first.Status);
        var selected = await Read(repository, instance.InstanceId);
        var winner = Assert.Single(selected.Joins).WinningChildId;
        Assert.NotNull(winner);
        Assert.NotNull(selected.Continuations[0].Stack.Single(f => f.Graph != null).Graph!.Fired);
        Assert.Equal(1, probe.WorkCalls);

        await client.CancelAsync(instance.InstanceId, new CancellationReason { ReasonCode = "review-finally" });
        var recovered = await client.RecoverAsync(instance.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        var cancelled = await Read(repository, instance.InstanceId);
        Assert.True(cancelled.CancellationRequested);
        Assert.Equal(winner, Assert.Single(cancelled.Joins).WinningChildId);
        Assert.NotNull(cancelled.Continuations[0].Stack.Single(f => f.Graph != null).Graph!.Fired);
        Assert.Equal(1, probe.WorkCalls);

        var result = await client.ResolveAsync(instance.InstanceId, invocation.Id,
            ActivityResolution.Completed(new Dictionary<string, object?> { ["Number"] = 99 }));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(11, data.Number);
        Assert.Equal(11, data.Action);
        Assert.Equal(1, probe.WorkCalls);
        Assert.Equal(1, probe.MarkCalls);
        var finished = await Read(repository, instance.InstanceId);
        Assert.Empty(finished.Joins);
        Assert.Empty(finished.Waits);
    }

    public static async Task RecoveryOnlyHydratesNeededResults(IServiceProvider host,
        IWorkflowStateRepository repository, string shape)
    {
        var held = shape == "held";
        var builder = new WorkflowBuilder<Data, object>([], "ActivityTriggerHydration" + shape);
        void Machine(StateMachineBuilder<Data, object> m) => m.Start("a").State("a", s =>
        {
            if (shape == "receipt") s.Entry(e => e.Request<Ask>("answer"));
            s.OnActivity<Work>(t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                .Output(x => x.Text).To(c => c.WorkflowData.Payload).Otherwise("end"));
            if (shape == "receipt") s.OnReply<Work>("answer", t => t.Output(x => x.Number)
                .To(c => c.WorkflowData.Number).Otherwise("end"));
            else s.OnActivity<Work>(t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                .Output(x => x.Text).To(c => c.WorkflowData.Payload).Otherwise("end"));
        });
        var definition = held
            ? builder.Parallel(p => p.Do(b => b.StateMachine(Machine)).Do(b => b.Step<Mark>().Delay(TimeSpan.FromMinutes(1)))).Build()
            : builder.StateMachine(Machine).Build();
        var (instance, checkpoint, scopes, transitions) = New(definition, host);
        var root = checkpoint.Continuations[0];
        ContinuationState parent = root;
        ContinuationState? independent = null;
        if (held)
        {
            Advance(transitions, scopes, checkpoint, root);
            var outer = Assert.Single(checkpoint.Joins);
            parent = checkpoint.BranchesOf(outer)[0];
            independent = checkpoint.BranchesOf(outer)[1];
        }
        while (!checkpoint.Joins.Any(j => j.ParentContinuationId == parent.Id))
        {
            var position = parent.Stack[^1];
            if (scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex)?.RequestName != null)
                position.NextStepIndex++; // The receipt fixture represents an already answered entry request.
            else Advance(transitions, scopes, checkpoint, parent);
        }
        var join = checkpoint.Joins.Single(j => j.ParentContinuationId == parent.Id);
        var children = checkpoint.BranchesOf(join);
        var references = new List<PayloadReference>();
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var number = index == 0 ? 11 : 22;
            var text = new string(index == 0 ? 'a' : 'b', LargeLength);
            var fire = SerializedValue.From(new Work(host.GetRequiredService<Probe>())
                { Number = number, Text = shape == "data" ? "" : text })!;
            if (shape != "data")
            {
                fire.Json = await Reference(repository, fire.Json);
                references.Add(fire.Json.Reference!);
            }
            var data = Assert.IsType<Data>(child.Data);
            data.Number = number;
            data.Payload = shape == "data" ? text : "";
            child.DataChanges = BranchData.Changes(BranchData.Node(checkpoint.DataOf(parent)), data, typeof(Data));
            if (shape == "data")
            {
                foreach (var change in child.DataChanges.Where(c => c.Value != null && c.Value.Bytes > LargeLength))
                {
                    change.Value = await Reference(repository, change.Value!);
                    references.Add(change.Value.Reference!);
                }
            }
            child.Data = null;
            child.DataChanged = false;
            var frame = child.Stack[0];
            frame.NextStepIndex = 2;
            frame.Fired = new GraphFired
            {
                Trigger = frame.Kind == FrameKind.Listen ? parent.Stack[^1].ScopeId + "/on/1/0"
                    : frame.ScopeId[..^"/run".Length] + "/0",
                Event = fire
            };
            if (frame.Kind == FrameKind.Listen)
                frame.Fired.Reply = new BoundReplyState
                {
                    ContinuationId = child.Id, ScopeActivationId = parent.Stack[^1].ActivationId,
                    StepId = frame.Fired.Trigger, WaitId = "answered", EventId = "answer", InvocationId = "request",
                    Event = fire
                };
            child.PendingJoinFrame = frame;
            child.Stack.Clear();
            child.Status = ContinuationStatus.Completed;
        }
        Assert.Equal(2, references.Count);
        Assert.All(references, r => Assert.True(r.Bytes < HydrationBudget));
        Assert.True(references.Sum(r => r.Bytes) > HydrationBudget);
        Assert.NotEqual(references[0], references[1]);
        if (independent != null)
            checkpoint.Debug = checkpoint.Debug with
            {
                Stepping = new(independent.Id, DebugStepKind.Into, SteppingPolicy.Position(independent.Stack[0]),
                    [independent.Stack[0].ActivationId], [independent.Id], "independent", "review")
            };
        await Seed(host, repository, definition, instance, checkpoint, scopes);
        var client = host.GetRequiredService<IWorkflowClient>();
        var result = await client.RecoverAsync(instance.InstanceId);
        if (held)
        {
            Assert.True(result.Status == WorkflowExecutionStatus.Paused, result.ErrorMessage);
            Assert.Equal(1, host.GetRequiredService<Probe>().MarkCalls);
            var saved = await Read(repository, instance.InstanceId);
            Assert.Equal(2, saved.Continuations.Count(c => c.PendingJoinFrame?.Fired != null));
            Assert.Null(saved.Joins.Single(j => j.Id == join.Id).WinningChildId);
        }
        else
        {
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            var data = Assert.IsType<Data>(result.WorkflowData);
            Assert.Equal(11, data.Number);
            Assert.Equal(shape == "data" ? new string('a', LargeLength) : "", data.Payload);
            Assert.Empty((await Read(repository, instance.InstanceId)).Joins);
        }
        Assert.Equal(0, host.GetRequiredService<Probe>().WorkCalls);
    }

    private static (WorkflowInstance, ExecutionCheckpoint, WorkflowScopeCatalog, ContinuationTransitions) New(
        WorkflowDefinition definition, IServiceProvider services)
    {
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        checkpoint.RootData = new Data();
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        return (instance, checkpoint, scopes, new(services, TimeProvider.System));
    }

    private static void Advance(ContinuationTransitions transitions, WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint, ContinuationState continuation) =>
        transitions.Advance(scopes, checkpoint, continuation, 100, 100);

    private static async Task<StoredJson> Reference(IWorkflowStateRepository repository, StoredJson json) =>
        StoredJson.FromReference(await repository.PayloadStore.PutAsync(Encoding.UTF8.GetBytes(json.InlineText)));

    private static async Task Seed(IServiceProvider host, IWorkflowStateRepository repository,
        WorkflowDefinition definition, WorkflowInstance instance, ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        await host.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow);
        var lease = await StoreTestSupport.LeaseAsync(repository, instance.InstanceId);
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, lease.Epoch, 0,
                openedWaits: checkpoint.Inbox.WaitChanges(checkpoint).Opened))).Status);
        await StoreTestSupport.ReleaseAsync(repository, instance.InstanceId);
    }

    private static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, string id) =>
        ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);

    public sealed class Data
    {
        public int Number { get; set; }
        public int Action { get; set; }
        public string Payload { get; set; } = "";
    }

    public sealed class Probe
    {
        public int WorkCalls;
        public int MarkCalls;
    }

    public sealed class Work(Probe probe) : IAsyncActivity
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.WorkCalls); return Task.CompletedTask; }
    }

    public sealed class Mark(Probe probe) : IAsyncActivity
    {
        public int Number { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.MarkCalls); return Task.CompletedTask; }
    }

    public sealed class Ask : IRequestActivity<Work>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Answer
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class AskForAnswer : IRequestActivity<Answer>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
