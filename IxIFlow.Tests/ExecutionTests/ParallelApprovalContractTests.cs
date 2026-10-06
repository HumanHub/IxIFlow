using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;

namespace IxIFlow.Tests.ExecutionTests;

public class ParallelApprovalContractTests
{
    [Fact]
    public async Task WaitFor_ResumesMatchingEventAsThePreviousStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("OneApproval")
            .Step<StartActivity>()
            .WaitFor<ApprovalReply>("finance", Matches("finance"), setup => setup
                .Output(reply => reply.Approved).To(ctx => ctx.WorkflowData.FinanceApproved))
            .Step<CompleteApprovalActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                .Input(activity => activity.Approver).From(ctx => "finance")
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.FinanceCompleted))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(GetData(completed).FinanceApproved);
        Assert.Equal(1, GetData(completed).FinanceCompleted);
    }

    [Theory]
    [InlineData("finance", "legal")]
    [InlineData("legal", "finance")]
    public async Task WaitAll_KeepsTwoApprovalsAndResumesEitherOrder(string first, string second)
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        Assert.True(started.Status == WorkflowExecutionStatus.Suspended, started.ErrorMessage);
        Assert.Equal(1, GetData(started).FinanceStarted);
        Assert.Equal(1, GetData(started).LegalStarted);
        Assert.Equal(2, (await repository.GetWorkflowInstanceAsync(started.InstanceId))!
            .ExecutionSnapshot!.Pointers.Count(pointer => pointer.Status == "Waiting"));

        var firstReply = await engine.ResumeWorkflowAsync(started.InstanceId, first,
            Reply(first, approved: true));

        Assert.Equal(WorkflowExecutionStatus.Suspended, firstReply.Status);
        var halfway = GetData(firstReply);
        Assert.Equal(1, halfway.FinanceStarted);
        Assert.Equal(1, halfway.LegalStarted);
        Assert.Equal(first == "finance" ? 1 : 0, halfway.FinanceCompleted);
        Assert.Equal(first == "legal" ? 1 : 0, halfway.LegalCompleted);
        Assert.Equal(0, halfway.Finished);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, second,
            Reply(second, approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var data = GetData(completed);
        Assert.Equal(1, data.FinanceStarted);
        Assert.Equal(1, data.LegalStarted);
        Assert.Equal(1, data.FinanceCompleted);
        Assert.Equal(1, data.LegalCompleted);
        Assert.Equal(1, data.Finished);
    }

    [Fact]
    public async Task WaitAll_RoutesSharedKeyByApprovalPredicate()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("SharedApprovalKey")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<ApprovalReply>("approval", Matches("finance"))
                    .Step<CompleteApprovalActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                        .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.FinanceCompleted)))
                .Do(branch => branch.WaitFor<ApprovalReply>("approval", Matches("legal"))
                    .Step<CompleteApprovalActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.LegalCompleted)
                        .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.LegalCompleted))))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        var first = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            Reply("finance", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        Assert.Equal(1, GetData(first).FinanceCompleted);
        Assert.Equal(0, GetData(first).LegalCompleted);

        var second = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            Reply("legal", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
        Assert.Equal(1, GetData(second).FinanceCompleted);
        Assert.Equal(1, GetData(second).LegalCompleted);
    }

    [Fact]
    public async Task ResumeRestoresActivityOutputWithPropertyJsonConverter()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("ConvertedPrevious")
            .Step<ConvertedOutputActivity>()
            .If(ctx => true, then => then.WaitFor<ApprovalReply>("approval"))
            .Step<CaptureConvertedOutputActivity>(setup => setup
                .Input(activity => activity.Status).From(ctx => ctx.PreviousStep.Status)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.EntrySeen))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            Reply("finance", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("Approved", GetData(completed).EntrySeen);
    }

    [Fact]
    public async Task EventManager_RoutesParallelApprovalsWithoutSuspensionInfo()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var manager = services.GetRequiredService<ISuspensionManager>();
        var started = await engine.ExecuteWorkflowAsync(
            CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false),
            new ApprovalData { RequestId = "request-1" });
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Null(saved!.SuspensionInfo);

        var routed = await manager.ProcessEventAsync(Reply("finance", approved: true));

        Assert.Contains(started.InstanceId, routed);
        saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.FinanceCompleted);
    }

    [Fact]
    public async Task EventManager_DoesNotReportRejectedApprovalAsResumed()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var manager = services.GetRequiredService<ISuspensionManager>();
        var started = await engine.ExecuteWorkflowAsync(
            CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false),
            new ApprovalData { RequestId = "request-1" });

        var correlator = services.GetRequiredService<IEventCorrelator>();
        Assert.False(await correlator.CheckResumeConditionAsync(started.InstanceId,
            new ApprovalReply { RequestId = "other-request", ApproverId = "finance" }));
        Assert.True(await correlator.CheckResumeConditionAsync(started.InstanceId,
            Reply("finance", approved: true)));

        var routed = await manager.ProcessEventAsync(new ApprovalReply
        {
            RequestId = "other-request", ApproverId = "finance", Approved = true
        });

        Assert.Empty(routed);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, saved!.Status);
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.FinanceCompleted);
    }

    [Fact]
    public async Task WaitAll_DuplicateReplyDoesNotConsumeAnotherApproval()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true));
        var duplicate = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));

        Assert.NotEqual(WorkflowExecutionStatus.Success, duplicate.Status);
        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, saved!.Status);
        var legal = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, legal.Status);
        Assert.Equal(1, GetData(legal).FinanceCompleted);
        Assert.Equal(1, GetData(legal).LegalCompleted);
    }

    [Fact]
    public async Task WaitAll_UnmatchedReplyLeavesBothApprovalsWaiting()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        var unmatched = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            new ApprovalReply { RequestId = "different-request", ApproverId = "finance", Approved = true });

        Assert.Equal(WorkflowExecutionStatus.Suspended, unmatched.Status);
        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(2, saved!.ExecutionSnapshot!.Pointers.Count(pointer => pointer.Status == "Waiting"));

        await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true));
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).FinanceCompleted);
        Assert.Equal(1, GetData(completed).LegalCompleted);
    }

    [Fact]
    public async Task WaitAll_ConcurrentRepliesAdvanceEachBranchOnce()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        await Task.WhenAll(
            engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true)),
            engine.ResumeWorkflowAsync(started.InstanceId, "legal", Reply("legal", approved: true)));

        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        var data = System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!;
        Assert.Equal(1, data.FinanceCompleted);
        Assert.Equal(1, data.LegalCompleted);
        Assert.Equal(1, data.Finished);
    }

    [Fact]
    public async Task WaitAll_SecondReplyWaitsWhileFirstReplyIsRunning()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResumeGateProbe>();
        services.AddTransient<BlockApprovalActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var probe = provider.GetRequiredService<ResumeGateProbe>();
        var definition = Workflow.Create<ApprovalData>("QueuedApprovals")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<ApprovalReply>("finance")
                    .Step<BlockApprovalActivity>(_ => { }))
                .Do(branch => branch.WaitFor<ApprovalReply>("legal")
                    .Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.LegalCompleted)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.LegalCompleted))))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var first = engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", true));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = engine.ResumeWorkflowAsync(started.InstanceId, "legal", Reply("legal", true));
            Assert.False(second.IsCompleted);
            probe.Release.TrySetResult();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, result => Assert.True(result.EventAccepted, result.ErrorMessage));
            Assert.Equal(WorkflowExecutionStatus.Success, results[1].Status);
            Assert.Equal(1, GetData(results[1]).LegalCompleted);
            Assert.Equal(1, GetData(results[1]).Finished);
        }
        finally
        {
            probe.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ResumeReportsRunningInstanceAsRetryable()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<ApprovalData>("RunningApproval")
            .Step<StartActivity>()
            .WaitFor<ApprovalReply>("finance")
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());
        var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        saved.Status = WorkflowStatus.Running;
        var claimed = await repository.CommitWorkflowInstanceAsync(
            saved, saved.Revision, Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, claimed.Status);

        var busy = await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", true));
        Assert.Equal(WorkflowExecutionStatus.Running, busy.Status);
        Assert.False(busy.EventAccepted);

        saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        saved.Status = WorkflowStatus.Suspended;
        var released = await repository.CommitWorkflowInstanceAsync(
            saved, saved.Revision, Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, released.Status);
        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", true));
        Assert.True(resumed.EventAccepted);
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
    }

    [Fact]
    public async Task WaitAll_ConcurrentDuplicateRepliesAdvanceOnlyOneWait()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        await Task.WhenAll(
            engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true)),
            engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true)));

        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, saved!.Status);
        var data = System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!;
        Assert.Equal(1, data.FinanceCompleted);
        Assert.Equal(0, data.LegalCompleted);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).FinanceCompleted);
    }

    [Fact]
    public async Task BranchFailureClosesOtherApprovalWaits()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("FailedApproval")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<ApprovalReply>("finance", Matches("finance")))
                .Do(branch => branch.Step<ThrowActivity>(_ => { })))
            .Build();

        var result = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(result.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(WorkflowStatus.Failed, saved!.Status);
        Assert.Null(saved.SuspensionInfo);
        Assert.Equal(WorkflowStatus.Failed, saved.ExecutionSnapshot!.Status);
        Assert.DoesNotContain(saved.ExecutionSnapshot.Pointers, pointer => pointer.Status == "Waiting");
    }

    [Fact]
    public async Task WaitAll_ResumesFromSavedCheckpointInAnotherServiceScope()
    {
        using var services = CreateServices();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        string instanceId;

        using (var startScope = services.CreateScope())
        {
            var engine = startScope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var started = await engine.ExecuteWorkflowAsync(definition,
                new ApprovalData { RequestId = "request-1" });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using (var resumeScope = services.CreateScope())
        {
            var engine = resumeScope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var halfway = await engine.ResumeWorkflowAsync(instanceId, "legal",
                Reply("legal", approved: true));
            Assert.Equal(WorkflowExecutionStatus.Suspended, halfway.Status);
        }

        using (var finishScope = services.CreateScope())
        {
            var engine = finishScope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var completed = await engine.ResumeWorkflowAsync(instanceId, "finance",
                Reply("finance", approved: true));
            Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
            Assert.Equal(1, GetData(completed).FinanceCompleted);
            Assert.Equal(1, GetData(completed).LegalCompleted);
            Assert.Equal(1, GetData(completed).Finished);
        }
    }

    [Fact]
    public async Task WaitAll_ResumesAfterRebuildingTheDefinitionInANewProvider()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var original = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        string instanceId;

        using (var firstProvider = CreateServices(repository))
        {
            var engine = firstProvider.GetRequiredService<IWorkflowEngine>();
            var started = await engine.ExecuteWorkflowAsync(original,
                new ApprovalData { RequestId = "request-1" });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        var rebuilt = CreateApprovalWorkflow(ParallelJoinMode.WaitAll, includeRisk: false);
        Assert.NotEqual(original.Steps[0].Id, rebuilt.Steps[0].Id);
        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(rebuilt);
        var resumedEngine = secondProvider.GetRequiredService<IWorkflowEngine>();

        await resumedEngine.ResumeWorkflowAsync(instanceId, "finance", Reply("finance", approved: true));
        var completed = await resumedEngine.ResumeWorkflowAsync(instanceId, "legal",
            Reply("legal", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).FinanceCompleted);
        Assert.Equal(1, GetData(completed).LegalCompleted);
        Assert.Equal(1, GetData(completed).Finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_ContinuesFromTheLastCommittedPosition(bool afterActivity)
    {
        var recording = new RecordingRepository();
        var definition = CreateRecoverWorkflow();

        using (var firstProvider = CreateServices(recording))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        }

        var saved = afterActivity ? recording.AfterFinance : recording.Initial;
        Assert.NotNull(saved);
        Assert.Equal(WorkflowStatus.Running, saved.Status);

        var recoveredRepository = new InMemoryWorkflowStateRepository();
        await recoveredRepository.CommitWorkflowInstanceAsync(saved, 0, Guid.NewGuid().ToString("N"));
        using var secondProvider = CreateServices(recoveredRepository);
        var rebuilt = CreateRecoverWorkflow();
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(rebuilt);
        var engine = secondProvider.GetRequiredService<IWorkflowEngine>();

        var idle = await engine.RecoverWorkflowAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(1, GetData(idle).FinanceStarted);

        var completed = await engine.ResumeWorkflowAsync(saved.InstanceId, "finance",
            Reply("finance", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).FinanceStarted);
        Assert.Equal(1, GetData(completed).Finished);
    }

    [Fact]
    public async Task FailedCheckpointWriteDoesNotReplayAnUnresolvedActivity()
    {
        var repository = new RecordingRepository { FailOnFinanceStartCount = 3 };
        using var services = CreateServices(repository);
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(
            CreateRecoverWorkflow(), new ApprovalData { RequestId = "request-1" }));

        var saved = (await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running)).Single();
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.FinanceStarted);

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(0, GetData(recovered).FinanceStarted);
    }

    [Fact]
    public async Task RecoveryRejectsChangedDefinitionWithoutLosingTheCheckpoint()
    {
        var recording = new RecordingRepository();
        using (var firstProvider = CreateServices(recording))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(CreateRecoverWorkflow(),
                    new ApprovalData { RequestId = "request-1" });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        }

        var saved = recording.Initial!;
        var repository = new InMemoryWorkflowStateRepository();
        await repository.CommitWorkflowInstanceAsync(saved, 0, Guid.NewGuid().ToString("N"));
        using (var wrongProvider = CreateServices(repository))
        {
            var changed = Workflow.Create<ApprovalData>("RecoverApproval")
                .Step<StartActivity>()
                .WaitFor<ApprovalReply>("finance")
                .Build();
            await wrongProvider.GetRequiredService<IWorkflowVersionRegistry>()
                .RegisterWorkflowAsync(changed);
            var rejected = await wrongProvider.GetRequiredService<IWorkflowEngine>()
                .RecoverWorkflowAsync(saved.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Faulted, rejected.Status);
        }

        var stillSaved = await repository.GetWorkflowInstanceAsync(saved.InstanceId);
        Assert.Equal(WorkflowStatus.Running, stillSaved!.Status);
        using var correctProvider = CreateServices(repository);
        await correctProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(CreateRecoverWorkflow());
        var recovered = await correctProvider.GetRequiredService<IWorkflowEngine>()
            .RecoverWorkflowAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);
        Assert.Equal(1, GetData(recovered).FinanceStarted);
    }

    [Fact]
    public async Task IncompleteInvocationIsRejectedBeforeAnActivityRuns()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("UnsupportedScope")
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceStarted)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceStarted))
            .WaitFor<ApprovalReply>("finance")
            .Build();
        definition.Steps.Insert(1, new WorkflowStep
        {
            Name = "Unsupported invocation",
            StepType = WorkflowStepType.WorkflowInvocation
        });
        var data = new ApprovalData();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteWorkflowAsync(definition, data));
        Assert.Equal(0, data.FinanceStarted);
    }

    [Fact]
    public async Task WaitAny_FirstCompletedBranchCancelsOtherApproval()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitAny, includeRisk: false);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: false));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var data = GetData(completed);
        Assert.Equal(1, data.FinanceCompleted);
        Assert.Equal(0, data.LegalCompleted);
        Assert.Equal(1, data.Finished);

        var late = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));
        Assert.NotEqual(WorkflowExecutionStatus.Success, late.Status);
    }

    [Fact]
    public async Task WaitAny_FirstReplyDoesNotJoinUntilItsBranchCompletes()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("TwoStageApproval")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .WaitFor<ApprovalReply>("finance", Matches("finance"))
                    .WaitFor<ApprovalReply>("finance-review", Matches("finance-review"))
                    .Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceCompleted)))
                .Do(branch => branch
                    .WaitFor<ApprovalReply>("legal", Matches("legal"))
                    .Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.LegalCompleted)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.LegalCompleted))))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var firstReply = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Suspended, firstReply.Status);
        Assert.Equal(0, GetData(firstReply).Finished);

        var legal = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, legal.Status);
        Assert.Equal(0, GetData(legal).FinanceCompleted);
        Assert.Equal(1, GetData(legal).LegalCompleted);
        Assert.Equal(1, GetData(legal).Finished);

        var late = await engine.ResumeWorkflowAsync(started.InstanceId, "finance-review",
            Reply("finance-review", approved: true));
        Assert.NotEqual(WorkflowExecutionStatus.Success, late.Status);
    }

    [Theory]
    [InlineData(ParallelJoinMode.WaitAll, WorkflowExecutionStatus.Suspended)]
    [InlineData(ParallelJoinMode.WaitAny, WorkflowExecutionStatus.Success)]
    public async Task CompletedBranchUsesConfiguredJoinWhileAnotherBranchWaits(
        ParallelJoinMode joinMode, WorkflowExecutionStatus expectedStatus)
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("CompletedBranch")
            .Step<StartActivity>()
            .Parallel(parallel =>
            {
                if (joinMode == ParallelJoinMode.WaitAny)
                    parallel.WaitAny();

                parallel.Do(branch => branch.Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceCompleted)));
                AddLegalBranch(parallel);
            })
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        Assert.Equal(expectedStatus, started.Status);
        Assert.Equal(1, GetData(started).FinanceCompleted);
        Assert.Equal(joinMode == ParallelJoinMode.WaitAny ? 1 : 0, GetData(started).Finished);

        if (joinMode == ParallelJoinMode.WaitAll)
        {
            var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
                Reply("legal", approved: true));
            Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
            Assert.Equal(1, GetData(completed).Finished);
        }
        else
        {
            var late = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
                Reply("legal", approved: true));
            Assert.NotEqual(WorkflowExecutionStatus.Success, late.Status);
        }
    }

    [Fact]
    public async Task WaitConditionally_RejectionKeepsWaitingUntilAnApprovalCompletes()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitConditionally, includeRisk: true);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        var rejected = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: false));

        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);
        Assert.Equal(1, GetData(rejected).FinanceCompleted);
        Assert.Equal(0, GetData(rejected).Finished);

        var approved = await engine.ResumeWorkflowAsync(started.InstanceId, "legal",
            Reply("legal", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, approved.Status);
        var data = GetData(approved);
        Assert.Equal("legal", data.ApprovedBy);
        Assert.Equal(0, data.RiskCompleted);
        Assert.Equal(1, data.Finished);

        var late = await engine.ResumeWorkflowAsync(started.InstanceId, "risk",
            Reply("risk", approved: true));
        Assert.NotEqual(WorkflowExecutionStatus.Success, late.Status);
    }

    [Fact]
    public async Task WaitConditionally_AllRejectedStillCompletesTheParallelStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CreateApprovalWorkflow(ParallelJoinMode.WaitConditionally, includeRisk: true);
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: false));
        await engine.ResumeWorkflowAsync(started.InstanceId, "legal", Reply("legal", approved: false));
        var last = await engine.ResumeWorkflowAsync(started.InstanceId, "risk",
            Reply("risk", approved: false));

        Assert.Equal(WorkflowExecutionStatus.Success, last.Status);
        var data = GetData(last);
        Assert.Null(data.ApprovedBy);
        Assert.Equal(1, data.FinanceCompleted);
        Assert.Equal(1, data.LegalCompleted);
        Assert.Equal(1, data.RiskCompleted);
        Assert.Equal(1, data.Finished);
    }

    [Fact]
    public async Task NestedWait_ResumesItsOwnBranchWithoutRepeatingEarlierSteps()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("NestedApprovals")
            .Step<StartActivity>()
            .Parallel(parallel =>
            {
                parallel.Do(branch => branch.Sequence(sequence => sequence
                    .Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceStarted)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceStarted))
                    .If(ctx => ctx.WorkflowData.RequestId == "request-1",
                        then => then.WaitFor<ApprovalReply>("finance", Matches("finance"), setup => setup
                                .Output(reply => reply.Approved).To(ctx => ctx.WorkflowData.FinanceApproved))
                            .Step<CompleteApprovalActivity>(setup => setup
                                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                                .Input(activity => activity.Approver).From(ctx => "finance")
                                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                                .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.FinanceCompleted)))));
                AddLegalBranch(parallel);
            })
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await engine.ResumeWorkflowAsync(started.InstanceId, "legal", Reply("legal", approved: true));
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).FinanceStarted);
        Assert.Equal(1, GetData(completed).FinanceCompleted);
        Assert.Equal(1, GetData(completed).LegalStarted);
        Assert.Equal(1, GetData(completed).LegalCompleted);
        Assert.Equal(1, GetData(completed).Finished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task If_PreservesItsEntryPreviousStepAfterAnActivityOrWait(bool waitInsideIf)
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("IfPreviousStep")
            .Step<StartActivity>(setup => setup
                .Input(activity => activity.Marker).From(ctx => ctx.WorkflowData.RequestId))
            .If(ctx => true,
                then =>
                {
                    if (waitInsideIf)
                        then.WaitFor<ApprovalReply>("finance", Matches("finance"));
                    else
                        then.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceCompleted));
                })
            .Step<CapturePreviousActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Marker)
                .Output(activity => activity.Captured).To(ctx => ctx.WorkflowData.EntrySeen))
            .WaitFor<ApprovalReply>("legal", Matches("legal"))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        var result = waitInsideIf
            ? await engine.ResumeWorkflowAsync(started.InstanceId, "finance", Reply("finance", approved: true))
            : started;

        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal("request-1", GetData(result).EntrySeen);
    }

    [Fact]
    public async Task ActivityWithPrivateOutputSetterPassesValueToNextStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("PrivateOutput")
            .Step<PrivateOutputActivity>()
            .Step<CapturePreviousActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Captured).To(ctx => ctx.WorkflowData.EntrySeen))
            .WaitFor<ApprovalReply>("finance", Matches("finance"))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal("private-output", GetData(started).EntrySeen);
    }

    [Fact]
    public async Task ResumeRestoresPrivateOutputFromAnInjectedActivity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new InjectedMarker("injected-output"));
        services.AddTransient<InjectedOutputActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("InjectedPrevious")
            .Step<InjectedOutputActivity>()
            .If(ctx => true,
                then => then.WaitFor<ApprovalReply>("finance", Matches("finance")))
            .Step<CapturePreviousActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Captured).To(ctx => ctx.WorkflowData.EntrySeen))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("injected-output", GetData(completed).EntrySeen);
    }

    [Fact]
    public async Task ResumeRestoresAnActivityOutputWithAJsonPropertyName()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("RenamedOutput")
            .Step<NamedOutputActivity>()
            .If(ctx => true, then => then.WaitFor<ApprovalReply>("finance", Matches("finance")))
            .Step<CapturePreviousActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Captured).To(ctx => ctx.WorkflowData.EntrySeen))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData { RequestId = "request-1" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "finance",
            Reply("finance", approved: true));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("named-output", GetData(completed).EntrySeen);
    }

    [Fact]
    public async Task GetterOnlyActivityOutputIsRejectedBeforeExecution()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("GetterOnlyOutput")
            .Step<GetterOnlyActivity>()
            .WaitFor<ApprovalReply>("finance")
            .Build();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.ExecuteWorkflowAsync(definition, new ApprovalData()));
    }

    [Fact]
    public async Task WaitAny_StartsSiblingWhileFirstBranchAwaitsIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BranchSignal>();
        services.AddTransient<AwaitSiblingActivity>();
        services.AddTransient<SignalSiblingActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("ConcurrentBranches")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch.Step<AwaitSiblingActivity>(_ => { }))
                .Do(branch => branch.Step<SignalSiblingActivity>(_ => { })))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();

        var result = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(1, GetData(result).Finished);
    }

    [Theory]
    [InlineData(ParallelJoinMode.WaitAny)]
    [InlineData(ParallelJoinMode.WaitConditionally)]
    public async Task JoinWithoutWait_CancelsAndSettlesTheLosingActivity(ParallelJoinMode joinMode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CancellationProbe>();
        services.AddTransient<SlowCancellableActivity>();
        services.AddTransient<CheckCleanupActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("CancelLosingBranch")
            .Step<StartActivity>()
            .Parallel(parallel =>
            {
                if (joinMode == ParallelJoinMode.WaitAny)
                    parallel.WaitAny();
                else
                    parallel.WaitConditionally(data => data.ApprovedBy == "finance");

                parallel.Do(branch => branch.Step<SlowCancellableActivity>(_ => { }));
                parallel.Do(branch => branch.Step<FastApprovalActivity>(setup => setup
                    .Output(activity => activity.Winner).To(ctx => ctx.WorkflowData.ApprovedBy)));
            })
            .Step<CheckCleanupActivity>(_ => { })
            .Build();

        var result = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("finance", GetData(result).ApprovedBy);
        Assert.True(provider.GetRequiredService<CancellationProbe>().Cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task WaitAny_CancelledLoserDoesNotBlockAnIndependentBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BlockedCancellationProbe>();
        services.AddTransient<IgnoreCancellationActivity>();
        services.AddTransient<GateWinnerActivity>();
        services.AddTransient<GateTriggerActivity>();
        services.AddTransient<GateOtherActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<BlockedCancellationProbe>();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<ApprovalData>("IndependentBranchAfterWaitAny")
            .Step<StartActivity>()
            .Parallel(outer => outer
                .Do(branch => branch.Parallel(inner => inner.WaitAny()
                    .Do(child => child.Step<IgnoreCancellationActivity>(_ => { }))
                    .Do(child => child.Step<GateWinnerActivity>(_ => { }))))
                .Do(branch => branch.Step<GateTriggerActivity>(setup => setup
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceCompleted)))
                .Do(branch => branch.Step<GateOtherActivity>(setup => setup
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.LegalCompleted))))
            .Build();

        var execution = provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ApprovalData());
        var otherCommittedBeforeLoserEnded = false;
        try
        {
            await Task.WhenAll(probe.LoserStarted.Task, probe.TriggerStarted.Task,
                    probe.OtherStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            probe.ReleaseWinner.SetResult();
            await probe.LoserCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probe.ReleaseTrigger.SetResult();
            await probe.TriggerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var triggerDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < triggerDeadline)
            {
                var saved = (await repository.GetWorkflowInstancesByNameAsync(definition.Name)).Single();
                if (System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!
                    .FinanceCompleted == 1)
                    break;
                await Task.Delay(25);
            }
            await Task.Delay(100);
            probe.ReleaseOther.SetResult();
            await probe.OtherReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var saved = (await repository.GetWorkflowInstancesByNameAsync(definition.Name)).Single();
                if (System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!
                    .LegalCompleted == 1)
                {
                    otherCommittedBeforeLoserEnded = true;
                    break;
                }
                await Task.Delay(25);
            }
        }
        finally
        {
            probe.ReleaseLoser.TrySetResult();
        }

        var completed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(otherCommittedBeforeLoserEnded,
            "The independent branch must commit while the cancelled WaitAny loser is still running");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, GetData(completed).LegalCompleted);
    }

    [Fact]
    public async Task WaitAny_CancelledLoserDoesNotBlockAnIndependentIf()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BlockedCancellationProbe>();
        services.AddTransient<IgnoreCancellationActivity>();
        services.AddTransient<GateWinnerActivity>();
        services.AddTransient<GateTriggerActivity>();
        services.AddTransient<GateOtherActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<BlockedCancellationProbe>();
        var definition = Workflow.Create<ApprovalData>("IndependentIfAfterWaitAny")
            .Step<StartActivity>()
            .Parallel(outer => outer
                .Do(branch => branch.Parallel(inner => inner.WaitAny()
                    .Do(child => child.Step<IgnoreCancellationActivity>(_ => { }))
                    .Do(child => child.Step<GateWinnerActivity>(_ => { }))))
                .Do(branch => branch.Step<GateTriggerActivity>(_ => { })
                    .If(_ => true, then => then.Step<GateOtherActivity>(_ => { }))))
            .Build();

        var execution = provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ApprovalData());
        var startedBeforeLoserEnded = false;
        try
        {
            await Task.WhenAll(probe.LoserStarted.Task, probe.TriggerStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            probe.ReleaseWinner.SetResult();
            await probe.LoserCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probe.ReleaseTrigger.SetResult();
            await probe.TriggerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startedBeforeLoserEnded = await Task.WhenAny(probe.OtherStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(5))) == probe.OtherStarted.Task;
        }
        finally
        {
            probe.ReleaseWinner.TrySetResult();
            probe.ReleaseTrigger.TrySetResult();
            probe.ReleaseLoser.TrySetResult();
            probe.ReleaseOther.TrySetResult();
        }

        var completed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(startedBeforeLoserEnded,
            "An independent If must advance while a cancelled WaitAny loser is still running");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
    }

    [Fact]
    public async Task WaitAny_CompletedJoinStartsNextStepBeforeUnrelatedActivityEnds()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BlockedCancellationProbe>();
        services.AddTransient<GateWinnerActivity>();
        services.AddTransient<GateTriggerActivity>();
        services.AddTransient<GateOtherActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<BlockedCancellationProbe>();
        var definition = Workflow.Create<ApprovalData>("NestedJoinBeforeUnrelatedActivity")
            .Step<StartActivity>()
            .Parallel(outer => outer
                .Do(branch => branch.Parallel(inner => inner.WaitAny()
                        .Do(child => child.WaitFor<ApprovalReply>("reply"))
                        .Do(child => child.Step<GateWinnerActivity>(_ => { })))
                    .Step<GateTriggerActivity>(_ => { }))
                .Do(branch => branch.Step<GateOtherActivity>(_ => { })))
            .Build();

        var execution = provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ApprovalData());
        var nextStepStartedBeforeOtherEnded = false;
        try
        {
            await probe.OtherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probe.ReleaseWinner.SetResult();
            nextStepStartedBeforeOtherEnded = await Task.WhenAny(probe.TriggerStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(5))) == probe.TriggerStarted.Task;
        }
        finally
        {
            probe.ReleaseWinner.TrySetResult();
            probe.ReleaseTrigger.TrySetResult();
            probe.ReleaseOther.TrySetResult();
        }

        var completed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nextStepStartedBeforeOtherEnded,
            "A completed join must resume its parent while an unrelated activity is running");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
    }

    private static WorkflowDefinition CreateApprovalWorkflow(ParallelJoinMode joinMode, bool includeRisk)
    {
        return Workflow.Create<ApprovalData>("ParallelApprovals")
            .Step<StartActivity>()
            .Parallel(parallel =>
            {
                switch (joinMode)
                {
                    case ParallelJoinMode.WaitAny:
                        parallel.WaitAny();
                        break;
                    case ParallelJoinMode.WaitConditionally:
                        parallel.WaitConditionally(data => data.ApprovedBy != null);
                        break;
                }

                AddFinanceBranch(parallel);
                AddLegalBranch(parallel);
                if (includeRisk)
                    AddRiskBranch(parallel);
            })
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();
    }

    private static WorkflowDefinition CreateRecoverWorkflow() =>
        Workflow.Create<ApprovalData>("RecoverApproval")
            .Step<StartActivity>()
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceStarted)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceStarted))
            .WaitFor<ApprovalReply>("finance", Matches("finance"))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finished)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finished))
            .Build();

    private static void AddFinanceBranch(IParallelBuilder<ApprovalData, StartActivity> parallel) =>
        parallel.Do(branch => branch
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceStarted)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinanceStarted))
            .WaitFor<ApprovalReply>("finance", Matches("finance"), setup => setup
                .Output(reply => reply.Approved).To(ctx => ctx.WorkflowData.FinanceApproved))
            .Step<CompleteApprovalActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinanceCompleted)
                .Input(activity => activity.Approver).From(ctx => "finance")
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.FinanceCompleted)
                .Output(activity => activity.Winner).To(ctx => ctx.WorkflowData.ApprovedBy)));

    private static void AddLegalBranch(IParallelBuilder<ApprovalData, StartActivity> parallel) =>
        parallel.Do(branch => branch
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.LegalStarted)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.LegalStarted))
            .WaitFor<ApprovalReply>("legal", Matches("legal"), setup => setup
                .Output(reply => reply.Approved).To(ctx => ctx.WorkflowData.LegalApproved))
            .Step<CompleteApprovalActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.LegalCompleted)
                .Input(activity => activity.Approver).From(ctx => "legal")
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.LegalCompleted)
                .Output(activity => activity.Winner).To(ctx => ctx.WorkflowData.ApprovedBy)));

    private static void AddRiskBranch(IParallelBuilder<ApprovalData, StartActivity> parallel) =>
        parallel.Do(branch => branch
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.RiskStarted)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.RiskStarted))
            .WaitFor<ApprovalReply>("risk", Matches("risk"), setup => setup
                .Output(reply => reply.Approved).To(ctx => ctx.WorkflowData.RiskApproved))
            .Step<CompleteApprovalActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.RiskCompleted)
                .Input(activity => activity.Approver).From(ctx => "risk")
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.CompletedCount).To(ctx => ctx.WorkflowData.RiskCompleted)
                .Output(activity => activity.Winner).To(ctx => ctx.WorkflowData.ApprovedBy)));

    private static Func<ApprovalReply, WorkflowContext<ApprovalData>, bool> Matches(string approver) =>
        (reply, context) => reply.RequestId == context.WorkflowData.RequestId &&
            reply.ApproverId == approver;

    private static ApprovalReply Reply(string approver, bool approved) =>
        new() { RequestId = "request-1", ApproverId = approver, Approved = approved };

    private static ApprovalData GetData(WorkflowExecutionResult result) =>
        Assert.IsType<ApprovalData>(result.WorkflowData);

    private static ServiceProvider CreateServices(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (repository != null)
            services.AddSingleton(repository);
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class ApprovalData
    {
        public string RequestId { get; set; } = string.Empty;
        public int FinanceStarted { get; set; }
        public int LegalStarted { get; set; }
        public int RiskStarted { get; set; }
        public int FinanceCompleted { get; set; }
        public int LegalCompleted { get; set; }
        public int RiskCompleted { get; set; }
        public bool FinanceApproved { get; set; }
        public bool LegalApproved { get; set; }
        public bool RiskApproved { get; set; }
        public string? ApprovedBy { get; set; }
        public int Finished { get; set; }
        public string? EntrySeen { get; set; }
    }

    public sealed class ApprovalReply
    {
        public string RequestId { get; set; } = string.Empty;
        public string ApproverId { get; set; } = string.Empty;
        public bool Approved { get; set; }
    }

    public sealed class StartActivity : IAsyncActivity
    {
        public string Marker { get; set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class CountActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class CompleteApprovalActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public string Approver { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public int CompletedCount { get; set; }
        public string? Winner { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            CompletedCount = Count + 1;
            Winner = Approved ? Approver : null;
            return Task.CompletedTask;
        }
    }

    public enum ApprovalStatus { Approved, Rejected }

    public sealed class ConvertedOutputActivity : IAsyncActivity
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApprovalStatus Status { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Status = ApprovalStatus.Approved;
            return Task.CompletedTask;
        }
    }

    public sealed class CaptureConvertedOutputActivity : IAsyncActivity
    {
        public ApprovalStatus Status { get; set; }
        public string Value { get; set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = Status.ToString();
            return Task.CompletedTask;
        }
    }

    public sealed class CapturePreviousActivity : IAsyncActivity
    {
        public string Value { get; set; } = string.Empty;
        public string Captured { get; set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Captured = Value;
            return Task.CompletedTask;
        }
    }

    public sealed class PrivateOutputActivity : IAsyncActivity
    {
        public string Value { get; private set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = "private-output";
            return Task.CompletedTask;
        }
    }

    public sealed record InjectedMarker(string Value);

    public sealed class InjectedOutputActivity(InjectedMarker marker) : IAsyncActivity
    {
        public string Value { get; private set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = marker.Value;
            return Task.CompletedTask;
        }
    }

    public sealed class NamedOutputActivity : IAsyncActivity
    {
        [JsonPropertyName("saved_value")]
        public string Value { get; private set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = "named-output";
            return Task.CompletedTask;
        }
    }

    public sealed class GetterOnlyActivity : IAsyncActivity
    {
        public string Value { get; } = "getter-only";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class BranchSignal
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ResumeGateProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class BlockApprovalActivity(ResumeGateProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed class AwaitSiblingActivity(BranchSignal signal) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await signal.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public sealed class SignalSiblingActivity(BranchSignal signal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            signal.Started.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    public sealed class CancellationProbe
    {
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class BlockedCancellationProbe
    {
        public TaskCompletionSource LoserStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TriggerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoserCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TriggerReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWinner { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTrigger { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOther { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoser { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class IgnoreCancellationActivity(BlockedCancellationProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() =>
                probe.LoserCancelled.TrySetResult());
            probe.LoserStarted.TrySetResult();
            await probe.ReleaseLoser.Task;
        }
    }

    public sealed class GateWinnerActivity(BlockedCancellationProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            probe.ReleaseWinner.Task;
    }

    public sealed class GateOtherActivity(BlockedCancellationProbe probe) : IAsyncActivity
    {
        public int Result { get; private set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.OtherStarted.TrySetResult();
            await probe.ReleaseOther.Task;
            Result = 1;
            probe.OtherReturned.TrySetResult();
        }
    }

    public sealed class GateTriggerActivity(BlockedCancellationProbe probe) : IAsyncActivity
    {
        public int Result { get; private set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.TriggerStarted.TrySetResult();
            await probe.ReleaseTrigger.Task;
            Result = 1;
            probe.TriggerReturned.TrySetResult();
        }
    }

    public sealed class SlowCancellableActivity(CancellationProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                probe.Cancelled.TrySetResult(true);
                throw;
            }
        }
    }

    public sealed class CheckCleanupActivity(CancellationProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (!probe.Cancelled.Task.IsCompleted)
                throw new InvalidOperationException("The losing branch is still running");
            return Task.CompletedTask;
        }
    }

    public sealed class FastApprovalActivity : IAsyncActivity
    {
        public string Winner { get; private set; } = string.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Winner = "finance";
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Approval branch failed");
    }

    private sealed class RecordingRepository : IWorkflowStateRepository
    {
        private readonly InMemoryWorkflowStateRepository _inner = new();
        public Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.RenewExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
            _inner.ReleaseExecutionLeaseAsync(instanceId, token);

        public WorkflowInstance? Initial { get; private set; }
        public WorkflowInstance? AfterFinance { get; private set; }
        public int FailOnFinanceStartCount { get; set; }

        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
        {
            ObserveSave(instance);
            return _inner.SaveWorkflowInstanceAsync(instance);
        }

        public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
            WorkflowInstance instance, long expectedRevision, string commitId)
        {
            ObserveSave(instance);
            return _inner.CommitWorkflowInstanceAsync(instance, expectedRevision, commitId);
        }

        private void ObserveSave(WorkflowInstance instance)
        {
            if (FailOnFinanceStartCount > 0 && instance.Status == WorkflowStatus.Running &&
                System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(instance.WorkflowDataJson)?.FinanceStarted == 1)
            {
                FailOnFinanceStartCount--;
                throw new IOException("The checkpoint store is temporarily unavailable");
            }
            if (instance.Status == WorkflowStatus.Running && !string.IsNullOrWhiteSpace(instance.ExecutionStateJson))
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<WorkflowInstance>(
                    System.Text.Json.JsonSerializer.Serialize(instance))!;
                Initial ??= copy;
                if (System.Text.Json.JsonSerializer.Deserialize<ApprovalData>(instance.WorkflowDataJson)?.FinanceStarted == 1)
                    AfterFinance ??= copy;
            }
        }

        public Task<bool> TryClaimSuspendedWorkflowAsync(WorkflowInstance instance) =>
            _inner.TryClaimSuspendedWorkflowAsync(instance);

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            _inner.GetWorkflowInstancesByNameAsync(workflowName);

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            _inner.GetWorkflowInstancesByStatusAsync(status);

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);

        public Task DeleteWorkflowInstanceAsync(string instanceId) => _inner.DeleteWorkflowInstanceAsync(instanceId);

        public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync() =>
            _inner.GetSuspendedWorkflowsReadyForResumptionAsync();
    }
}
