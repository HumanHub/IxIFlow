using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace IxIFlow.Tests.ExecutionTests;

public class SagaResumeRegressionTests
{
    [Fact]
    public async Task ResumeInsideSaga_RunsRemainingSagaActivityBeforeFollowingWorkflowStep()
    {
        var definition = Workflow.Create<SagaRegressionData>("SagaResumeOrder")
            .Step<SagaSetupActivity>(_ => { })
            .Saga(saga => saga
                .Step<SagaBeforeActivity>(setup => setup
                    .Output(a => a.Completed).To(ctx => ctx.WorkflowData.BeforeCompleted)
                    .CompensateWith<SagaUndoActivity>(undo => undo
                        .Output(a => a.Compensated).To(ctx => ctx.WorkflowData.BeforeCompensated)))
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<SagaAfterActivity>(setup => setup
                    .Output(a => a.Completed).To(ctx => ctx.WorkflowData.AfterCompleted)))
            .Step<SagaFollowingActivity>(setup => setup
                .Output(a => a.Completed).To(ctx => ctx.WorkflowData.FollowingCompleted))
            .Build();

        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var suspended = await engine.ExecuteWorkflowAsync(definition, new SagaRegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        Assert.True(((SagaRegressionData)suspended.WorkflowData!).BeforeCompleted);
        var stored = await scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(suspended.InstanceId);
        Assert.NotNull(stored);
        Assert.True(JsonSerializer.Deserialize<SagaRegressionData>(stored.WorkflowDataJson)!.BeforeCompleted);
        Assert.Equal(definition.Steps[1].SequenceSteps[1].Id,
            Assert.Single(stored.ExecutionSnapshot!.Pointers).StepId);
        Assert.Contains(stored.ExecutionSnapshot.Frames, frame => frame.Kind == WorkflowStepType.Saga.ToString());

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        var data = Assert.IsType<SagaRegressionData>(resumed.WorkflowData);
        Assert.True(data.BeforeCompleted);
        Assert.True(data.AfterCompleted, $"Saga activity did not run; following={data.FollowingCompleted}, error={resumed.ErrorMessage}");
        Assert.True(data.FollowingCompleted, $"Workflow activity did not run; error={resumed.ErrorMessage}");
    }

    [Fact]
    public async Task FailureAfterSagaResume_CompensatesWorkCompletedBeforeSuspension()
    {
        var definition = Workflow.Create<SagaRegressionData>("SagaResumeCompensation")
            .Step<SagaSetupActivity>(_ => { })
            .Saga(saga => saga
                .Step<SagaBeforeActivity>(setup => setup
                    .Output(a => a.Completed).To(ctx => ctx.WorkflowData.BeforeCompleted)
                    .CompensateWith<SagaUndoActivity>(undo => undo
                        .Output(a => a.Compensated).To(ctx => ctx.WorkflowData.BeforeCompensated)))
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<SagaFailActivity>(setup => setup
                    .Input(a => a.ShouldFail).From(_ => true)))
            .Build();

        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var suspended = await engine.ExecuteWorkflowAsync(definition, new SagaRegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        Assert.True(((SagaRegressionData)suspended.WorkflowData!).BeforeCompleted);

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        var data = Assert.IsType<SagaRegressionData>(resumed.WorkflowData);
        Assert.True(data.BeforeCompleted);
        Assert.True(data.BeforeCompensated);
    }

    [Fact]
    public async Task MultipleSagaSuspensions_KeepEarlierCompensationCheckpoint()
    {
        var definition = Workflow.Create<SagaRegressionData>("SagaMultipleSuspensions")
            .Step<SagaSetupActivity>(_ => { })
            .Saga(saga => saga
                .Step<SagaBeforeActivity>(setup => setup
                    .Output(a => a.Completed).To(ctx => ctx.WorkflowData.BeforeCompleted)
                    .CompensateWith<SagaUndoActivity>(undo => undo
                        .Output(a => a.Compensated).To(ctx => ctx.WorkflowData.BeforeCompensated)))
                .Suspend<RegressionApprovalEvent>("first approval", (evt, _) => evt.Approved)
                .Step<SagaAfterActivity>(setup => setup
                    .Output(a => a.Completed).To(ctx => ctx.WorkflowData.AfterCompleted))
                .Suspend<RegressionApprovalEvent>("second approval", (evt, _) => evt.Approved)
                .Step<SagaFailActivity>(setup => setup
                    .Input(a => a.ShouldFail).From(_ => true)))
            .Build();

        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var first = await engine.ExecuteWorkflowAsync(definition, new SagaRegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);

        using var secondScope = provider.CreateScope();
        var second = await secondScope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(first.InstanceId, new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Suspended, second.Status);
        Assert.True(Assert.IsType<SagaRegressionData>(second.WorkflowData).AfterCompleted);

        using var finalScope = provider.CreateScope();
        var final = await finalScope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(first.InstanceId, new RegressionApprovalEvent { Approved = true });
        var data = Assert.IsType<SagaRegressionData>(final.WorkflowData);
        Assert.True(data.BeforeCompensated);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }
}

public sealed class SagaRegressionData
{
    public bool BeforeCompleted { get; set; }
    public bool BeforeCompensated { get; set; }
    public bool AfterCompleted { get; set; }
    public bool FollowingCompleted { get; set; }
}

public sealed class SagaSetupActivity : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class SagaBeforeActivity : IAsyncActivity
{
    public bool Completed { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Completed = true;
        return Task.CompletedTask;
    }
}

public sealed class SagaUndoActivity : IAsyncActivity
{
    public bool Compensated { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Compensated = true;
        return Task.CompletedTask;
    }
}

public sealed class SagaAfterActivity : IAsyncActivity
{
    public bool Completed { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Completed = true;
        return Task.CompletedTask;
    }
}

public sealed class SagaFollowingActivity : IAsyncActivity
{
    public bool Completed { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Completed = true;
        return Task.CompletedTask;
    }
}

public sealed class SagaFailActivity : IAsyncActivity
{
    public bool ShouldFail { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        ShouldFail ? throw new InvalidOperationException("Failure after resume") : Task.CompletedTask;
}
