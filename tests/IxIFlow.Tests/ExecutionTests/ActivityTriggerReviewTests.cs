using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
public sealed class ActivityTriggerReviewTests
{
    [Fact]
    public async Task ReplyIntakeDefersSelectedFireUntilItsJoinCompletes()
    {
        await using var host = Services();

        await ActivityTriggerReviewCases.ReplyIntakeDefersSelectedFireUntilJoinCompletes(host,
            host.GetRequiredService<IWorkflowStateRepository>());
    }

    [Fact]
    public async Task CancellationPreservesTheSelectedWinnerInsideFinallyAcrossRecovery()
    {
        await using var host = Services();

        await ActivityTriggerReviewCases.CancellationPreservesCleanupWinner(host,
            host.GetRequiredService<IWorkflowStateRepository>());
    }

    [Theory]
    [InlineData("fire")]
    [InlineData("data")]
    [InlineData("held")]
    [InlineData("receipt")]
    public async Task RecoveryHydratesOnlyNeededTriggerResults(string shape)
    {
        await using var host = Services();

        await ActivityTriggerReviewCases.RecoveryOnlyHydratesNeededResults(host,
            host.GetRequiredService<IWorkflowStateRepository>(), shape);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
            o.MaxHydratedPayloadBytes = ActivityTriggerReviewCases.HydrationBudget;
        });

        services.AddSingleton(new ActivityTriggerReviewCases.Probe());

        return services.BuildServiceProvider();
    }
}