using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.SyntaxTests;

[Trait("Feature", "saga")]
public sealed class SagaRetryPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void IntegerOverloadsUseTheSameBackoff(int attempts)
    {
        var definition = Workflow.Create<Data>("DefaultRetryPolicy")
            .Step<Noop>()
            .Saga(saga => saga.Step<Noop>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(attempts))
                .OnError(handler => handler.ThenRetry(attempts))))
            .OnError(handler => handler.Compensate().ThenRetry(attempts))
            .Build();
        var saga = definition.Steps[1];
        var stepHandlers = Assert.IsType<List<StepErrorHandlerInfo>>(
            Assert.Single(saga.SequenceSteps).StepMetadata["StepErrorHandlers"]);
        Assert.Equal(2, stepHandlers.Count);
        var sagaPolicy = Assert.IsType<SagaErrorConfiguration>(
            Assert.Single(saga.CatchBlocks).StepMetadata["SagaErrorConfig"]).RetryPolicy;

        foreach (var policy in stepHandlers.Select(handler => handler.RetryPolicy).Append(sagaPolicy))
        {
            Assert.NotNull(policy);
            Assert.Equal(attempts, policy.MaximumAttempts);
            Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialInterval);
            Assert.Equal(TimeSpan.FromSeconds(10), policy.MaximumInterval);
            Assert.Equal(2, policy.BackoffCoefficient);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPoliciesArePreserved(bool immediate)
    {
        var policy = immediate
            ? new RetryPolicy(4)
            : new RetryPolicy(TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(50), 4, 3);
        var definition = Workflow.Create<Data>("ExplicitRetryPolicy")
            .Step<Noop>()
            .Saga(saga => saga.Step<Noop>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(policy))
                .OnError(handler => handler.ThenRetry(policy))))
            .OnError(handler => handler.Compensate().ThenRetry(policy))
            .Build();
        var saga = definition.Steps[1];
        var stepHandlers = Assert.IsType<List<StepErrorHandlerInfo>>(
            Assert.Single(saga.SequenceSteps).StepMetadata["StepErrorHandlers"]);
        Assert.Equal(2, stepHandlers.Count);
        Assert.All(stepHandlers, handler => Assert.Same(policy, handler.RetryPolicy));
        Assert.Same(policy, Assert.IsType<SagaErrorConfiguration>(
            Assert.Single(saga.CatchBlocks).StepMetadata["SagaErrorConfig"]).RetryPolicy);
        Assert.Equal(4, policy.MaximumAttempts);
        Assert.Equal(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(7), policy.InitialInterval);
        Assert.Equal(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50), policy.MaximumInterval);
        Assert.Equal(immediate ? 1 : 3, policy.BackoffCoefficient);
    }

    [Fact]
    public void DefaultHandlerBackoffChangesTheStructureHash()
    {
        static WorkflowDefinition Build(RetryPolicy? policy) => Workflow.Create<Data>("RetryFingerprint")
            .Step<Noop>()
            .Saga(saga => saga.Step<Noop>())
            .OnError(handler =>
            {
                var continuation = handler.Compensate();
                if (policy == null)
                    continuation.ThenRetry(3);
                else
                    continuation.ThenRetry(policy);
            })
            .Build();

        var structureHash = new WorkflowScopeCatalog(Build(null)).StructureHash;
        Assert.Equal(new WorkflowScopeCatalog(Build(new RetryPolicy { MaximumAttempts = 3 })).StructureHash,
            structureHash);
        Assert.NotEqual(new WorkflowScopeCatalog(Build(new RetryPolicy(3))).StructureHash, structureHash);
    }

    public sealed class Data;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
