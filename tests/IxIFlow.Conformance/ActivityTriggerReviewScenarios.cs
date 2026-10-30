using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow")]
public sealed class ActivityTriggerReviewScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ReplyIntakeDefersSelectedFireUntilItsJoinCompletes(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository);
        await ActivityTriggerReviewCases.ReplyIntakeDefersSelectedFireUntilJoinCompletes(host, repository);
    }

    public static IEnumerable<object[]> HydrationCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "fire", "data", "held", "receipt" }.Select(shape => new object[] { profile, shape }));

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellationPreservesTheSelectedWinnerInsideFinallyAcrossRecovery(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository);
        await ActivityTriggerReviewCases.CancellationPreservesCleanupWinner(host, repository);
    }

    [Theory, MemberData(nameof(HydrationCases))]
    public async Task RecoveryHydratesOnlyNeededTriggerResults(string profile, string shape)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository);
        await ActivityTriggerReviewCases.RecoveryOnlyHydratesNeededResults(host, repository, shape);
    }

    private static Host Services(ProfileStore store, IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
            o.MaxHydratedPayloadBytes = ActivityTriggerReviewCases.HydrationBudget;
        });

        services.AddSingleton(repository);
        services.AddSingleton(new ActivityTriggerReviewCases.Probe());
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();

        try
        {
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return new(provider);
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();

            throw;
        }
    }

    private sealed class Host(ServiceProvider provider) : IServiceProvider, IAsyncDisposable
    {
        public object? GetService(Type type) => provider.GetService(type);

        public async ValueTask DisposeAsync()
        {
            await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }
}