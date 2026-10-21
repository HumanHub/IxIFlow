using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryResolutionScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SimultaneousDecisionsAcceptOneTargetAndNeverResolveItsReplacement(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile,
            services => services.AddSingleton(new ExecutionLimits { MaxCheckpointBytes = 18_000, VerifyCheckpointParts = true }));
        var first = cluster.AddHost();
        var second = profile == StoreProfiles.LocalSqlite ? first : cluster.AddHost();
        var child = Workflow.Create<Data>("RacingResolutionChild").Build();
        await cluster.Registry.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "RacingInvocationResolution")
            .Invoke<Data>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => new string('x', 100_000))
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();
        var stopped = await first.RunAsync(parent, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var target = Assert.Single(await first.GetPendingActivitiesAsync(stopped.InstanceId));
        var outcomes = await Task.WhenAll(Decide(first), Decide(second));
        Assert.Single(outcomes, accepted => accepted);
        var decisions = (await first.GetTraceAsync(stopped.InstanceId)).Select(entry => entry.Payload).OfType<ActivityResolutionTrace>();
        Assert.Equal(target.PolicyAttemptId, Assert.Single(decisions).PolicyAttemptId);
        var replacement = Assert.Single(await second.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.NotEqual(target.PolicyAttemptId, replacement.PolicyAttemptId);
        var before = (await cluster.GetInstanceAsync(stopped.InstanceId))!.Revision;
        var stale = await Assert.ThrowsAsync<WorkflowEngineException>(() => second.ResolveAsync(stopped.InstanceId,
            target.InvocationId, ActivityResolution.CompletedInvocation(new Data { Value = "stale" }, expectedPolicyAttemptId: target.PolicyAttemptId)));
        Assert.Equal(WorkflowErrorKind.InvalidState, stale.Kind);
        Assert.Equal(before, (await cluster.GetInstanceAsync(stopped.InstanceId))!.Revision);
        Assert.Equal(WorkflowExecutionStatus.Success, (await second.ResolveAsync(stopped.InstanceId, replacement.InvocationId,
            ActivityResolution.CompletedInvocation(new Data { Value = "observed" }, expectedPolicyAttemptId: replacement.PolicyAttemptId))).Status);
        Assert.Empty((await cluster.NewRepository().QueryInstancesAsync(new InstanceQuery(ParentInstanceId: stopped.InstanceId))).Items);

        async Task<bool> Decide(IWorkflowClient client)
        {
            try
            {
                var result = await client.ResolveAsync(stopped.InstanceId, target.InvocationId,
                    ActivityResolution.Failed("observed failure", expectedPolicyAttemptId: target.PolicyAttemptId));
                return result.Status == WorkflowExecutionStatus.NeedsResolution;
            }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidState) { return false; }
        }
    }

    public sealed class Data { public string Value { get; set; } = ""; }
}
