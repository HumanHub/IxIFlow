using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "waits")]
public sealed class SignedReplyTokenTests
{
    private static readonly byte[] Current = Enumerable.Repeat((byte)17, 32).ToArray();
    private static readonly byte[] Previous = Enumerable.Repeat((byte)23, 32).ToArray();
    private static readonly byte[] Retired = Enumerable.Repeat((byte)31, 32).ToArray();
    private static ReplyTokenKeyRing Ring => new(Current, Previous);

    [Theory]
    [InlineData("instance"), InlineData("wait"), InlineData("subject"), InlineData("version"), InlineData("signature")]
    [InlineData("unsigned"), InlineData("retired"), InlineData("malformed"), InlineData("signed-unknown-version")]
    public async Task EveryUnauthenticatedPartIsUnknownWithoutAnyStoreCall(string part)
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var services = Services(store.Object, Ring);
        var client = services.GetRequiredService<IWorkflowClient>();
        var token = ReplyToken.Sign(Ring, "instance", "wait", ReplyToken.Digest("order"));
        var bytes = Decode(token);
        var fields = JsonSerializer.Deserialize<string?[]>(bytes.AsSpan(0, bytes.Length - 32))!;
        switch (part)
        {
            case "instance": fields[1] = "other-instance"; break;
            case "wait": fields[2] = "other-wait"; break;
            case "subject": fields[3] = ReplyToken.Digest("another-order"); break;
            case "version": fields[0] = "3"; break;
            case "signature": bytes[^1] ^= 1; token = Encode(bytes); break;
            case "unsigned": token = Encode(JsonSerializer.SerializeToUtf8Bytes(new[] { "1", "instance", "wait", (string?)null })); break;
            case "retired": token = ReplyToken.Sign(new(Retired), "instance", "wait"); break;
            case "malformed": token = "not-a-token"; break;
            case "signed-unknown-version":
                fields[0] = "3";
                var payload = JsonSerializer.SerializeToUtf8Bytes(fields);
                token = Encode(payload.Concat(HMACSHA256.HashData(Current, payload)).ToArray());
                break;
        }
        if (part is "instance" or "wait" or "subject" or "version")
            token = Encode(JsonSerializer.SerializeToUtf8Bytes(fields).Concat(bytes[^32..]).ToArray());
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ReplyAsync(token, new Answer()));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Equal("Unknown reply token", error.Message);
        Assert.False(ReplyToken.TryVerify(Ring, token, out var instance, out var wait, out var subject));
        Assert.Equal("", instance); Assert.Equal("", wait); Assert.Null(subject);
        var ingress = services.GetRequiredService<IWorkflowIngressClient>();
        var entry = new WorkflowEvent("instance", "event", typeof(Answer).AssemblyQualifiedName!, "{}");
        var ingressError = await Assert.ThrowsAsync<WorkflowEngineException>(() => ingress.AcceptReplyAsync(token, entry));
        Assert.Equal("Unknown reply token", ingressError.Message);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARawWaitIdCannotBypassIngressVerification()
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var services = Services(store.Object, Ring);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<IWorkflowIngressClient>()
            .AcceptEventAsync(new("instance", "event", typeof(Answer).AssemblyQualifiedName!, "{}", TargetWaitId: "wait")));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemovingAPreviousVerificationKeyRevokesItsTokensBeforeStoreAccess()
    {
        var token = ReplyToken.Sign(new(Previous), "instance", "wait");
        Assert.True(ReplyToken.TryVerify(Ring, token, out _, out _, out _));
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var services = Services(store.Object, new(Current));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<IWorkflowClient>()
            .ReplyAsync(token, new Answer()));
        Assert.Equal("Unknown reply token", error.Message);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void PaginationHidesWaitIdsRotatesKeysAndCannotBeUsedAsAReplyToken()
    {
        var cursor = WaitPageCursor.Create(new(Previous), "private-wait-id");
        Assert.True(WaitPageCursor.TryRead(Ring, cursor, out var wait));
        Assert.Equal("private-wait-id", wait);
        Assert.DoesNotContain("private-wait-id", Encoding.UTF8.GetString(Decode(cursor)));
        Assert.False(ReplyToken.TryVerify(Ring, cursor, out _, out _, out _));
        Assert.False(WaitPageCursor.TryRead(new(Current), cursor, out _));
        var bytes = Decode(cursor); bytes[^1] ^= 1;
        Assert.False(WaitPageCursor.TryRead(Ring, Encode(bytes), out _));
        Assert.False(WaitPageCursor.TryRead(Ring, ReplyToken.Sign(Ring, "instance", "wait"), out _));
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

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task CurrentAndPreviousKeysCanAnswerAnOpenWait(bool previous)
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store, Ring);
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Workflow.Create<Data>("SignedPlainWait").Step<Noop>().WaitFor<Answer>("answer").Build(), new Data());
        var open = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
        Assert.True(ReplyToken.TryVerify(new(Current), open.Token, out _, out _, out _));
        var token = previous ? ReplyToken.Sign(new(Previous), result.InstanceId, open.WaitId) : open.Token;
        var answered = await client.ReplyAsync(token, new Answer());
        Assert.Equal(EventOutcome.Consumed, answered.Event!.Outcome);
    }

    [Fact]
    public async Task RequestIssuanceSignsAndDiagnosticsContainOnlyWaitReferences()
    {
        var logs = new CaptureLogs();
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var services = Services(store, Ring, collection =>
        {
            collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            collection.AddSingleton(probe); collection.AddTransient<Request>();
        });
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("SignedRequest")
            .Request<Request>("request", subject: _ => "order")
            .WaitFor<Answer>("request", matches: (answer, _) => answer.Accepted).Build(), new Data());
        Assert.True(ReplyToken.TryVerify(new(Current), probe.Token, out _, out var waitId, out var subject));
        Assert.Equal(ReplyToken.Digest("order"), subject);
        Assert.Empty(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        Assert.Equal(EventRejection.NotMatched, (await client.ReplyAsync(probe.Token!, new Answer())).Event!.Rejection);
        var trace = await store.GetTraceAsync(started.InstanceId, 0, 1000);
        var diagnostics = JsonSerializer.Serialize(new { trace, snapshot = await client.GetSnapshotAsync(started.InstanceId),
            pending = await client.GetPendingActivitiesAsync(started.InstanceId), logs = logs.Messages });
        Assert.Contains(WaitReference.Of(started.InstanceId, waitId), diagnostics);
        foreach (var secret in new[] { probe.Token!, waitId, Convert.ToBase64String(Current), Convert.ToHexStringLower(Current) })
            Assert.DoesNotContain(secret, diagnostics);
    }

    [Fact]
    public async Task StartupRequiresKeysAndTheRingNeverExposesOrBorrowsThem()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); builder.Services.AddIxIFlow();
        using var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        foreach (var length in new[] { 0, 1, 31 })
        {
            Assert.Throws<ArgumentException>(() => new ReplyTokenKeyRing(new byte[length]));
            Assert.Throws<ArgumentException>(() => new ReplyTokenKeyRing(Current, new byte[length]));
        }
        var key = Current.ToArray(); var old = Previous.ToArray(); var ring = new ReplyTokenKeyRing(key, old);
        Array.Clear(key); Array.Clear(old);
        Assert.True(ReplyToken.TryVerify(new(Current), ReplyToken.Sign(ring, "i", "w"), out _, out _, out _));
        Assert.True(ReplyToken.TryVerify(ring, ReplyToken.Sign(new(Previous), "i", "w"), out _, out _, out _));
        Assert.Equal("{}", JsonSerializer.Serialize(ring));
        Assert.DoesNotContain(Convert.ToBase64String(Current), ring.ToString());
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, ReplyTokenKeyRing ring,
        Action<IServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection(); collection.AddLogging();
        collection.AddIxIFlow(options => options.ReplyTokenKeys = ring);
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
