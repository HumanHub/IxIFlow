using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class ReviewFixCatalogDebugTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAcquisitionCancelsStoreRegistrationOrTheRegistrationLock(bool holdLock)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalogStore = PublicationServiceWaitTests.CatalogProbe.Wrap(store, out var probe);
        var entered = Signal();
        var release = Signal();
        CancellationToken storeToken = default;

        probe.Intercept = (method, args) => method.Name == nameof(IWorkflowCatalogStore.RegisterWorkflowVersionAsync)
            ? RegisterAsync(args!)
            : null;

        var definition = new WorkflowBuilder<Data, object>([], "CancelableInitialization").WaitFor<string>("go").Build();
        using var catalog = new WorkflowCatalog(catalogStore, [definition]);

        var gate = (SemaphoreSlim)typeof(WorkflowCatalog).GetField("_registrationLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(catalog)!;

        if (holdLock)
            await gate.WaitAsync();

        using var cancel = new CancellationTokenSource();
        var acquiring = catalog.AcquireWorkflowDefinitionAsync(definition.Name, 1, null, cancel.Token);

        try
        {
            if (!holdLock)
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(acquiring.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring.WaitAsync(TimeSpan.FromSeconds(5)));

            if (holdLock)
                Assert.False(entered.Task.IsCompleted);
            else
                Assert.True(storeToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();

            if (holdLock)
                gate.Release();

            try
            {
                using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Task RegisterAsync(object?[] args)
        {
            storeToken = (CancellationToken)args[^1]!;
            entered.TrySetResult();
            await release.Task.WaitAsync(storeToken);
            await store.RegisterWorkflowVersionAsync((WorkflowVersion)args[0]!, cancellationToken: storeToken);
        }
    }

    [Fact]
    public async Task JoiningInitializerCancelsOnlyItsOwnWait()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalogStore = PublicationServiceWaitTests.CatalogProbe.Wrap(store, out var probe);
        var entered = Signal();
        var release = Signal();
        CancellationToken initializerToken = default;

        probe.Intercept = (method, args) => method.Name == nameof(IWorkflowCatalogStore.RegisterWorkflowVersionAsync)
            ? RegisterAsync(args!)
            : null;

        var definition = new WorkflowBuilder<Data, object>([], "JoinedInitialization").WaitFor<string>("go").Build();
        using var catalog = new WorkflowCatalog(catalogStore, [definition]);
        using var owner = new CancellationTokenSource();
        var initializing = catalog.InitializeAsync(owner.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var joiner = new CancellationTokenSource();
        var joining = catalog.InitializeAsync(joiner.Token);

        try
        {
            Assert.False(joining.IsCompleted);
            joiner.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joining.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(initializerToken.IsCancellationRequested);
            Assert.False(initializing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await initializing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Same(definition, await catalog.GetWorkflowDefinitionAsync(definition.Name, 1));
        Assert.NotNull(await store.GetWorkflowVersionAsync(definition.Name, 1));

        async Task RegisterAsync(object?[] args)
        {
            initializerToken = (CancellationToken)args[^1]!;
            entered.TrySetResult();
            await release.Task.WaitAsync(initializerToken);
            await store.RegisterWorkflowVersionAsync((WorkflowVersion)args[0]!, cancellationToken: initializerToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SecondaryOrderingPreservesItsTypeOrderAndEnumerationBudget(bool staticCall, bool descending)
    {
        var method = descending ? "ThenByDescending" : "ThenBy";
        const string primary = "context.WorkflowData.Items.OrderBy(i => i % 2)";
        var ordered = staticCall ? $"Enumerable.{method}({primary}, i => i)" : $"{primary}.{method}(i => i)";
        using var compiler = new BreakpointConditionCompiler();
        var result = compiler.Compile(new("ordered", $"{ordered}.First() == {(descending ? 4 : 2)}", typeof(Data)));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Evaluate);
        Assert.True(result.Evaluate(new Data(), null, null, null, DateTimeOffset.UtcNow));

        var large = new Data { Items = Enumerable.Range(0, 20_000).ToList() };
        Assert.Throws<TimeoutException>(() => result.Evaluate(large, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task LaunchWithDebuggingDisabledHasTheInvalidRequestKind()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<IWorkflowOperations>();

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.LaunchAsync(
            "disabled-launch", "Workflow", 1, JsonSerializer.SerializeToElement(new { }), false, "actor"));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Data
    {
        public List<int> Items { get; set; } = [3, 4, 1, 2];
    }
}