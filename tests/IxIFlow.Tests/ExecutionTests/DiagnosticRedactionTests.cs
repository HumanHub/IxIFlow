using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "H-08")]
[Trait("Feature", "tracing")]
public sealed class DiagnosticRedactionTests : IClassFixture<SqliteTestDatabase<DiagnosticRedactionTests>>
{
    private const string Secret = "secret-error-and-note-d68bfae9";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrownSecretOnlyAppearsInAuthorizedDiagnosticsAndApplicationResults(bool sqlite)
    {
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<DiagnosticRedactionTests>.ConnectionString) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        var logs = new Capture();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "IxIFlow",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var services = Services(store, logs, authorize: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Workflow.Create<Data>("SecretError" + sqlite)
            .Step<Noop>().Step<ThrowsSecret>().Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(Secret, result.ErrorMessage);
        Assert.NotEmpty(result.ErrorStackTrace!);
        var awaited = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished);
        Assert.Equal(Secret, awaited.ErrorMessage);
        Assert.Equal(Secret, Assert.IsType<Data>(awaited.WorkflowData).Secret);
        var trace = await client.GetTraceAsync(result.InstanceId);
        var failed = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.ActivityFailed);
        Assert.Equal(typeof(Exception).FullName, failed.Error!.ErrorType);
        Assert.Equal(failed.Error.ToString(), failed.ErrorMessage);
        Assert.Null(failed.StackTrace);
        Assert.Equal("\"\"", failed.Metadata["Note"].ToString());
        var snapshot = await client.GetSnapshotAsync(result.InstanceId);
        Assert.Contains(failed.Error, snapshot!.Errors);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(new
        {
            trace, snapshot, pending = await client.GetPendingActivitiesAsync(result.InstanceId),
            result.TraceEntries, storedTrace = await store.GetTraceAsync(result.InstanceId)
        }));
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(Secret, entry.Text));
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == InspectionRedactionPolicy.SensitiveDiagnosticsCategory);
        var attempt = Assert.Single(spans, span => span.GetTagItem("ixiflow.instance.id") as string == result.InstanceId &&
            span.DisplayName == "activity ThrowsSecret");
        Assert.Equal(ActivityStatusCode.Error, attempt.Status);
        Assert.Null(attempt.StatusDescription);
        Assert.Empty(attempt.Events);
        Assert.Equal(failed.Error.ErrorType, attempt.GetTagItem("error.type"));
        Assert.Equal(failed.Error.DiagnosticId, attempt.GetTagItem("ixiflow.diagnostic.id"));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(new { attempt.TagObjects, attempt.StatusDescription, attempt.Events }));
        var raw = await services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(result.InstanceId);
        var diagnostic = Assert.Single(raw, item => item.DiagnosticId == failed.Error.DiagnosticId);
        Assert.Equal(Secret, diagnostic.Message);
        Assert.Contains(nameof(ThrowsSecret), diagnostic.StackTrace);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RawExceptionLoggerRequiresExplicitEngineOptIn(bool optIn, bool providerSpecific)
    {
        var capture = new Capture();
        using var services = Services(new InMemoryWorkflowStateRepository(), capture, optIn: optIn,
            providerSpecific: providerSpecific);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("SensitiveLogger" + optIn + providerSpecific).Step<Noop>().Step<ThrowsSecret>().Build(), new Data());
        Assert.Equal(Secret, result.ErrorMessage);
        var sensitive = capture.Entries.Where(item => item.Category == InspectionRedactionPolicy.SensitiveDiagnosticsCategory).ToArray();
        if (optIn)
        {
            Assert.NotEmpty(sensitive);
            Assert.All(sensitive, item =>
            {
                Assert.Equal(Secret, item.Error!.Message);
                Assert.NotEmpty(item.Error.StackTrace!);
                Assert.Contains("DiagnosticId", item.State);
                Assert.Contains(result.Diagnostic!.DiagnosticId, item.State);
            });
            Assert.Contains(sensitive, item => item.Text.Contains(nameof(ThrowsSecret)));
        }
        else Assert.Empty(sensitive);
        Assert.All(capture.Entries.Where(item => item.Category != InspectionRedactionPolicy.SensitiveDiagnosticsCategory),
            item => { Assert.Null(item.Error); Assert.DoesNotContain(Secret, item.Text); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task RawDiagnosticsDenyMissingOrRejectingApplicationAuthorizer(bool? authorize)
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Capture(), authorize: authorize);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync("instance"));
    }

    [Fact]
    public async Task RecoveryReasonAndOperatorDecisionNoteUseTheSamePolicy()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var services = Services(store, new Capture(), authorize: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("SecretRecovery").Step<Noop>()
            .WaitFor<Notice>("gate").Step<Recovers>().Build();
        var started = await client.RunAsync(definition, new Data());
        var saved = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var continuation = Assert.Single(checkpoint.Continuations);
        continuation.Stack[^1].NextStepIndex++;
        continuation.Status = ContinuationStatus.Active;
        continuation.PendingActivity = new ActivityInvocationState
        {
            StepId = new WorkflowScopeCatalog(definition).Id(definition.Steps[2]),
            RecoveryState = SerializedValue.From("ready"), Attempts = [new ActivityAttemptState()]
        };
        checkpoint.Waits.Clear();
        saved.Status = WorkflowStatus.Running;
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await store.AppendEventAsync(new(saved.InstanceId, Guid.NewGuid().ToString("N"),
            IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Notice)), "{}", Key: "gate"));
        var committed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved,
            (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        await StoreTestSupport.ReleaseAsync(store, saved.InstanceId);
        var recovered = await client.RecoverAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Contains(Secret, recovered.ErrorMessage);
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(saved.InstanceId));
        Assert.Contains(typeof(Exception).FullName!, pending.Reason);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(pending));
        var raw = await services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(saved.InstanceId);
        Assert.Contains(raw, item => item.Message == Secret && item.StackTrace!.Contains(nameof(Recovers)));
        var completed = await client.ResolveAsync(saved.InstanceId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>(), Secret));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var trace = await client.GetTraceAsync(saved.InstanceId);
        Assert.Contains(trace, item => item.Metadata.TryGetValue("DecisionNote", out var note) && note.ToString() == "");
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(trace));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(await client.GetSnapshotAsync(saved.InstanceId)));
    }

    [Fact]
    public void StringNotesUseTheSensitiveClassificationAndFailClosed()
    {
        using var erased = Services(new InMemoryWorkflowStateRepository(), new Capture());
        Assert.Equal("", InspectionRedactionPolicy.Default.Note(Secret, erased));
        var services = new ServiceCollection();
        services.AddSingleton<IRedactorProvider>(new NoteProvider());
        using var configured = services.BuildServiceProvider();
        Assert.Equal("pseudonym", InspectionRedactionPolicy.Default.Note(Secret, configured));
        Assert.Equal("\"pseudonym\"", InspectionRedactionPolicy.Default.AttemptNote(SerializedValue.From(Secret)!, configured));
        Assert.Equal("\"\"", InspectionRedactionPolicy.Default.AttemptNote(new SerializedValue
            { Type = typeof(string).AssemblyQualifiedName!, Json = "invalid" }, configured));
    }

    [Fact]
    public void EngineExceptionLogsCarryOnlyIdentityAndDeliveryListingsProjectStoredErrors()
    {
        var capture = new Capture();
        using var services = Services(new InMemoryWorkflowStateRepository(), capture);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("engine");
        Exception error;
        try { throw new Exception(Secret, new Exception(Secret)); }
        catch (Exception caught) { error = caught; }
        InspectionRedactionPolicy.Default.Log(logger, services.GetRequiredService<ILogger<SensitiveDiagnostics>>(),
            LogLevel.Warning, error, "Delivery failed for {EventId}", "event");
        var entry = Assert.Single(capture.Entries);
        Assert.Null(entry.Error);
        Assert.DoesNotContain(Secret, entry.Text);
        Assert.Contains(InspectionRedactionPolicy.Default.Error(error).DiagnosticId, entry.Text);
        var original = new OutboxDelivery(new("event", "type", "instance", "workflow", 1, "", DateTime.UtcNow, "{}"),
            "publisher", 2, DateTime.UtcNow, OutboxDeliveryStatus.Pending, Secret, null, null, DateTime.UtcNow);
        var listed = InspectionRedactionPolicy.Default.Delivery(original);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(listed));
        Assert.Contains("DeliveryFailure", listed.LastError);
        Assert.Equal(Secret, original.LastError);
    }

    [Fact]
    public async Task AThrowingMappingSetterCannotLeakThroughTheEngineExceptionLogger()
    {
        var capture = new Capture();
        using var services = Services(new InMemoryWorkflowStateRepository(), capture);
        await services.GetRequiredService<IWorkflowClient>().RunAsync(Workflow.Create<Data>("SecretMapping")
            .Step<Noop>().Step<BadMapping>(step => step.Input(activity => activity.Value)
                .From(context => context.WorkflowData.Secret)).Build(), new Data());
        var failure = Assert.Single(capture.Entries, entry => entry.Text.Contains("Failed to apply typed input mapping"));
        Assert.Null(failure.Error);
        Assert.Contains("ErrorType", failure.State);
        Assert.Contains("DiagnosticId", failure.State);
        Assert.All(capture.Entries, entry => Assert.DoesNotContain(Secret, entry.Text + entry.State));
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, Capture capture, bool? authorize = null,
        bool optIn = false, bool providerSpecific = false)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture);
            if (providerSpecific)
                logging.AddFilter<Capture>(InspectionRedactionPolicy.SensitiveDiagnosticsCategory, LogLevel.Trace);
            else
                logging.AddFilter(InspectionRedactionPolicy.SensitiveDiagnosticsCategory, LogLevel.Trace);
        });
        services.AddIxIFlow(options => options.EnableSensitiveDiagnostics = optIn);
        services.AddSingleton(store);
        services.AddTransient<Noop>();
        services.AddTransient<ThrowsSecret>();
        services.AddTransient<Recovers>();
        services.AddTransient<BadMapping>();
        if (authorize is { } allowed) services.AddScoped<IWorkflowDiagnosticsAuthorizer>(_ => new Authorizer(allowed));
        return services.BuildServiceProvider();
    }

    private sealed class Authorizer(bool allowed) : IWorkflowDiagnosticsAuthorizer
    {
        public ValueTask<bool> CanReadAsync(string instanceId, CancellationToken cancellationToken) => ValueTask.FromResult(allowed);
    }
    public sealed class Data { [Sensitive] public string Secret { get; set; } = DiagnosticRedactionTests.Secret; }
    public sealed class Notice;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class ThrowsSecret : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var data = (Data)context.WorkflowData;
            context.Attempt.Note = data.Secret;
            throw new Exception(data.Secret);
        }
    }
    public sealed class Recovers : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => "ready";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context,
            CancellationToken cancellationToken = default) => throw new Exception(((Data)context.WorkflowData).Secret);
    }
    public sealed class BadMapping : IAsyncActivity
    {
        public string Value { get => ""; set => throw new Exception(value); }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoteProvider : IRedactorProvider
    {
        public Redactor GetRedactor(Microsoft.Extensions.Compliance.Classification.DataClassificationSet classifications)
        {
            Assert.Equal(new Microsoft.Extensions.Compliance.Classification.DataClassificationSet(SensitiveAttribute.Classification), classifications);
            return new NoteRedactor();
        }
    }
    private sealed class NoteRedactor : Redactor
    {
        public override int GetRedactedLength(ReadOnlySpan<char> input) => 9;
        public override int Redact(ReadOnlySpan<char> source, Span<char> destination) { "pseudonym".AsSpan().CopyTo(destination); return 9; }
    }
    private sealed record Entry(string Category, string Text, string State, Exception? Error);
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentBag<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }
        private sealed class Logger(string category, ConcurrentBag<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(new(category, formatter(state, exception) + exception, JsonSerializer.Serialize(state), exception));
        }
    }
}
