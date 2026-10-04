using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

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

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        var data = Assert.IsType<SagaRegressionData>(resumed.WorkflowData);
        Assert.True(data.BeforeCompleted);
        Assert.True(data.AfterCompleted);
        Assert.True(data.FollowingCompleted);
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
