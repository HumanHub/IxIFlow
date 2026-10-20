using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "H-04")]
[Trait("Feature", "durability")]
public sealed class ManagementApiReviewTests
{
    public static IEnumerable<object[]> PreparationFailures => new[] { StoreProfiles.LocalMemory, StoreProfiles.LocalSqlite, StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql }.SelectMany(profile =>
        new[] { "search", "read", "limit", "commit" }.Select(stage => new object[] { profile, stage }));

    [Theory, MemberData(nameof(PreparationFailures))]
    [Trait("Spec", "A-02")]
    public async Task TerminationFailuresReturnTheOriginalConflictAcrossSerialization(string profile, string stage)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var inner = database.NewRepository();
        var instance = await SeedAsync(inner, WorkflowStatus.Running);
        var store = ReviewRepository.Wrap(inner, out var probe);
        await using var services = Services(store);
        await using var renewer = new ExecutionLeaseRenewer(inner);
        await using var watch = new CancellationWatch(inner);
        var owner = (await WorkflowExecutionLease.TryAcquireAsync(inner, instance,
            new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)), watch, renewer, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()), _ => { }, _ => { }, _ => { }))!;
        var operations = new WorkflowHost.InstanceOperations(services, services.GetRequiredService<IActivityExecutor>(),
            store, services.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(), services.GetRequiredService<ExecutionLeaseRenewer>(),
            limits: new ExecutionLimits { MaxCheckpointBytes = 10_000 });
        var identity = Guid.NewGuid();
        using var assignment = operations.Admit(instance, owner, identity);
        await inner.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" });
        if (stage == "search") probe.Search = _ => throw new IOException("preparation search unavailable");
        if (stage == "read") probe.Read = _ => throw new IOException("preparation read unavailable");
        if (stage == "limit") probe.Read = async id =>
        {
            var saved = (await inner.GetWorkflowInstanceAsync(id))!;
            saved.WorkflowDataJson = StoredJson.Inline(JsonSerializer.Serialize(new string('x', 20_000)));
            return saved;
        };
        if (stage == "commit") probe.Commit = commit => commit.Instance.Status == WorkflowStatus.Terminated
            ? throw new IOException("terminal commit unavailable") : inner.CommitWorkflowInstanceAsync(commit);
        var request = Cross(new Checkpoint(identity, CheckpointCommit.Create(instance, owner.Epoch, instance.Revision)));
        var reply = Cross(await new WorkflowHost.InstanceOperations.ExecutionHost(operations).CheckpointAsync(request));
        Assert.Equal(CheckpointReplyKind.Conflict, reply.Kind);
        Assert.Equal(WorkflowControlMode.Terminate, reply.PendingCancellation!.Mode);
        if (stage == "limit")
        {
            // Termination writes metadata only, so an instance over the size limit is still terminated,
            // and its saved data stays as it was.
            Assert.Null(assignment.LastCommitError);
            var terminated = (await inner.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(WorkflowStatus.Terminated, terminated.Status);
            Assert.Equal(instance.WorkflowDataJson, terminated.WorkflowDataJson);
            probe.Read = null;
            await owner.DisposeAsync();
            return;
        }
        Assert.NotNull(assignment.LastCommitError);
        Assert.Equal(WorkflowStatus.Running, (await inner.GetWorkflowInstanceSummaryAsync(instance.InstanceId))!.Status);
        probe.Search = null; probe.Read = null; probe.Commit = null;
        await owner.DisposeAsync();
        Assert.Equal(WorkflowStatus.Terminated,
            (await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(instance.InstanceId, "retry")).Status);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FallbackScansLongerThanARenewalIntervalBeforeTakingItsLease(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var inner = database.NewRepository();
        var root = await SeedAsync(inner, WorkflowStatus.Suspended);
        var child = await SeedAsync(inner, WorkflowStatus.Suspended, root.InstanceId, "child-wait");
        var store = ReviewRepository.Wrap(inner, out var probe);
        var entered = Signal(); var resume = Signal();
        var terminalSubmitted = 0;
        probe.Summary = id => id == child.InstanceId && Volatile.Read(ref terminalSubmitted) == 0
            ? throw new InvalidOperationException("The scan must use the projected ParentWaitId without a per-child read")
            : inner.GetWorkflowInstanceSummaryAsync(id);
        probe.Commit = commit =>
        {
            if (commit.InstanceId == root.InstanceId && commit.Instance.Status == WorkflowStatus.Terminated)
                Interlocked.Exchange(ref terminalSubmitted, 1);
            return inner.CommitWorkflowInstanceAsync(commit);
        };
        probe.Search = async query =>
        {
            if (query.ParentInstanceId == root.InstanceId)
            {
                entered.TrySetResult();
                await resume.Task;
            }
            var page = await inner.QueryInstancesAsync(query);
            if (query.ParentInstanceId == root.InstanceId)
                Assert.Equal("child-wait", Assert.Single(page.Items).ParentWaitId);
            return page;
        };
        await using var services = Services(store, time);
        var finishing = services.GetRequiredService<IWorkflowOperations>().TerminateAsync(root.InstanceId, "stop");
        try
        {
            await entered.Task.WaitAsync(Patience);
            time.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(0, probe.Acquisitions);
            Assert.DoesNotContain(child.InstanceId, probe.SummaryReads);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(WorkflowStatus.Terminated, (await finishing.WaitAsync(Patience)).Status);
        Assert.Equal(WorkflowControlMode.Terminate, (await inner.GetCancellationRequestAsync(child.InstanceId))!.Mode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    [Trait("Spec", "H-05")]
    public async Task RetentionPreservesTheAncestryOfALiveDetachedGrandchild(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var root = await SeedAsync(store, WorkflowStatus.Suspended);
        var first = await SeedAsync(store, WorkflowStatus.Completed, root.InstanceId);
        var second = await SeedAsync(store, WorkflowStatus.Completed, first.InstanceId);
        var detached = await SeedAsync(store, WorkflowStatus.Suspended, second.InstanceId);
        await AgeAsync(database, time);
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
        Assert.NotNull(await store.GetWorkflowInstanceSummaryAsync(first.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceSummaryAsync(second.InstanceId));
        await using var services = Services(store);
        await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(root.InstanceId, "subtree");
        Assert.Equal(WorkflowControlMode.Terminate, (await store.GetCancellationRequestAsync(detached.InstanceId))!.Mode);
        await services.GetRequiredService<IWorkflowClient>().RecoverAsync(detached.InstanceId);
        Assert.Equal(WorkflowStatus.Terminated, (await store.GetWorkflowInstanceSummaryAsync(detached.InstanceId))!.Status);
        await AgeAsync(database, time);
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100)).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceSummaryAsync(detached.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceSummaryAsync(second.InstanceId));
        await AgeAsync(database, time);
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
        Assert.Null(await store.GetWorkflowInstanceSummaryAsync(second.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceSummaryAsync(first.InstanceId));
        await AgeAsync(database, time);
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
        Assert.Null(await store.GetWorkflowInstanceSummaryAsync(first.InstanceId));
        await AgeAsync(database, time);
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100);
        Assert.Null(await store.GetWorkflowInstanceSummaryAsync(root.InstanceId));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AuthorizedHttpTerminationCannotReachAReplacement(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var inner = database.NewRepository();
        var original = await SeedAsync(inner, WorkflowStatus.Suspended);
        var store = ReviewRepository.Wrap(inner, out var probe);
        var submitting = Signal(); var resume = Signal(); Guid? submittedIdentity = null;
        probe.Terminate = async (id, reason, incarnation) =>
        {
            submittedIdentity = incarnation;
            submitting.TrySetResult();
            await resume.Task;
            return await inner.RequestTerminationAsync(id, reason, incarnation);
        };
        await using var factory = new OperationsHttpFactory(store);
        using var http = factory.Operator();
        var request = http.PostAsJsonAsync($"/ops/instances/{original.InstanceId}/terminate", new { reasonCode = "authorized" });
        WorkflowInstance replacement;
        try
        {
            await submitting.Task.WaitAsync(Patience);
            await inner.DeleteWorkflowInstanceAsync(original.InstanceId, TimeSpan.FromDays(1));
            await AgeAsync(database, time);
            replacement = await SeedAsync(inner, WorkflowStatus.Suspended, id: original.InstanceId);
            Assert.NotEqual(original.Incarnation, replacement.Incarnation);
        }
        finally { resume.TrySetResult(); }
        using var response = await request.WaitAsync(Patience);
        Assert.Equal(original.Incarnation, submittedIdentity);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await inner.GetCancellationRequestAsync(original.InstanceId));
        var saved = (await inner.GetWorkflowInstanceSummaryAsync(original.InstanceId))!;
        Assert.Equal(replacement.Incarnation, saved.Incarnation);
        Assert.Equal(WorkflowStatus.Suspended, saved.Status);
        Assert.Equal(replacement.Revision, saved.Revision);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AllOperationsRefuseAnIncarnationTheyDidNotAuthorize(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var original = await SeedAsync(store, WorkflowStatus.NeedsResolution);
        await store.DeleteWorkflowInstanceAsync(original.InstanceId, TimeSpan.FromDays(1));
        await AgeAsync(database, time);
        var replacement = await SeedAsync(store, WorkflowStatus.NeedsResolution, id: original.InstanceId);
        await using var services = Services(store);
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var submissions = new Func<Task<WorkflowOperation>>[]
        {
            () => operations.SubmitTerminationAsync(original.InstanceId, "stop", null, "operator", original.Incarnation),
            () => operations.SubmitCancellationAsync(original.InstanceId, new CancellationReason(), original.Incarnation),
            () => operations.SubmitReleaseAsync(original.InstanceId, "operator", original.Incarnation),
            () => operations.SubmitResolutionAsync(original.InstanceId, "pending", ActivityResolution.Completed(new Dictionary<string, object?>()), original.Incarnation)
        };
        foreach (var submit in submissions)
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(submit)).Kind);
        Assert.Null(await store.GetCancellationRequestAsync(original.InstanceId));
        Assert.Equal(replacement.Revision, (await store.GetWorkflowInstanceSummaryAsync(original.InstanceId))!.Revision);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task HttpCanResolveAChildInvocationRefusedAtTheCheckpointLimit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await using var factory = new OperationsHttpFactory(store)
        {
            ConfigureServices = services => services.AddSingleton(new ExecutionLimits { MaxCheckpointBytes = 8_000 })
        };
        using var http = factory.Operator();
        var child = Workflow.Create<ChildData>("ReviewChild").Step<Noop>().Build();
        await factory.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Workflow.Create<Data>("ReviewParent").Step<Noop>()
            .Invoke<ChildData>(child.Name, child.Version, step => step
                .Input(data => data.Input).From(_ => new string('x', 20_000))
                .Output(data => data.Number).To(context => context.WorkflowData.Number)).Build();
        var stopped = await factory.Engine.RunAsync(parent, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var pending = Assert.Single((await http.GetFromJsonAsync<PendingActivityInfo[]>($"/ops/instances/{stopped.InstanceId}/pending-activities"))!);
        foreach (var body in new object[]
        {
            new { invocationId = pending.InvocationId, kind = "Completed", note = "observed" },
            new { invocationId = pending.InvocationId, kind = "Completed", note = "observed", invocationResult = new { number = "invalid" } },
            new { invocationId = pending.InvocationId, kind = "Completed", note = "observed", invocationResult = (object?)null }
        })
        {
            using var invalid = await http.PostAsJsonAsync($"/ops/instances/{stopped.InstanceId}/resolutions", body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
            Assert.Contains("invocationResult", problem.RootElement.GetProperty("detail").GetString());
        }
        using var resolved = await http.PostAsJsonAsync($"/ops/instances/{stopped.InstanceId}/resolutions", new
        { invocationId = pending.InvocationId, kind = "Completed", note = "observed", invocationResult = new { number = 42 } });
        Assert.Contains(resolved.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        Assert.Equal(WorkflowExecutionStatus.Success, (await factory.Engine.AwaitAsync(stopped.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience)).Status);
        Assert.Equal(42, JsonSerializer.Deserialize<Data>((await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.WorkflowDataJson.InlineText)!.Number);
        Assert.Empty((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: stopped.InstanceId))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TriggerStatusReportsTerminated(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        await using var factory = new OperationsHttpFactory(database.NewRepository())
        {
            ConfigureEndpoints = endpoints => endpoints.MapWorkflowTriggers(triggers =>
                triggers.Status().Authorize("read").AuthorizeInstance("read"))
        };
        using var http = factory.Operator();
        var id = (await factory.Services.GetRequiredService<IWorkflowIngressClient>().AdmitAsync(Waiting("ReviewTrigger"), new Data(),
            new WorkflowAdmission("review", "start", Guid.NewGuid().ToString("N"), new string('a', 64), "operator"))).InstanceId;
        await factory.Engine.AwaitAsync(id).WaitAsync(Patience);
        await factory.Services.GetRequiredService<IWorkflowOperations>().TerminateAsync(id, "stop");
        var status = await http.GetFromJsonAsync<WorkflowStatusResponse>($"/workflows/{id}");
        Assert.Equal("Terminated", status!.Status);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TerminationPollingChecksTheSummaryWithoutLoadingAFinishedCheckpoint(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var inner = database.NewRepository();
        var root = await SeedAsync(inner, WorkflowStatus.Suspended);
        var decorated = ReviewRepository.Wrap(inner, out var probe);
        probe.Terminate = async (id, reason, incarnation) =>
        {
            var receipt = await inner.RequestTerminationAsync(id, reason, incarnation);
            var saved = (await inner.GetWorkflowInstanceAsync(id))!;
            saved.Status = WorkflowStatus.Terminated;
            saved.CancellationReason = (await inner.GetCancellationRequestAsync(id))!;
            await inner.SeedAsync(saved);
            return receipt;
        };
        probe.Read = _ => throw new IOException("A polling pre-check must not load checkpoint parts");
        await using var services = Services(decorated);
        var operation = await services.GetRequiredService<IWorkflowOperations>().SubmitTerminationAsync(root.InstanceId, "stop", null, "operator");
        Assert.Equal(WorkflowExecutionStatus.Terminated, (await operation.Completion.WaitAsync(Patience)).Status);
        Assert.Contains(root.InstanceId, probe.SummaryReads);
        Assert.Equal(0, probe.Acquisitions);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FallbackWakesChildrenAndUnschedulesTheTerminatedInstance(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var root = await SeedAsync(store, WorkflowStatus.Suspended);
        var child = await SeedAsync(store, WorkflowStatus.Suspended, root.InstanceId);
        var observed = Signal();
        var decorated = ReviewRepository.Wrap(store, out var probe);
        probe.Commit = async commit =>
        {
            var result = await store.CommitWorkflowInstanceAsync(commit);
            if (commit.InstanceId == child.InstanceId && commit.Instance.Status == WorkflowStatus.Terminated)
                observed.TrySetResult();
            return result;
        };
        await using var services = Services(decorated);
        var host = services.GetRequiredService<WorkflowHost>();
        host.Schedule(root.InstanceId, DateTime.UtcNow.AddHours(1));
        Assert.Equal(WorkflowStatus.Terminated, (await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(root.InstanceId, "stop")).Status);
        await observed.Task.WaitAsync(Patience); // No recovery service or polling is running in this provider.
        Assert.Equal(WorkflowStatus.Terminated, (await store.GetWorkflowInstanceSummaryAsync(child.InstanceId))!.Status);
        var due = (System.Collections.IDictionary)typeof(WorkflowHost).GetField("_dueOf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        Assert.False(due.Contains(root.InstanceId));
    }

    private static async Task AgeAsync(ProfileStore database, FakeTimeProvider time)
    {
        if (database.Profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite) time.Advance(TimeSpan.FromDays(2));
        else await database.AgeRetentionAsync();
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Cross<T>(T message) where T : class
    {
        var info = ExecutionMessageContext.Default.GetTypeInfo(typeof(T))!;
        return (T)JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(message, info), info)!;
    }
    private static ServiceProvider Services(IWorkflowStateRepository store, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(); services.AddSingleton(store);
        if (time != null) services.AddSingleton(time);
        return services.BuildServiceProvider();
    }
    private static async Task<WorkflowInstance> SeedAsync(IWorkflowStateRepository store, WorkflowStatus status,
        string? parent = null, string? parentWait = null, string? id = null)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = id ?? Guid.NewGuid().ToString("N"), WorkflowName = "ReviewUnregistered", Status = status,
            CreatedAt = DateTime.UtcNow, WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = "unavailable type",
            ExecutionStateJson = StoreTestSupport.CheckpointJson, ParentInstanceId = parent, ParentWaitId = parentWait
        };
        await store.SeedAsync(instance);
        return (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
    }
    public sealed class ChildData { public string Input { get; set; } = ""; public int Number { get; set; } }

    // A transparent decorator for the complete mandatory contract. Only named fault points change behavior.
    public class ReviewRepository : DispatchProxy
    {
        public IWorkflowStateRepository Inner = null!;
        public Func<InstanceQuery, Task<InstancePage>>? Search;
        public Func<string, Task<WorkflowInstance?>>? Read;
        public Func<string, Task<WorkflowInstance?>>? Summary;
        public Func<CheckpointCommit, Task<WorkflowCommitResult>>? Commit;
        public Func<string, CancellationReason, Guid?, Task<WorkflowControlReceipt>>? Terminate;
        public int Acquisitions;
        public System.Collections.Concurrent.ConcurrentBag<string> SummaryReads = [];
        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, out ReviewRepository probe)
        {
            var store = Create<IWorkflowStateRepository, ReviewRepository>();
            probe = (ReviewRepository)store; probe.Inner = inner;
            return store;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var args = arguments!;
            if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync)) Interlocked.Increment(ref Acquisitions);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceSummaryAsync)) SummaryReads.Add((string)args[0]!);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceSummaryAsync) && Summary != null) return Summary((string)args[0]!);
            if (method.Name == nameof(IWorkflowStateRepository.QueryInstancesAsync) && Search != null) return Search((InstanceQuery)args[0]!);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceAsync) && Read != null) return Read((string)args[0]!);
            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync) && Commit != null) return Commit((CheckpointCommit)args[0]!);
            if (method.Name == nameof(IWorkflowStateRepository.RequestTerminationAsync) && Terminate != null)
                return Terminate((string)args[0]!, (CancellationReason)args[1]!, (Guid?)args[2]);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
