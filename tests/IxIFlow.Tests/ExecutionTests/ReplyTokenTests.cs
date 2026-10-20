using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "waits")]
public sealed class ReplyTokenTests
{
    [Theory]
    [InlineData("version"), InlineData("legacy"), InlineData("signed"), InlineData("malformed")]
    [InlineData("truncated"), InlineData("empty-instance"), InlineData("empty-wait"), InlineData("noncanonical")]
    public async Task MalformedAndUnsupportedTokensAreUnknownWithoutAnyStoreCall(string part)
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var services = Services(store.Object);
        var client = services.GetRequiredService<IWorkflowClient>();
        var token = ReplyToken.Create("instance", "wait", ReplyToken.Digest("order"));
        var fields = JsonSerializer.Deserialize<string?[]>(Decode(token))!;
        switch (part)
        {
            case "version": fields[0] = "999"; break;
            case "legacy": fields[0] = "1"; break;
            case "signed": fields[0] = "2"; break;
            case "empty-instance": fields[1] = ""; break;
            case "empty-wait": fields[2] = ""; break;
        }
        token = Encode(JsonSerializer.SerializeToUtf8Bytes(fields));
        if (part == "signed") token = Encode(Decode(token).Concat(new byte[32]).ToArray());
        if (part == "malformed") token = "not-a-token";
        if (part == "truncated") token = token[..^1];
        if (part == "noncanonical") token += "=";
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ReplyAsync(token, new Answer()));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Equal("Unknown reply token", error.Message);
        Assert.False(ReplyToken.TryRead(token, out var instance, out var wait, out var subject));
        Assert.Equal("", instance); Assert.Equal("", wait); Assert.Null(subject);
        var ingress = services.GetRequiredService<IWorkflowIngressClient>();
        var entry = new WorkflowEvent("instance", "event", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Answer)), "{}");
        var ingressError = await Assert.ThrowsAsync<WorkflowEngineException>(() => ingress.AcceptReplyAsync(token, entry));
        Assert.Equal("Unknown reply token", ingressError.Message);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("instance"), InlineData("wait"), InlineData("subject")]
    public async Task ChangedAddressOrSubjectCannotAnswerTheIssuedWait(string part)
    {
        var probe = new Probe();
        await using var services = Services(new InMemoryWorkflowStateRepository(), collection =>
        {
            collection.AddSingleton(probe);
            collection.AddTransient<Request>();
        });
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("AddressedRequest")
            .Request<Request>("request", subject: _ => "order").WaitFor<Answer>("request").Build();
        var first = await client.RunAsync(definition, new Data());
        var token = probe.Token!;
        Assert.True(ReplyToken.TryRead(token, out var instance, out var wait, out var subject));
        var second = await client.RunAsync(definition, new Data());
        var changed = ReplyToken.Create(part == "instance" ? second.InstanceId : instance,
            part == "wait" ? Guid.NewGuid().ToString("N") : wait,
            part == "subject" ? ReplyToken.Digest("other-order") : subject);
        var refused = await client.ReplyAsync(changed, new Answer());
        Assert.Equal(EventOutcome.Rejected, refused.Event!.Outcome);
        Assert.Equal(part == "subject" ? EventRejection.SubjectMismatch : EventRejection.UnknownWait, refused.Event.Rejection);
        Assert.Equal(WorkflowExecutionStatus.Suspended, await client.GetStatusAsync(first.InstanceId));
        Assert.Equal(WorkflowExecutionStatus.Suspended, await client.GetStatusAsync(second.InstanceId));
        Assert.Equal(EventOutcome.Consumed, (await client.ReplyAsync(token, new Answer())).Event!.Outcome);
    }

    [Fact]
    public async Task ARawWaitIdCannotBypassReplyIngress()
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var services = Services(store.Object);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<IWorkflowIngressClient>()
            .AcceptEventAsync(new("instance", "event", typeof(Answer).AssemblyQualifiedName!, "{}", TargetWaitId: "wait")));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void PaginationCarriesOnlyAReferenceAndCannotBeUsedAsAReplyToken()
    {
        const string waitId = "private-wait-id";
        var reference = WaitReference.Of("instance", waitId);
        var cursor = WaitPageCursor.Create(reference);
        Assert.True(WaitPageCursor.TryRead(cursor, out var boundary));
        Assert.Equal(reference, boundary);
        Assert.Equal(reference, Encoding.UTF8.GetString(Decode(cursor)));
        Assert.DoesNotContain(waitId, Encoding.UTF8.GetString(Decode(cursor)));
        Assert.False(ReplyToken.TryRead(cursor, out _, out _, out _));
        Assert.False(WaitPageCursor.TryRead(ReplyToken.Create("instance", waitId), out _));
        Assert.False(WaitPageCursor.TryRead(Encode(Encoding.UTF8.GetBytes("invalid-boundary")), out _));
    }

    [Fact]
    public async Task AFailedWaitReadUsesOnlyItsDiagnosticReferenceInErrorContext()
    {
        const string waitId = "private-wait-id-for-an-infrastructure-error";
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        store.Setup(s => s.GetWaitAsync("instance", waitId)).ThrowsAsync(new IOException("Read failed"));
        var guarded = GuardedStateRepository.Wrap(store.Object);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => guarded.GetWaitAsync("instance", waitId));
        Assert.Contains(WaitReference.Of("instance", waitId), error.Message);
        Assert.DoesNotContain(waitId, error.ToString());
    }

    [Fact]
    public async Task ADiscoveredCapabilityCanAnswerAnOpenWait()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Workflow.Create<Data>("PlainWait").Step<Noop>().WaitFor<Answer>("answer").Build(), new Data());
        var open = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
        Assert.True(ReplyToken.TryRead(open.Token, out _, out _, out _));
        var answered = await client.ReplyAsync(open.Token, new Answer());
        Assert.Equal(EventOutcome.Consumed, answered.Event!.Outcome);
    }

    [Fact]
    public async Task RequestIssuanceCarriesSubjectAndDiagnosticsContainOnlyWaitReferences()
    {
        var logs = new CaptureLogs();
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var services = Services(store, collection =>
        {
            collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            collection.AddSingleton(probe); collection.AddTransient<Request>();
        });
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("Request")
            .Request<Request>("request", subject: _ => "order")
            .WaitFor<Answer>("request", matches: (answer, _) => answer.Accepted).Build(), new Data());
        Assert.True(ReplyToken.TryRead(probe.Token, out _, out var waitId, out var subject));
        Assert.Equal(ReplyToken.Digest("order"), subject);
        Assert.Empty(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        Assert.Equal(EventRejection.NotMatched, (await client.ReplyAsync(probe.Token!, new Answer())).Event!.Rejection);
        var trace = await store.GetTraceAsync(started.InstanceId, 0, 1000);
        var diagnostics = JsonSerializer.Serialize(new { trace, snapshot = await client.GetSnapshotAsync(started.InstanceId),
            pending = await client.GetPendingActivitiesAsync(started.InstanceId), logs = logs.Messages });
        Assert.Contains(WaitReference.Of(started.InstanceId, waitId), diagnostics);
        foreach (var secret in new[] { probe.Token!, waitId })
            Assert.DoesNotContain(secret, diagnostics);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store,
        Action<IServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection(); collection.AddLogging();
        collection.AddIxIFlow();
        collection.AddTransient<Noop>(); collection.AddSingleton(store); collection.AddSingleton(new InProcessInstanceGate());
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }
    private static byte[] Decode(string token) => Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/').PadRight((token.Length + 3) / 4 * 4, '='));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Data;
    public sealed class Answer { public bool Accepted { get; set; } }
    public sealed class Probe { public string? Token { get; set; } }
    public sealed class Request(Probe probe) : IRequestActivity<Answer>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Token = context.Reply!.Token; return Task.CompletedTask; }
    }
    private sealed class CaptureLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);
        public void Dispose() { }
        private sealed class CapturedLogger(CaptureLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            { owner.Messages.Add(JsonSerializer.Serialize(state)); return null; }
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            { owner.Messages.Add(format(state, error)); owner.Messages.Add(JsonSerializer.Serialize(state)); }
        }
    }
}
