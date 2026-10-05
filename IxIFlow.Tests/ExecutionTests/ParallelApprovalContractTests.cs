using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

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

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
    }

    public sealed class ApprovalReply
    {
        public string RequestId { get; set; } = string.Empty;
        public string ApproverId { get; set; } = string.Empty;
        public bool Approved { get; set; }
    }

    public sealed class StartActivity : IAsyncActivity
    {
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
}
