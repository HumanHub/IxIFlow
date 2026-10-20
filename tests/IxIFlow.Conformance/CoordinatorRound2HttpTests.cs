using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class CoordinatorRound2HttpTests
{
    [Theory]
    [InlineData(StoreProfiles.LocalMemory)]
    [InlineData(StoreProfiles.LocalSqlite)]
    public async Task HiddenBacklogHasABoundedCandidateBudgetAndProtectedContinuation(string profile)
    {
        var time = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var inner = database.NewRepository();
        for (var index = 0; index < 206; index++)
            await Seed(inner, time, $"hidden-resource-{index:D4}", index == 205 ? "visible" : "hidden", "Budget");
        var store = DispatchProxy.Create<IWorkflowStateRepository, StoreProbe>();
        var probe = (StoreProbe)(object)store;
        probe.Inner = inner;
        await using var factory = new OperationsHttpFactory(store);
        factory.ConfigureServices = services => services.RemoveAll<IHostedService>();
        factory.Scope.Instances = query => query with { CorrelationId = "visible" };
        using var reader = factory.Operator("read");
        probe.Calls.Clear();
        string? cursor = null;
        for (var request = 0; request < 3; request++)
        {
            using var response = await reader.GetAsync("/ops/unschedulable?limit=1" +
                (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(request == 2 ? 1 : 0, page.GetProperty("items").GetArrayLength());
            cursor = page.GetProperty("nextCursor").GetString();
            Assert.Equal(request + 1, probe.Count(nameof(IWorkflowStateRepository.GetUnschedulableAsync)));
            Assert.Equal(Math.Min((request + 1) * 100, 206), probe.Count(nameof(IWorkflowStateRepository.QueryInstancesAsync)));
            if (request < 2)
            {
                Assert.False(string.IsNullOrWhiteSpace(cursor));
                Assert.DoesNotContain("hidden-resource", cursor);
                // Protection also rejects modifications before performing any store read.
                var before = probe.Count(nameof(IWorkflowStateRepository.GetUnschedulableAsync));
                var tampered = (cursor![0] == 'A' ? "B" : "A") + cursor[1..];
                Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(
                    "/ops/unschedulable?cursor=" + Uri.EscapeDataString(tampered))).StatusCode);
                Assert.Equal(before, probe.Count(nameof(IWorkflowStateRepository.GetUnschedulableAsync)));
            }
            else
            {
                Assert.Null(cursor);
                Assert.Equal("hidden-resource-0205", Assert.Single(page.GetProperty("items").EnumerateArray())
                    .GetProperty("instanceId").GetString());
            }
        }
    }

    [Theory]
    [InlineData(StoreProfiles.LocalMemory)]
    [InlineData(StoreProfiles.LocalSqlite)]
    public async Task AuthorizingAReplacementCannotDiscloseThePreviousIncarnation(string profile)
    {
        var time = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var inner = database.NewRepository();
        var old = await Seed(inner, time, "reused", "hidden", "SecretWorkflow");
        var store = DispatchProxy.Create<IWorkflowStateRepository, StoreProbe>();
        var probe = (StoreProbe)(object)store;
        probe.Inner = inner;
        WorkflowInstance? replacement = null;
        probe.BeforeQuery = async () =>
        {
            await inner.DeleteWorkflowInstanceAsync("reused", TimeSpan.FromTicks(1));
            time.Advance(TimeSpan.FromTicks(2));
            replacement = await Seed(inner, time, "reused", "visible", "PublicWorkflow");
        };
        await using var factory = new OperationsHttpFactory(store);
        factory.ConfigureServices = services => services.RemoveAll<IHostedService>();
        factory.Scope.Instances = query => query with { CorrelationId = "visible" };
        using var reader = factory.Operator("read");
        using var response = await reader.GetAsync("/ops/unschedulable");
        Assert.Null(probe.Error);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(replacement);
        Assert.NotEqual(old.Incarnation, replacement.Incarnation);
        var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.DoesNotContain("SecretWorkflow", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WorkflowInstance> Seed(IWorkflowStateRepository store, TimeProvider time,
        string id, string correlation, string name)
    {
        var definition = Workflow.Create<Data>(name).Demand("region", "eu").Step<Noop>().Build();
        var (instance, checkpoint, catalog) = WorkflowInstances.NewInstance(definition, new Data(),
            new() { InstanceId = id, CorrelationId = correlation }, time.GetUtcNow().UtcDateTime);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), catalog, time.GetUtcNow().UtcDateTime);
        await store.SeedAsync(instance, TimeSpan.Zero);
        return instance;
    }

    public class StoreProbe : DispatchProxy
    {
        internal IWorkflowStateRepository Inner = null!;
        internal Func<Task>? BeforeQuery;
        internal Exception? Error;
        internal readonly ConcurrentDictionary<string, int> Calls = new();
        internal int Count(string method) => Calls.GetValueOrDefault(method);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.AddOrUpdate(method!.Name, 1, (_, count) => count + 1);
            if (method.Name == nameof(IWorkflowStateRepository.QueryInstancesAsync) &&
                Interlocked.Exchange(ref BeforeQuery, null) is { } before)
                return QueryAfterAsync(before, (InstanceQuery)args![0]!);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
        private async Task<InstancePage> QueryAfterAsync(Func<Task> before, InstanceQuery query)
        {
            try
            {
                await before();
                return await Inner.QueryInstancesAsync(query);
            }
            catch (Exception error) { Error = error; throw; }
        }
    }
}
