using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "W-01")]
public sealed class FlowchartEventHandoffScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DebuggerWakeThatTakesAnEventDoesNotLetItsCallerRunTheNextTimeout(string profile)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddIxIFlow(options => options.Debugging = true);
        registrations.AddSingleton(repository);

        if (time != null)
            registrations.AddSingleton<TimeProvider>(time);

        registrations.AddBreakpointConditions();
        store.ConfigureHost(registrations);
        await using var services = registrations.BuildServiceProvider();
        await store.StartHostLinksAsync(services);

        await FlowchartEventHandoffScenario.VerifyAsync(services,
            id => FlowchartScenarios.WaitForTimeoutAsync(repository, id, time),
            timeout: time == null ? TimeSpan.FromSeconds(5) : null);
    }
}