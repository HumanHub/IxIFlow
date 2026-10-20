using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class OperationsHttpTests
{
    public static TheoryData<string, string, string> Routes => new()
    {
        { "GET", "/instances", "read" }, { "GET", "/instances/id", "read" },
        { "GET", "/instances/id/waits", "read" }, { "GET", "/instances/id/waits?tokens=true", "reply" },
        { "GET", "/waits", "reply" }, { "GET", "/instances/id/pending-activities", "read" },
        { "GET", "/instances/id/trace", "inspect" }, { "GET", "/instances/id/snapshot", "inspect" },
        { "POST", "/instances/id/resolutions", "write" }, { "POST", "/instances/id/release", "write" },
        { "POST", "/instances/id/cancel", "write" }, { "POST", "/instances/id/terminate", "write" },
        { "GET", "/outbox/publishers", "outbox" }, { "POST", "/outbox/publishers/name/enable", "outbox" },
        { "DELETE", "/outbox/publishers/name", "outbox" }, { "GET", "/outbox/deliveries", "outbox" },
        { "POST", "/outbox/deliveries/event/name/retry", "outbox" }
    };

    [Theory, MemberData(nameof(Routes))]
    public async Task EveryRouteRequiresAuthenticationAndItsPolicy(string method, string path, string policy)
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.SendAsync(new(new HttpMethod(method), "/ops" + path));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var denied = factory.Operator(string.Join(' ', new[] { "read", "reply", "inspect", "write", "outbox" }.Except([policy])));
        using var forbidden = await denied.SendAsync(new(new HttpMethod(method), "/ops" + path));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task InstanceAuthorizationAndInspectRequiringReplyAreEnforced()
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var http = factory.Operator();
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        factory.DenyInstance = id;
        foreach (var suffix in new[] { "", "/waits", "/waits?tokens=true", "/pending-activities", "/trace", "/snapshot" })
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/ops/instances/" + id + suffix)).StatusCode);
        factory.DenyInstance = null;
        using var inspectOnly = factory.Operator("inspect");
        Assert.Equal(HttpStatusCode.Forbidden, (await inspectOnly.GetAsync($"/ops/instances/{id}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspectOnly.GetAsync($"/ops/instances/{id}/snapshot")).StatusCode);
        using var readOnly = factory.Operator("read");
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync($"/ops/instances/{id}/waits?tokens=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"/ops/instances/{id}/waits")).StatusCode);
    }

    [Fact]
    public async Task ScopingHappensBeforeSearchPagingAndTokenDiscovery()
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var http = factory.Operator();
        var allowed = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var result = await factory.Engine.RunAsync(Waiting(), new Data(), new WorkflowOptions { CorrelationId = i % 2 == 0 ? "allowed" : "hidden" });
            if (i % 2 == 0) allowed.Add(result.InstanceId);
        }
        factory.Scope.Instances = query => query with { CorrelationId = "allowed" };
        factory.Scope.Waits = query => query with { InstanceIds = allowed };
        var found = new List<string>();
        string? after = null;
        do
        {
            var page = await http.GetFromJsonAsync<InstancePage>("/ops/instances?limit=1" + (after == null ? "" : "&after=" + Uri.EscapeDataString(after)));
            found.Add(Assert.Single(page!.Items).InstanceId);
            after = page.NextCursor;
        } while (after != null);
        Assert.Equal(allowed.Order(), found.Order());
        found.Clear(); after = null;
        do
        {
            var page = await http.GetFromJsonAsync<WorkflowWaitPage>("/ops/waits?limit=1" + (after == null ? "" : "&after=" + Uri.EscapeDataString(after)));
            found.Add(Assert.Single(page!.Items).InstanceId);
            after = page.NextAfter;
        } while (after != null);
        Assert.Equal(allowed.Order(), found.Order());
    }

    [Theory]
    [InlineData(false, false, "operator")]
    [InlineData(true, false, null)]
    [InlineData(true, true, "delegated")]
    public async Task ActorsComeFromThePrincipalOrAuthorizedDelegation(bool header, bool actFor, string? expected)
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store) { ConfigureOptions = o => o.ActForPolicy = actFor ? "act-for" : null };
        using var http = factory.Operator();
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        if (header) http.DefaultRequestHeaders.Add("X-IxIFlow-Actor", "delegated");
        using var response = await http.PostAsJsonAsync($"/ops/instances/{id}/terminate", new { reasonCode = "test" });
        Assert.Equal(expected == null ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        var trace = await store.GetTraceAsync(id, 0, 100);
        if (expected == null) Assert.DoesNotContain(trace, t => t.Entry.EntryType == TraceEntryType.WorkflowTerminated);
        else Assert.Contains(trace, t => t.Entry.Payload is CancellationTrace cancellation && cancellation.Actor == expected);
    }

    [Fact]
    public async Task AConfiguredActForPolicyMustAlsoSucceed()
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository()) { ConfigureOptions = o => o.ActForPolicy = "act-for" };
        using var http = factory.Operator("write");
        http.DefaultRequestHeaders.Add("X-IxIFlow-Actor", "delegated");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/ops/instances/id/terminate", new { reasonCode = "test" })).StatusCode);
    }

    [Fact]
    public async Task ResolutionConvertsDeclaredOutputsAndWaitsOnlyAfterDurableAcceptance()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        var definition = Workflow.Create<Data>("HttpResolution")
            .Step<Uncertain>(step => step.Output(activity => activity.Number).To(context => context.WorkflowData.Number))
            .Step<Block>().Build();
        await factory.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var pending = new ActivityInvocationState
        {
            StepId = scopes.Id(definition.Steps[0]), StartedAtUtc = DateTime.UtcNow,
            Attempts = [new ActivityAttemptState { StartedAtUtc = DateTime.UtcNow }], ResolutionReason = "unknown external outcome"
        };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = scopes.StructureHash,
            Continuations = [new ContinuationState
            {
                Status = ContinuationStatus.WaitingResolution, PendingActivity = pending,
                Stack = [new ScopePosition { ScopeId = "root" }]
            }]
        };
        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = "resolution", WorkflowName = definition.Name, WorkflowVersion = definition.Version,
            CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Status = WorkflowStatus.NeedsResolution,
            WorkflowDataJson = JsonSerializer.Serialize(new Data()), WorkflowDataType = typeof(Data).AssemblyQualifiedName!,
            ExecutionStateJson = JsonSerializer.Serialize(checkpoint)
        });
        var activities = await http.GetFromJsonAsync<PendingActivityInfo[]>("/ops/instances/resolution/pending-activities");
        Assert.Equal(pending.Id, Assert.Single(activities!).InvocationId);
        using var invalid = await http.PostAsJsonAsync("/ops/instances/resolution/resolutions", new
        { invocationId = pending.Id, kind = "Completed", outputs = new { Number = "invalid number" }, note = "observed" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(factory.ActivityProbe.Entered.Task.IsCompleted);
        try
        {
            using var response = await http.PostAsJsonAsync("/ops/instances/resolution/resolutions", new
            { invocationId = pending.Id, kind = "Completed", outputs = new { Number = 42 }, note = "observed", decidedBy = "forged" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("/ops/instances/resolution", response.Headers.Location!.OriginalString);
            var saved = (await store.GetWorkflowInstanceAsync("resolution"))!;
            Assert.Equal(42, JsonSerializer.Deserialize<Data>(saved.WorkflowDataJson)!.Number);
            Assert.Contains(await store.GetTraceAsync("resolution"), entry => entry.Entry.Payload is ActivityResolutionTrace { DecidedBy: "operator" });
        }
        finally { factory.ActivityProbe.Exit.TrySetResult(); }
        Assert.Equal(WorkflowExecutionStatus.Success, (await factory.Engine.AwaitAsync("resolution", WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task BusyMutationIsAConflictAndDoesNotPretendToBeAccepted()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        await SeedQuarantine(factory, store, Workflow.Create<Data>("BusyRelease").Step<Noop>().Build(), "busy");
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("busy", "other-owner", TimeSpan.FromMinutes(1),
            await StoreTestSupport.ClaimAsync(factory.Services), ExecutionClaimKind.Operator));
        try
        {
            using var response = await http.PostAsync("/ops/instances/busy/release", null);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
        finally { await store.ReleaseExecutionLeaseAsync("busy", "other-owner"); }
    }

    [Fact]
    public async Task AQueuedDecisionIsNotAcceptedBeforeItCommits()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store) { Slots = 1 };
        using var http = factory.Operator();
        await SeedQuarantine(factory, store, Workflow.Create<Data>("QueuedRelease").Step<Noop>().Build(), "queued");
        var blocker = factory.Engine.RunAsync(Workflow.Create<Data>("OccupySlot").Step<Block>().Build(), new Data());
        try
        {
            await factory.ActivityProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var response = http.PostAsync("/ops/instances/queued/release", null);
            await Task.Delay(factory.Wait + TimeSpan.FromMilliseconds(100));
            Assert.False(response.IsCompleted);
            Assert.Equal(WorkflowStopKind.Quarantine, (await factory.Services.GetRequiredService<IWorkflowOperations>().GetAsync("queued"))!.StopKind);
            factory.ActivityProbe.Exit.TrySetResult();
            using var accepted = await response.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(accepted.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
            Assert.Equal(WorkflowExecutionStatus.Success, (await factory.Engine.AwaitAsync("queued", WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally { factory.ActivityProbe.Exit.TrySetResult(); await blocker; }
    }

    [Fact]
    public async Task ResponsesExcludeSavedDataStacksTraceMetadataAndDeliveryCredentials()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        // The fixture makes the row due for a trace-only checkpoint. Keep the background scanner
        // from consuming that setup transition before its explicit owner has acquired the lease.
        foreach (var scanner in factory.Services.GetServices<IHostedService>().OfType<WorkflowRecoveryService>())
            await scanner.StopAsync(default);
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        await store.RequestCancellationAsync(id, new CancellationReason { ReasonCode = "test" });
        var grant = await StoreTestSupport.LeaseAsync(store, id, await StoreTestSupport.ClaimAsync(factory.Services));
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, grant.Epoch, saved.Revision,
            traceEntries: [new ExecutionTraceEntry { StackTrace = "private-stack", Metadata = new() { ["secret"] = "private-metadata" } }]));
        await StoreTestSupport.ReleaseAsync(store, id);
        await store.RegisterPublisherAsync("lease-test", [OutboxEvent.WorkflowCompleted]);
        await factory.Engine.RunAsync(Workflow.Create<Data>("HttpCompleted").Step<Noop>().Build(), new Data());
        Assert.Single(await store.ClaimDeliveriesAsync("lease-test", "private-lease-token", TimeSpan.FromMinutes(1), 10));
        foreach (var path in new[]
        {
            "/instances", "/instances/" + id, $"/instances/{id}/waits", $"/instances/{id}/waits?tokens=true", "/waits",
            $"/instances/{id}/pending-activities", $"/instances/{id}/trace", $"/instances/{id}/snapshot", "/outbox/publishers", "/outbox/deliveries"
        })
        {
            using var response = await http.GetAsync("/ops" + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { "private-workflow-data", "private-stack", "private-metadata", "private-lease-token" })
                Assert.DoesNotContain(secret, json);
            using var document = JsonDocument.Parse(json);
            AssertSafe(document.RootElement);
        }
        using var stopped = await http.PostAsJsonAsync($"/ops/instances/{id}/cancel", new { reasonCode = "test" });
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        AssertSafe(JsonDocument.Parse(await stopped.Content.ReadAsStringAsync()).RootElement);

        static void AssertSafe(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) AssertSafe(child);
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name.ToLowerInvariant(), new[] { "workflowdata", "workflowdatajson", "errorstacktrace", "stacktrace", "leasetoken", "datajson", "values", "state", "metadata", "runtimevalues" });
                AssertSafe(property.Value);
            }
        }
    }

    [Fact]
    public async Task AcceptedReleaseHasABoundedWaitAndDisconnectDoesNotCancelTheRun()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        var definition = Workflow.Create<Data>("BoundedRelease").Step<Block>().Build();
        await SeedQuarantine(factory, store, definition, "blocked");
        try
        {
            var watch = Stopwatch.StartNew();
            using var response = await http.PostAsync("/ops/instances/blocked/release", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("/ops/instances/blocked", response.Headers.Location!.OriginalString);
            Assert.InRange(watch.Elapsed, factory.Wait, TimeSpan.FromSeconds(5));
            await factory.ActivityProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(factory.ActivityProbe.RunToken.IsCancellationRequested);
            var current = (await store.GetWorkflowInstanceAsync("blocked"))!;
            Assert.Null(ExecutionCheckpoint.Read(current.ExecutionStateJson).OperatorStop);
            factory.ActivityProbe.Exit.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Success, (await factory.Engine.AwaitAsync("blocked", WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally { factory.ActivityProbe.Exit.TrySetResult(); }

        await using var disconnected = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var requestHttp = disconnected.Operator();
        var repo = disconnected.Services.GetRequiredService<IWorkflowStateRepository>();
        await SeedQuarantine(disconnected, repo, definition, "disconnect");
        using var cancellation = new CancellationTokenSource();
        var request = requestHttp.PostAsync("/ops/instances/disconnect/release", null, cancellation.Token);
        try
        {
            await disconnected.ActivityProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { using var response = await request; } catch (OperationCanceledException) { }
            Assert.False(disconnected.ActivityProbe.RunToken.IsCancellationRequested);
        }
        finally { disconnected.ActivityProbe.Exit.TrySetResult(); }
        Assert.Equal(WorkflowExecutionStatus.Success, (await disconnected.Engine.AwaitAsync("disconnect", WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Theory]
    [InlineData(WorkflowErrorKind.InvalidRequest, 400)]
    [InlineData(WorkflowErrorKind.InvalidState, 409)]
    [InlineData(WorkflowErrorKind.InstanceRemoved, 410)]
    [InlineData(WorkflowErrorKind.ArtifactUnavailable, 503)]
    public async Task EngineErrorsBecomeSafeProblemDetails(WorkflowErrorKind kind, int status)
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        factory.Scope.Instances = _ => throw new WorkflowEngineException(kind, "private-stack-and-data");
        using var http = factory.Operator();
        using var response = await http.GetAsync("/ops/instances");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-stack-and-data", json);
        Assert.Equal(status, JsonDocument.Parse(json).RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task UnknownRemovedInvalidStateAndInvalidQueriesHaveDefinedErrors()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await using var factory = new OperationsHttpFactory(store) { ConfigureServices = services => services.AddSingleton<TimeProvider>(time) };
        using var http = factory.Operator();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/ops/instances/unknown")).StatusCode);
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/ops/instances/{id}/release", null)).StatusCode);
        await factory.Engine.CancelAsync(id, new CancellationReason { ReasonCode = "test" });
        time.Advance(TimeSpan.FromMinutes(2));
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync($"/ops/instances/{id}")).StatusCode);
        foreach (var query in new[] { "limit=0", "status=unknown", "after=garbage", "version=bad", "createdFrom=bad", "limit=1001", "limit=1&limit=2" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/ops/instances?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/ops/outbox/deliveries/unknown/publisher/retry", null)).StatusCode);
    }

    [Fact]
    public async Task UnavailableSnapshotArtifactReturnsServiceUnavailable()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var owner = new OperationsHttpFactory(store);
        using var first = owner.Operator();
        var id = (await owner.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        await using var unprepared = new OperationsHttpFactory(store, new WorkflowCatalog());
        using var http = unprepared.Operator();
        using var response = await http.GetAsync($"/ops/instances/{id}/snapshot");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task PublisherEndpointsEnableAndUnregisterWithThePrincipalActor()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        await store.RegisterPublisherAsync("managed", [OutboxEvent.WorkflowCompleted]);
        await factory.Engine.RunAsync(Workflow.Create<Data>("PublisherEndpoints").Step<Noop>().Build(), new Data());
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync("managed", "owner", TimeSpan.FromMinutes(1), 1));
        Assert.True(await store.SettleDeliveryAsync(delivery.Event.Id, "managed", "owner", OutboxPublishResult.GiveUp(disablePublisher: true)));
        using var enabled = await http.PostAsync("/ops/outbox/publishers/managed/enable", null);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("operator", (await enabled.Content.ReadFromJsonAsync<OutboxPublisherState>())!.EnabledBy);
        Assert.Equal(OutboxDeliveryStatus.Pending, (await store.GetDeliveryAsync(delivery.Event.Id, "managed"))!.Status);
        using var invalidRetry = await http.PostAsync($"/ops/outbox/deliveries/{delivery.Event.Id}/managed/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, invalidRetry.StatusCode);
        using var removed = await http.DeleteAsync("/ops/outbox/publishers/managed");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("operator", (await removed.Content.ReadFromJsonAsync<OutboxPublisherState>())!.UnregisteredBy);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/ops/outbox/publishers/managed/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/ops/outbox/publishers/unknown/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync("/ops/outbox/publishers/unknown")).StatusCode);
    }

    [Theory]
    [InlineData("ReadPolicy"), InlineData("ReplyPolicy"), InlineData("InspectPolicy"), InlineData("WritePolicy"), InlineData("OutboxPolicy")]
    public async Task StartupRefusesAnyUncoveredPolicyGroup(string property)
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository())
        { ConfigureOptions = options => typeof(WorkflowOperationsOptions).GetProperty(property)!.SetValue(options, null) };
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task StartupRefusesUnknownPoliciesMissingScopeAndRouteConflicts()
    {
        await using var unknown = new OperationsHttpFactory(new InMemoryWorkflowStateRepository()) { ConfigureOptions = o => o.ReadPolicy = "missing" };
        Assert.Throws<InvalidOperationException>(() => unknown.CreateClient());
        await using var noScope = new OperationsHttpFactory(new InMemoryWorkflowStateRepository()) { RegisterScope = false };
        Assert.Throws<InvalidOperationException>(() => noScope.CreateClient());
        await using var conflicting = new OperationsHttpFactory(new InMemoryWorkflowStateRepository())
        { ConfigureEndpoints = endpoints => endpoints.MapGet("/ops/instances/{other}", () => "conflict") };
        Assert.Throws<InvalidOperationException>(() => conflicting.CreateClient());
    }

    internal static async Task SeedQuarantine(OperationsHttpFactory factory, IWorkflowStateRepository store, WorkflowDefinition definition, string id)
    {
        await factory.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = scopes.StructureHash,
            OperatorStop = new OperatorStopState { Kind = OperatorStopKind.Quarantine, Reason = "lost owners", CrashCount = 3 },
            Continuations = [new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] }]
        };
        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = id, WorkflowName = definition.Name, WorkflowVersion = definition.Version,
            CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow,
            Status = WorkflowStatus.NeedsResolution, WorkflowDataJson = JsonSerializer.Serialize(new Data()),
            WorkflowDataType = typeof(Data).AssemblyQualifiedName!, ExecutionStateJson = JsonSerializer.Serialize(checkpoint)
        });
    }
}
