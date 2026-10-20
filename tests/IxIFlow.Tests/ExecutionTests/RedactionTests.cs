using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
public sealed class RedactionTests : IClassFixture<SqliteTestDatabase<RedactionTests>>
{
    private const string Secret = "classified-value-7b590dc3";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassifiedValuesNeverAppearInAnyInspectionCopyOrEngineLog(bool sqlite)
    {
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<RedactionTests>.ConnectionString) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        var logs = new Capture();
        using var services = Services(store, logs);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("Redaction" + sqlite)
            .Step<Noop>()
            .Saga(saga => saga.Step<LeavesNote>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Secret)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Secret)
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .WaitFor<Notice>("notice", configure: wait => wait.DefaultPayload(new Notice { Secret = Secret }))
            .Build();

        // Data, event and Previous contracts are collected during Build; notes are object-typed and
        // their actual contracts are collected when the runtime first projects them.
        Assert.Contains(RedactingJson.ClassifiedPaths(typeof(Data)), path => path.Path == "Secret");
        Assert.Contains(RedactingJson.ClassifiedPaths(typeof(Notice)), path => path.Path == "Secret");
        Assert.Contains(RedactingJson.ClassifiedPaths(typeof(LeavesNote)), path => path.Path == "Value");
        var started = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var snapshot = await client.GetSnapshotAsync(started.InstanceId);
        Assert.Equal("", snapshot!.Data!.Value.GetProperty("Secret").GetString());
        Assert.False(JsonSerializer.SerializeToElement(snapshot).TryGetProperty("Values", out _));
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.Equal("", JsonDocument.Parse(wait.DefaultPayloadJson!).RootElement.GetProperty("Secret").GetString());
        // Inspection must not change the saved default, data or notes used by subsequent execution.
        Assert.Contains(Secret, Assert.Single(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId))).DefaultPayloadJson!.InlineText);
        var finished = await client.ReplyAsync(wait.Token, new Notice { Secret = Secret });
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(Secret, Assert.IsType<Data>(finished.WorkflowData).Secret);
        Assert.Equal(Secret, Assert.IsType<Data>((await client.AwaitAsync(started.InstanceId,
            WorkflowAwaitCondition.Finished)).WorkflowData).Secret);

        var trace = await client.GetTraceAsync(started.InstanceId);
        var stored = await store.GetTraceAsync(started.InstanceId);
        var serializationStore = new InMemoryWorkflowStateRepository();
        var commit = CheckpointCommit.Create(new WorkflowInstance
            { InstanceId = started.InstanceId, ExecutionStateJson = StoreTestSupport.CheckpointJson },
            (await StoreTestSupport.LeaseAsync(serializationStore, started.InstanceId)).Epoch, 0,
            traceEntries: trace);
        Assert.Contains(trace, entry => entry.Metadata.ContainsKey("Note"));
        var copies = trace.Concat(stored.Select(entry => entry.Entry))
            .Concat(commit.ReadTraceEntries()).Concat(started.TraceEntries).Concat(finished.TraceEntries);
        var jsonCopies = copies.Select(entry => JsonSerializer.Serialize(entry))
            .Concat(commit.TraceEntries).Concat(stored.Select(entry => JsonSerializer.Serialize(entry)))
            .Append(JsonSerializer.Serialize(snapshot))
            .Append(JsonSerializer.Serialize(await client.GetSnapshotAsync(started.InstanceId)))
            .Append(JsonSerializer.Serialize(wait)).Concat(logs.Entries).ToArray();
        Assert.NotEmpty(logs.Entries);
        Assert.All(jsonCopies, json => Assert.DoesNotContain(Secret, json));
        Assert.Contains(RedactingJson.ClassifiedPaths(typeof(Note)), path => path.Path == "Secret");
    }

    [Fact]
    public void ErasureChangesNumbersObjectsAndListElementsToStrings()
    {
        using var services = Services();
        var json = JsonSerializer.Deserialize<JsonElement>(new RedactingJson(services).Serialize(new Shapes()));
        Assert.Equal("", json.GetProperty("Number").GetString());
        Assert.Equal("", json.GetProperty("Whole").GetString());
        Assert.Equal(["", ""], json.GetProperty("List").EnumerateArray().Select(item => item.GetString()));
        Assert.All(json.GetProperty("Numbers").EnumerateArray(), item => Assert.Equal("", item.GetString()));
        Assert.All(json.GetProperty("Objects").EnumerateArray(), item => Assert.Equal("", item.GetString()));
        Assert.Equal("", json.GetProperty("Nested").GetProperty("Secret").GetString());
        Assert.All(json.GetProperty("NestedList").EnumerateArray(), item => Assert.Equal("", item.GetProperty("Secret").GetString()));
        Assert.Equal("visible", json.GetProperty("Nested").GetProperty("Public").GetString());
        Assert.Equal("", json.GetProperty("Taxonomy").GetString());
    }

    [Fact]
    public void HmacIsOptInAndCorrelationIsScopedToTheApplicationsKey()
    {
        using var first = Services(configure: services => Hmac(services, 1));
        using var second = Services(configure: services => Hmac(services, 2));
        var projection = new RedactingJson(first);
        var json = JsonSerializer.Deserialize<JsonElement>(projection.Serialize(new Shapes()));
        var redactor = first.GetRequiredService<IRedactorProvider>().GetRedactor(SensitiveAttribute.Classification);
        Assert.Equal(redactor.Redact("1234567"), json.GetProperty("Number").GetString());
        Assert.Equal(redactor.Redact(JsonSerializer.Serialize(new Note())), json.GetProperty("Whole").GetString());
        Assert.All(json.GetProperty("List").EnumerateArray(), item => Assert.Equal(redactor.Redact(Secret), item.GetString()));
        Assert.All(json.GetProperty("Numbers").EnumerateArray(), item => Assert.Equal(redactor.Redact("1234567"), item.GetString()));
        Assert.All(json.GetProperty("Objects").EnumerateArray(), item => Assert.Equal(redactor.Redact(JsonSerializer.Serialize(new Note())), item.GetString()));
        Assert.Equal(redactor.Redact(Secret), json.GetProperty("Nested").GetProperty("Secret").GetString());
        Assert.Equal(projection.Serialize(new Shapes()), projection.Serialize(new Shapes()));
        Assert.NotEqual(projection.Serialize(new Shapes()), new RedactingJson(second).Serialize(new Shapes()));
        Assert.DoesNotContain(Secret, json.GetRawText());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("provider-throws")]
    [InlineData("redactor-throws")]
    public void MissingOrFailingRedactorsErase(string failure)
    {
        using var services = Services(configure: services => services.AddScoped<IRedactorProvider>(
            _ => new BrokenProvider(failure)));
        var json = new RedactingJson(services).Serialize(new Shapes());
        Assert.DoesNotContain(Secret, json);
        Assert.Equal("", JsonSerializer.Deserialize<JsonElement>(json).GetProperty("Number").GetString());
        Assert.All(JsonSerializer.Deserialize<JsonElement>(json).GetProperty("List").EnumerateArray(),
            item => Assert.Equal("", item.GetString()));
    }

    [Fact]
    public void UninspectableConvertersAndRawJsonAreErasedEvenWithHmac()
    {
        using var services = Services(configure: services => Hmac(services, 1));
        var projection = new RedactingJson(services);
        Assert.Equal("\"\"", projection.Serialize(new Opaque()));
        Assert.Equal("\"\"", projection.Serialize(JsonSerializer.Serialize(new Opaque()), typeof(Opaque).AssemblyQualifiedName!));
        var json = JsonSerializer.Deserialize<JsonElement>(projection.Serialize(new OpaqueMembers()));
        Assert.All(json.EnumerateObject(), property => Assert.Equal("", property.Value.GetString()));
        Assert.Equal("\"\"", projection.Serialize("{}", "unavailable-type"));
    }

    [Fact]
    public void ProjectionFailuresEraseWithoutAffectingApplicationValues()
    {
        using var services = Services();
        var projection = new RedactingJson(services);
        var value = new BadGetter();
        Assert.Equal("", JsonSerializer.Deserialize<JsonElement>(projection.Serialize(value)).GetProperty("Value").GetString());
        Assert.Equal("\"\"", projection.Serialize("not-json", typeof(Note).AssemblyQualifiedName!));
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, Capture? logs = null,
        Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs ?? new Capture()));
        services.AddIxIFlow();
        if (store != null)
            services.AddSingleton(store);
        services.AddTransient<Noop>();
        services.AddTransient<LeavesNote>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void Hmac(IServiceCollection services, byte scope)
    {
#pragma warning disable EXTEXP0002
        services.AddRedaction(builder => builder.SetHmacRedactor(options =>
        {
            options.KeyId = scope;
            options.Key = Convert.ToBase64String(Enumerable.Repeat(scope, 32).ToArray());
        }, SensitiveAttribute.Classification));
#pragma warning restore EXTEXP0002
    }

    public sealed class Data { [Sensitive] public string Secret { get; set; } = RedactionTests.Secret; }
    public sealed class Notice { [Sensitive] public string Secret { get; set; } = RedactionTests.Secret; }
    public sealed class Note
    {
        [Sensitive] public string Secret { get; set; } = RedactionTests.Secret;
        public string Public { get; set; } = "visible";
    }
    public sealed class Shapes
    {
        [Sensitive] public int Number { get; set; } = 1234567;
        [Sensitive] public Note Whole { get; set; } = new();
        [Sensitive] public List<string> List { get; set; } = [Secret, Secret];
        [Sensitive] public List<int> Numbers { get; set; } = [1234567, 1234567];
        [Sensitive] public List<Note> Objects { get; set; } = [new(), new()];
        public Note Nested { get; set; } = new();
        public List<Note> NestedList { get; set; } = [new()];
        [Personal] public string Taxonomy { get; set; } = Secret;
    }
    public sealed class PersonalAttribute() : DataClassificationAttribute(new DataClassification("App", "Personal"));
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class LeavesNote : IAsyncActivity
    {
        [Sensitive] public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (context.Attempt.Number == 1)
            {
                context.Attempt.Note = new Note { Secret = Value };
                throw new InvalidOperationException("safe retry reason");
            }
            Assert.Equal(Secret, Assert.IsType<Note>(Assert.Single(context.Attempt.Previous).Note).Secret);
            return Task.CompletedTask;
        }
    }

    [JsonConverter(typeof(OpaqueConverter))]
    public sealed class Opaque { [Sensitive] public string Secret { get; set; } = RedactionTests.Secret; }
    public sealed class OpaqueConverter : JsonConverter<Opaque>
    {
        public override Opaque Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
        public override void Write(Utf8JsonWriter writer, Opaque value, JsonSerializerOptions options) => writer.WriteStringValue(value.Secret);
    }
    public sealed class OpaqueMembers
    {
        public Opaque Unmarked { get; set; } = new();
        [Sensitive] public Opaque Marked { get; set; } = new();
        [JsonConverter(typeof(OpaqueConverter))] public Opaque Converted { get; set; } = new();
        public JsonElement Json { get; set; } = JsonSerializer.SerializeToElement(Secret);
    }
    public sealed class BadGetter { public string Value => throw new InvalidOperationException(Secret); }
    private sealed class BrokenProvider(string failure) : IRedactorProvider
    {
        public Redactor GetRedactor(DataClassificationSet classifications) => failure switch
        {
            "missing" => null!,
            "provider-throws" => throw new InvalidOperationException(Secret),
            _ => new BrokenRedactor()
        };
    }
    private sealed class BrokenRedactor : Redactor
    {
        public override int GetRedactedLength(ReadOnlySpan<char> input) => throw new InvalidOperationException(Secret);
        public override int Redact(ReadOnlySpan<char> source, Span<char> destination) => throw new InvalidOperationException(Secret);
    }
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(Capture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                capture.Entries.Enqueue(formatter(state, exception));
                if (exception != null)
                    capture.Entries.Enqueue(exception.ToString());
                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                    foreach (var property in properties)
                        capture.Entries.Enqueue($"{property.Key}={property.Value}");
            }
        }
    }
}
