using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "H-08")]
[Trait("Feature", "tracing")]
public sealed class RedactionReviewFixTests : IClassFixture<SqliteTestDatabase<RedactionReviewFixTests>>
{
    internal const string Secret = "red-review-secret-30be97";

    // Finding 1: provider rules and a provider that accepts every level cannot opt the engine in.
    [Theory]
    [InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task RawLoggingRequiresTheEngineSetting(bool enabled, bool providerSpecific)
    {
        var capture = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture);
            logging.AddFilter(InspectionRedactionPolicy.SensitiveDiagnosticsCategory, LogLevel.Trace);
            if (providerSpecific) logging.AddFilter<Capture>(null, LogLevel.Information);
        });
        services.AddIxIFlow(options => options.EnableSensitiveDiagnostics = enabled);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("raw-setting" + enabled + providerSpecific).Step<Noop>().Step<Failure>().Build(), new Data());
        Assert.Equal(Secret, result.ErrorMessage);
        var raw = capture.Entries.Where(item => item.Category == InspectionRedactionPolicy.SensitiveDiagnosticsCategory).ToArray();
        if (enabled)
        {
            Assert.NotEmpty(raw);
            Assert.All(raw, entry =>
            {
                Assert.Equal(Secret, entry.Exception!.Message);
                Assert.NotEmpty(entry.Exception.StackTrace!);
                Assert.Contains(result.Diagnostic!.DiagnosticId, entry.Text);
            });
        }
        else Assert.Empty(raw);
        Assert.All(capture.Entries.Where(item => item.Category != InspectionRedactionPolicy.SensitiveDiagnosticsCategory), entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(Secret, entry.Text);
        });
    }

    // Finding 3: drive the watch itself, including the guarded failing store call.
    [Fact]
    public async Task FailedInboxSequenceReadLogsOnlyASafeIdentity()
    {
        var store = StoreProbe.Wrap(new InMemoryWorkflowStateRepository(), out var probe);
        probe.FailInboxSequences = true;
        var capture = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        await using var watch = new CancellationWatch(store,
            logger: provider.GetRequiredService<ILogger<CancellationWatch>>(),
            sensitiveDiagnostics: provider.GetRequiredService<ILogger<SensitiveDiagnostics>>(),
            inspectionPolicy: provider.GetRequiredService<InspectionRedactionPolicy>());
        using var registration = watch.Watch("watched", new(TaskCreationOptions.RunContinuationsAsynchronously),
            _ => { }, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(10), _ => { });
        await capture.InboxFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The watch polls every 10 ms, so it may log the failure again before the assertions run; every entry must be safe.
        var entries = capture.Entries.Where(item => item.Text.Contains("Could not read inbox sequences")).ToArray();
        Assert.NotEmpty(entries);
        Assert.True(probe.InboxReads > 0);
        Assert.All(entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.Contains("ErrorType", entry.Text);
            Assert.Contains("DiagnosticId", entry.Text);
            Assert.DoesNotContain(Secret, entry.Text);
        });
        Assert.DoesNotContain(capture.Entries, item => item.Category == InspectionRedactionPolicy.SensitiveDiagnosticsCategory);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ResolutionNoteSurvivesANewerFailure(bool sqlite)
    {
        await using var relational = Local(sqlite);
        await VerifyNoteRetention(relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository());
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task PrivilegedReadCannotCrossIncarnations(bool sqlite, bool replaceAtStoreRead)
    {
        await using var relational = Local(sqlite);
        await VerifyIncarnationRead(relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(), replaceAtStoreRead);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ParallelResolutionReportsTheRemainingFailure(bool sqlite)
    {
        await using var relational = Local(sqlite);
        await VerifyParallelResult(relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository());
    }

    // Finding 6: exercise engine-generated recovery/resolution rejections, not a hand-built result.
    [Theory, InlineData(false), InlineData(true)]
    public async Task DataFreeRejectionsKeepTheirReasonAndKind(bool sqlite)
    {
        await using var relational = Local(sqlite);
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        await using var provider = Services(store);
        var client = provider.GetRequiredService<IWorkflowClient>();
        var recovery = await client.RecoverAsync("missing-" + Guid.NewGuid());
        var started = await client.RunAsync(Workflow.Create<Data>("rejections-" + Guid.NewGuid())
            .Step<Noop>().WaitFor<Notice>("gate").Build(), new Data());
        var resolution = await client.ResolveAsync(started.InstanceId, "no-invocation",
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        foreach (var result in new[] { recovery, resolution })
        {
            Assert.Equal(WorkflowErrorKind.InvalidState, result.ErrorKind);
            Assert.Null(result.Diagnostic);
            Assert.Null(InspectionRedactionPolicy.Default.ResultError(result));
            Assert.Equal(result.ErrorMessage, InspectionRedactionPolicy.Default.ResultReason(result));
            Assert.Null(result.Diagnostic);
        }
        Assert.Contains("no interrupted execution", recovery.ErrorMessage);
        Assert.Contains("no activity awaiting resolution", resolution.ErrorMessage);
        var refused = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowOperations>()
            .SubmitReleaseAsync(started.InstanceId, "operator"));
        Assert.Equal(WorkflowErrorKind.InvalidState, refused.Kind);
        Assert.Equal("The operation was not accepted", refused.Message);
        Assert.Empty(await store.GetDiagnosticsAsync(started.InstanceId));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task DiagnosticFreeCommitsDoNotRequestPruning(bool sqlite)
    {
        await using var relational = Local(sqlite);
        await VerifyNoPruning(relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository());
    }

    private static SqliteWorkflowStateRepository? Local(bool sqlite) => sqlite
        ? new(SqliteTestDatabase<RedactionReviewFixTests>.ConnectionString) : null;

    internal static async Task VerifyNoteRetention(IWorkflowStateRepository store)
    {
        await using var provider = Services(store);
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("note-retention-" + Guid.NewGuid()).Step<Noop>()
            .Saga(saga => saga.Step<UnsafeOutput>()).Step<Failure>().Build(), new Data());
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(started.InstanceId));
        var failed = await client.ResolveAsync(started.InstanceId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?> { ["Result"] = "safe" }, Secret + "-note"));
        Assert.Equal(WorkflowExecutionStatus.Faulted, failed.Status);
        var raw = await provider.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(started.InstanceId);
        var note = Assert.Single(raw, item => item.ErrorType == "OperatorNote");
        Assert.Equal(Secret + "-note", note.Message);
        var failure = Assert.Single(raw, item => item.ErrorType != "OperatorNote");
        Assert.Equal(Secret, failure.Message);
        Assert.Equal(failure.DiagnosticId, failed.Diagnostic!.DiagnosticId);
        var trace = await client.GetTraceAsync(started.InstanceId);
        Assert.Contains(trace, item => item.EntryType == TraceEntryType.ActivityResolved && item.Error?.DiagnosticId == note.DiagnosticId);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(trace));
    }

    internal static async Task VerifyIncarnationRead(IWorkflowStateRepository inner, bool replaceAtStoreRead)
    {
        var id = "diagnostic-race-" + Guid.NewGuid();
        var old = await SeedDiagnostic(inner, id, "old-message");
        var store = StoreProbe.Wrap(inner, out var probe);
        var replaced = false;
        var authorized = false;
        Guid replacement = default;
        async Task Replace()
        {
            Assert.True(authorized);
            if (replaced) return;
            replaced = true;
            await inner.DeleteWorkflowInstanceAsync(id, TimeSpan.FromTicks(1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await inner.GetTombstoneAsync(id) != null) await Task.Delay(10, timeout.Token);
            replacement = await SeedDiagnostic(inner, id, Secret + "-replacement");
            Assert.NotEqual(old, replacement);
        }
        if (replaceAtStoreRead) probe.BeforeBoundRead = Replace;
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddIxIFlow();
        services.AddScoped<IWorkflowDiagnosticsAuthorizer>(_ => new Authorizer(async () =>
        {
            authorized = true;
            if (!replaceAtStoreRead) await Replace();
            return true;
        }));
        await using var provider = services.BuildServiceProvider();
        var diagnostics = provider.GetRequiredService<IWorkflowDiagnostics>();
        Assert.Empty(await diagnostics.ReadAsync(id));
        Assert.True(replaced);
        Assert.Equal(old, probe.BoundIncarnations.First());
        Assert.Empty(await inner.GetDiagnosticsForIncarnationAsync(id, old));
        Assert.Equal(Secret + "-replacement", Assert.Single(await inner.GetDiagnosticsForIncarnationAsync(id, replacement)).Message);
        Assert.Equal(Secret + "-replacement", Assert.Single(await diagnostics.ReadAsync(id)).Message);
        Assert.Equal(replacement, probe.BoundIncarnations.Last());
    }

    private static async Task<Guid> SeedDiagnostic(IWorkflowStateRepository store, string id, string message)
    {
        var instance = new WorkflowInstance { InstanceId = id, ExecutionStateJson = StoreTestSupport.CheckpointJson };
        var raw = new WorkflowDiagnostic(Guid.NewGuid().ToString("N"), "Failure", message, "original stack");
        var lease = await StoreTestSupport.LeaseAsync(store, id);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance,
            lease.Epoch, 0, diagnostics: [raw], retainedDiagnosticIds: [raw.DiagnosticId]))).Status);
        await StoreTestSupport.ReleaseAsync(store, id);
        return (await store.GetWorkflowInstanceSummaryAsync(id))!.Incarnation;
    }

    internal static async Task VerifyParallelResult(IWorkflowStateRepository store)
    {
        await using var provider = Services(store);
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("parallel-result-" + Guid.NewGuid()).Step<Noop>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Saga(saga => saga.Step<UnsafeOutput>()))
                .Do(branch => branch.Saga(saga => saga.Step<UnsafeOutput>())))
            .Build(), new Data());
        var pending = await client.GetPendingActivitiesAsync(started.InstanceId);
        Assert.Equal(2, pending.Count);
        var raw = await provider.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(started.InstanceId);
        var expected = Assert.Single(raw, item => pending[1].Reason!.Contains(item.DiagnosticId));
        var operation = await provider.GetRequiredService<IWorkflowOperations>().SubmitResolutionAsync(started.InstanceId,
            pending[0].InvocationId, ActivityResolution.Completed(new Dictionary<string, object?> { ["Result"] = "safe" }, Secret + "-operator-note"));
        var result = await operation.Completion;
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Equal(expected.DiagnosticId, result.Error!.DiagnosticId);
        Assert.Equal(expected.ErrorType, result.Error.ErrorType);
        Assert.Equal(result.Error.ToString(), result.ErrorMessage);
        Assert.Null(result.ErrorKind);
        var remaining = Assert.Single(await client.GetPendingActivitiesAsync(started.InstanceId));
        Assert.Contains(expected.DiagnosticId, remaining.Reason);
        Assert.Equal(expected.DiagnosticId, (await client.AwaitAsync(started.InstanceId)).Diagnostic!.DiagnosticId);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
        Assert.Contains(await provider.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(started.InstanceId),
            item => item.ErrorType == "OperatorNote" && item.Message == Secret + "-operator-note");
    }

    internal static async Task VerifyNoPruning(IWorkflowStateRepository inner)
    {
        var store = StoreProbe.Wrap(inner, out var probe);
        await using var provider = Services(store);
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("no-diagnostics-" + Guid.NewGuid()).Step<Noop>().Step<Noop>().Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(probe.Commits.Count >= 3);
        Assert.All(probe.Commits, commit =>
        {
            Assert.Empty(commit.Diagnostics);
            Assert.Null(commit.RetainedDiagnosticIds);
        });
        Assert.Empty(await inner.GetDiagnosticsAsync(result.InstanceId));
    }

    private static ServiceProvider Services(IWorkflowStateRepository store)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        DiagnosticTestAccess.Allow(services);
        return services.BuildServiceProvider();
    }

    private sealed class Authorizer(Func<Task<bool>> authorize) : IWorkflowDiagnosticsAuthorizer
    {
        public async ValueTask<bool> CanReadAsync(string instanceId, CancellationToken cancellationToken) => await authorize();
    }
    public sealed class Data;
    public sealed class Notice;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Failure : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret);
    }
    public sealed class UnsafeOutput : IAsyncActivity
    {
        [ActivityOutput] public object Result { get; set; } = (Action)(() => { });
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public class StoreProbe : DispatchProxy
    {
        public IWorkflowStateRepository Inner = null!;
        public bool FailInboxSequences;
        public int InboxReads;
        public Func<Task>? BeforeBoundRead;
        public ConcurrentQueue<Guid> BoundIncarnations { get; } = new();
        public ConcurrentQueue<CheckpointCommit> Commits { get; } = new();
        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, out StoreProbe probe)
        {
            var store = Create<IWorkflowStateRepository, StoreProbe>();
            probe = (StoreProbe)store;
            probe.Inner = inner;
            return store;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.GetInboxSequencesAsync))
            {
                Interlocked.Increment(ref InboxReads);
                if (FailInboxSequences) return Task.FromException<IReadOnlyDictionary<string, long>>(new IOException(Secret));
            }
            if (method.Name == nameof(IWorkflowStateRepository.GetDiagnosticsForIncarnationAsync))
                return BoundRead((string)args![0]!, (Guid)args[1]!);
            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)) Commits.Enqueue((CheckpointCommit)args![0]!);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        private async Task<IReadOnlyList<WorkflowDiagnostic>> BoundRead(string id, Guid incarnation)
        {
            BoundIncarnations.Enqueue(incarnation);
            if (BeforeBoundRead != null) await BeforeBoundRead();
            return await Inner.GetDiagnosticsForIncarnationAsync(id, incarnation);
        }
    }

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public TaskCompletionSource InboxFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }
        public sealed record Entry(string Category, string Text, Exception? Exception);
        private sealed class Logger(Capture capture, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + " " + JsonSerializer.Serialize(state);
                capture.Entries.Enqueue(new(category, text, exception));
                if (text.Contains("Could not read inbox sequences")) capture.InboxFailure.TrySetResult();
            }
        }
    }
}
