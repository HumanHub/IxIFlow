using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-10")]
public sealed class DebugAdapterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private const string SessionToken = "application-issued-debug-session-token-for-tests";
    private const string BuildRoot = "C:/build/workflows";
    private const string LocalRoot = "C:/checkout/workflows";

    [Fact]
    public async Task FramedClientDrivesYamlBreakpointsEditsSteppingThreadsPauseAndReplay()
    {
        var store = new InMemoryWorkflowStateRepository();
        var grants = new List<DebugAuthorization>();
        await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "DapTour").WaitFor<Go>("go")
            .Sequence(b => b.Step<Noop>().WithId("one", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 30)
                .Step<Noop>().Step<Noop>()).WithId("sequence", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 20)
            .Step<Noop>().WithId("after", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 50)
            .Parallel(p => p.Do(b => b.Step<Noop>().WithId("branch-one", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 70))
                .Do(b => b.Step<Noop>().WithId("branch-two", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 80)))
            .WithId("parallel", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 60)
            .WaitFor<Go>("tail").WithId("tail", sourcePath: BuildRoot + "/tour.yaml", sourceLine: 90)
            .Step<Noop>().Build();
        var started = await client.RunAsync(definition, new Data());
        await using var dap = new Client(ops, Options(g => { lock (grants) grants.Add(g); return true; }));
        await dap.Ok("initialize", new { adapterID = "ixiflow" });
        await dap.Ok("attach", new { instanceId = started.InstanceId, sessionToken = SessionToken, actor = "forged",
            sourceRoots = new Dictionary<string, string> { [BuildRoot] = LocalRoot } });
        await dap.Event("initialized");
        var breakpoint = await dap.Ok("setBreakpoints", new { source = new { path = LocalRoot + "/tour.yaml" },
            breakpoints = new[] { new { line = 11 } }, actor = "forged" });
        var verified = breakpoint["body"]!["breakpoints"]![0]!;
        Assert.True(verified.Value<bool>("verified")); Assert.Equal(20, verified.Value<int>("line"));
        await dap.Ok("configurationDone");
        await client.ResumeAsync(started.InstanceId, "go", new Go());
        var stopped = await dap.Event("stopped");
        Assert.Equal("breakpoint", stopped["body"]!.Value<string>("reason"));
        Assert.Equal(verified.Value<int>("id"), stopped["body"]!["hitBreakpointIds"]![0]!.Value<int>());
        Assert.True(stopped["body"]!.Value<bool>("allThreadsStopped"));
        var threadId = await dap.Thread();
        var stack = await dap.Ok("stackTrace", new { threadId });
        var frame = stack["body"]!["stackFrames"]![0]!;
        Assert.Equal(LocalRoot + "/tour.yaml", frame["source"]!.Value<string>("path"));
        Assert.Equal(20, frame.Value<int>("line"));
        var frameId = frame.Value<int>("id");
        var reference = await dap.DataReference(frameId);
        var variables = await dap.Ok("variables", new { variablesReference = reference, start = 0, count = 1 });
        Assert.Single((JArray)variables["body"]!["variables"]!);
        Assert.Equal("0", (await dap.Ok("evaluate", new { expression = "data.Value", frameId }))["body"]!.Value<string>("result"));
        Assert.False((await dap.Request("evaluate", new { expression = "data.Value + 1", frameId })).Value<bool>("success"));
        await dap.Ok("setVariable", new { variablesReference = reference, name = "Value", value = "5", actor = "forged" });
        await dap.Event("invalidated");
        Assert.Equal(5, (await ops.InspectAsync(started.InstanceId))!.Data.Single().Value.GetProperty("Value").GetInt32());
        Assert.False((await dap.Request("setVariable", new { variablesReference = reference, name = "Value", value = "8" })).Value<bool>("success"));
        await dap.Ok("next", new { threadId }); await dap.Event("stopped");
        Assert.False((await dap.Event("continued"))["body"]!.Value<bool>("allThreadsContinued"));
        Assert.Equal(3, services.GetRequiredService<Probe>().Count);
        await dap.Ok("stepIn", new { threadId }); await dap.Event("stopped"); // after -> fork
        await dap.Ok("stepIn", new { threadId }); await dap.Event("stopped"); // enter fork
        var threads = (JArray)(await dap.Ok("threads"))["body"]!["threads"]!;
        Assert.True(threads.Count >= 3);
        var childId = threads.First(t => t.Value<int>("id") != threadId).Value<int>("id");
        await dap.Ok("stepOut", new { threadId = childId }); await dap.Event("stopped");
        Assert.Contains(dap.Messages, m => m.Value<string>("event") == "thread" && m["body"]!.Value<string>("reason") == "started");
        await dap.Ok("continue", new { threadId });
        await Eventually(async () => (await ops.InspectAsync(started.InstanceId))!.Summary.Status == WorkflowStatus.Suspended);
        await dap.Ok("pause", new { threadId, actor = "forged" });
        Assert.Equal("pause", (await dap.Event("stopped"))["body"]!.Value<string>("reason"));
        Assert.False((await dap.Request("setVariable", new { variablesReference = reference, name = "Value", value = "9" })).Value<bool>("success"));
        var current = await dap.Thread();
        await dap.Ok("stepIn", new { threadId = current });
        Assert.Equal("blocked", (await dap.Event("stopped"))["body"]!.Value<string>("reason"));
        var liveRevision = (await ops.InspectAsync(started.InstanceId))!.Revision;
        await dap.Ok("stepBack", new { threadId = current }); await dap.Event("stopped");
        await dap.Ok("reverseContinue", new { threadId = current });
        var reverseStop = await dap.Event("stopped");
        Assert.Equal("breakpoint", reverseStop["body"]!.Value<string>("reason"));
        Assert.Contains(verified.Value<int>("id"), reverseStop["body"]!["hitBreakpointIds"]!.Values<int>());
        Assert.Equal(liveRevision, (await ops.InspectAsync(started.InstanceId))!.Revision);
        await dap.Ok("disconnect");
        Assert.Contains(dap.Messages, m => m.Value<string>("event") == "thread" && m["body"]!.Value<string>("reason") == "exited");
        var audit = (await ops.GetTraceAsync(started.InstanceId)).Where(e => e.Entry.Payload is DebugCommandTrace or DataEditedTrace).ToArray();
        Assert.NotEmpty(audit);
        Assert.All(audit, e => Assert.DoesNotContain("forged", JsonSerializer.Serialize(e)));
        lock (grants) Assert.All(grants, g => Assert.Equal("authenticated-developer", g.Actor));
    }

    [Fact]
    public async Task AuthenticationAndPerOperationAuthorizationCannotBeOverriddenByRequests()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var started = await services.GetRequiredService<IWorkflowClient>().RunAsync(new WorkflowBuilder<Data, object>([], "Auth").WaitFor<Go>("go").Build(), new Data());
        var grants = new List<DebugAuthorization>();
        var denied = DebugOperation.PauseAndStep;
        var options = Options(g => { lock (grants) grants.Add(g); return g.Operation != denied; });
        await using (var unauthenticated = new Client(ops, options))
        {
            await unauthenticated.Ok("initialize");
            Assert.False((await unauthenticated.Request("attach", new { instanceId = started.InstanceId, actor = "admin" })).Value<bool>("success"));
            Assert.False((await unauthenticated.Request("threads")).Value<bool>("success"));
            Assert.Empty(grants);
        }
        await using var dap = new Client(ops, options);
        await dap.Attach(started.InstanceId);
        Assert.False((await dap.Request("pause", new { actor = "admin" })).Value<bool>("success"));
        Assert.Equal(WorkflowStatus.Suspended, (await ops.InspectAsync(started.InstanceId))!.Summary.Status);
        denied = DebugOperation.Edit;
        await ops.PauseAsync(started.InstanceId, "setup"); await dap.Event("stopped");
        var stack = await dap.Ok("stackTrace", new { threadId = await dap.Thread() });
        var frameId = stack["body"]!["stackFrames"]![0]!.Value<int>("id");
        var handle = await dap.DataReference(frameId);
        Assert.False((await dap.Request("setVariable", new { variablesReference = handle, name = "Value", value = "7", actor = "admin" })).Value<bool>("success"));
        denied = DebugOperation.Breakpoints;
        Assert.False((await dap.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name = "root/0" } } })).Value<bool>("success"));
        denied = DebugOperation.Inspect;
        Assert.False((await dap.Request("variables", new { variablesReference = handle })).Value<bool>("success"));
        lock (grants) Assert.All(grants, g => Assert.Equal("authenticated-developer", g.Actor));
    }

    [Fact]
    public async Task RepliesNeverExposeRawWaitIdsReplyTokensOrTheSessionTokenEvenAfterEdits()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>(); var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(new WorkflowBuilder<Data, object>([], "PrivacyDap").WaitFor<Go>("go").Build(), new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new() { InstanceId = result.InstanceId }));
        await ops.PauseAsync(result.InstanceId, "setup");
        await using var dap = new Client(ops, Options()); await dap.Attach(result.InstanceId); await dap.Event("stopped");
        foreach (var secret in new[] { wait.WaitId, wait.Token, SessionToken })
        {
            var frame = (await dap.Ok("stackTrace", new { threadId = await dap.Thread() }))["body"]!["stackFrames"]![0]!.Value<int>("id");
            var handle = await dap.DataReference(frame);
            await dap.Ok("setVariable", new { variablesReference = handle, name = "Secret", value = JsonSerializer.Serialize("url/" + secret + "/suffix") });
            await dap.Ok("evaluate", new { expression = "data.Secret" });
            frame = (await dap.Ok("stackTrace", new { threadId = await dap.Thread() }))["body"]!["stackFrames"]![0]!.Value<int>("id");
            await dap.Ok("variables", new { variablesReference = await dap.DataReference(frame) });
        }
        await dap.Ok("loadedSources");
        await dap.Ok("stepBack", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        await dap.Ok("disconnect");
        Assert.All(dap.Messages, message =>
        {
            var text = message.ToString();
            Assert.DoesNotContain(wait.WaitId, text); Assert.DoesNotContain(wait.Token, text); Assert.DoesNotContain(SessionToken, text);
        });
    }

    [Fact]
    public async Task LaunchSavesTheEntryHoldBeforeAnyActivityAndUsesTheAuthenticatedActor()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var definition = Workflow.Create<Data>("LaunchDap").Step<Noop>().WithId("entry").Step<Noop>().Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var authorizedIds = new List<string>();
        await using var dap = new Client(services.GetRequiredService<IWorkflowOperations>(), Options(g => { authorizedIds.Add(g.InstanceId); return true; }));
        await dap.Ok("initialize");
        await dap.Ok("launch", new { workflow = definition.Name, version = definition.Version, input = new { Value = 4 },
            stopOnEntry = true, sessionToken = SessionToken, actor = "forged" });
        await dap.Ok("configurationDone"); await dap.Event("stopped");
        Assert.Equal(0, services.GetRequiredService<Probe>().Count);
        var id = authorizedIds.Distinct().Single();
        var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!;
        Assert.Equal(WorkflowStatus.Paused, inspection.Summary.Status);
        Assert.Equal(4, inspection.Data.Single().Value.GetProperty("Value").GetInt32());
        var entry = (await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id)).Single(e => e.Entry.EntryType == TraceEntryType.Paused);
        Assert.Equal("authenticated-developer", Assert.IsType<DebugCommandTrace>(entry.Entry.Payload).Actor);
        await dap.Ok("disconnect");
        Assert.Equal(WorkflowStatus.Paused, (await store.GetWorkflowInstanceAsync(id))!.Status);
    }

    [Fact]
    public async Task RemoteOwnerStopIsFoundByTheBoundedPollAndAnotherSessionCannotUseHandles()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var owner = Services(store); await using var reader = Services(store);
        var definition = new WorkflowBuilder<Data, object>([], "RemoteDap").WaitFor<Go>("go").Build();
        var result = await owner.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        await reader.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var ops = reader.GetRequiredService<IWorkflowOperations>();
        await using var first = new Client(ops, Options()); await first.Attach(result.InstanceId);
        await owner.GetRequiredService<IWorkflowOperations>().PauseAsync(result.InstanceId, "remote");
        await first.Event("stopped");
        var frame = (await first.Ok("stackTrace", new { threadId = await first.Thread() }))["body"]!["stackFrames"]![0]!.Value<int>("id");
        var handle = await first.DataReference(frame);
        await using var second = new Client(ops, Options()); await second.Attach(result.InstanceId);
        Assert.False((await second.Request("setVariable", new { variablesReference = handle, name = "Value", value = "7" })).Value<bool>("success"));
        await first.Ok("disconnect"); await second.Ok("disconnect");
    }

    [Fact]
    public void RegistrationRequiresPolicyAndRejectsUnauthenticatedOrNonlocalTcp()
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        Assert.Throws<InvalidOperationException>(() => services.AddDebugging(o => o.NamedPipe("debug-test")));
        var options = new DebuggingOptions();
        Assert.Throws<ArgumentException>(() => options.Tcp(IPAddress.Any, 4711, SessionToken, "actor"));
        Assert.Throws<ArgumentException>(() => options.Tcp(IPAddress.Loopback, 4711, "short", "actor"));
        services.AddDebugging(o => { o.NamedPipe("debug-test"); o.Authorize = (_, _) => ValueTask.FromResult(false); });
        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IHostedService>(), service => service.GetType().Name == "DebugAdapterService");
    }

    [Fact]
    public async Task SourcesFunctionConditionsExpressionEditsAndLocationlessFramesUseTheCatalog()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "ixiflow-dap-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(sourcePath, "# Published workflow source\n# " + SessionToken);
        try
        {
            var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store, new SourceBundle(sourcePath));
            var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
            var definition = new WorkflowBuilder<Data, object>([], "SourcesDap").WaitFor<Go>("go").WithId("gate", sourcePath, 1)
                .Step<Noop>().WithId("located", sourcePath, 8).Step<Noop>().WithId("unlocated", "", 0)
                .WaitFor<Go>("tail").Build();
            var result = await client.RunAsync(definition, new Data());
            await using var dap = new Client(ops, Options()); await dap.Attach(result.InstanceId);
            var source = ((JArray)(await dap.Ok("loadedSources"))["body"]!["sources"]!).Single(s => s.Value<string>("path")!.EndsWith(Path.GetFileName(sourcePath), StringComparison.Ordinal));
            var content = await dap.Ok("source", new { sourceReference = source.Value<int>("sourceReference") });
            Assert.Contains("Published workflow source", content["body"]!.Value<string>("content"));
            Assert.DoesNotContain(SessionToken, content.ToString());
            File.Delete(sourcePath);
            var bundled = await dap.Ok("source", new { sourceReference = source.Value<int>("sourceReference") });
            Assert.Equal("Published bundle source", bundled["body"]!.Value<string>("content"));
            Assert.False((await dap.Request("source", new { sourceReference = 99999 })).Value<bool>("success"));
            var locations = await dap.Ok("breakpointLocations", new { source = new { path = sourcePath }, line = 2, endLine = 9 });
            Assert.Equal(8, locations["body"]!["breakpoints"]![0]!.Value<int>("line"));
            var functions = await dap.Ok("setFunctionBreakpoints", new { breakpoints = new[] {
                new { name = "located", condition = "context.WorkflowData.Value == 0", hitCondition = "== 1" } } });
            Assert.True(functions["body"]!["breakpoints"]![0]!.Value<bool>("verified"));
            await client.ResumeAsync(result.InstanceId, "go", new Go()); await dap.Event("stopped");
            var threadId = await dap.Thread();
            var frame = (await dap.Ok("stackTrace", new { threadId }))["body"]!["stackFrames"]![0]!.Value<int>("id");
            await dap.Ok("setExpression", new { expression = "data.Value", value = "11", frameId = frame });
            Assert.Equal("11", (await dap.Ok("evaluate", new { expression = "data.Value" }))["body"]!.Value<string>("result"));
            await dap.Ok("next", new { threadId }); await dap.Event("stopped");
            var subtle = (await dap.Ok("stackTrace", new { threadId }))["body"]!["stackFrames"]![0]!;
            Assert.Equal("subtle", subtle.Value<string>("presentationHint"));
            Assert.True(subtle["source"] == null || subtle["source"]!.Type == JTokenType.Null);
            var single = await dap.Ok("continue", new { threadId, singleThread = true });
            Assert.False(single["body"]!.Value<bool>("allThreadsContinued"));
            Assert.Equal("blocked", (await dap.Event("stopped"))["body"]!.Value<string>("reason"));
            await dap.Ok("disconnect");
        }
        finally { File.Delete(sourcePath); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectHonorsTheExplicitContinuePolicyAndNeverTerminates(bool continueOnDisconnect)
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            new WorkflowBuilder<Data, object>([], "DisconnectDap").WaitFor<Go>("go").Build(), new Data());
        await ops.PauseAsync(result.InstanceId, "setup");
        await using var dap = new Client(ops, Options());
        await dap.Ok("initialize"); await dap.Ok("attach", new { instanceId = result.InstanceId, sessionToken = SessionToken, continueOnDisconnect });
        await dap.Ok("configurationDone"); await dap.Event("stopped");
        await dap.Ok("disconnect", new { terminateDebuggee = true });
        if (continueOnDisconnect) await Eventually(async () => (await ops.InspectAsync(result.InstanceId))!.Summary.Status == WorkflowStatus.Suspended);
        Assert.Equal(continueOnDisconnect ? WorkflowStatus.Suspended : WorkflowStatus.Paused, (await ops.InspectAsync(result.InstanceId))!.Summary.Status);
    }

    [Fact]
    public async Task ContinueOnDisconnectReturnsWhileTheApplicationOwnsALongActivity()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("DetachedDap").Step<Block>().Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var id = Guid.NewGuid().ToString("N");
        await ops.LaunchAsync(id, definition.Name, definition.Version, JsonSerializer.SerializeToElement(new Data()), true, "setup");
        await using var dap = new Client(ops, Options());
        await dap.Ok("initialize"); await dap.Ok("attach", new { instanceId = id, sessionToken = SessionToken, continueOnDisconnect = true });
        await dap.Ok("configurationDone"); await dap.Event("stopped");
        try
        {
            await dap.Ok("disconnect");
            await services.GetRequiredService<Probe>().Started.Task.WaitAsync(Patience);
            Assert.Equal(WorkflowStatus.Running, (await ops.InspectAsync(id))!.Summary.Status);
        }
        finally { services.GetRequiredService<Probe>().Finish.TrySetResult(); }
        await Eventually(async () => (await ops.InspectAsync(id))!.Summary.Status == WorkflowStatus.Completed);
    }

    [Fact]
    public async Task FinishedReplayIsReadOnlyAndLocalChangeSubscriptionsObserveOnlyTheirInstance()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var ops = services.GetRequiredService<IWorkflowOperations>(); var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Workflow.Create<Data>("FinishedDap").Step<Noop>().Build(), new Data());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.OpenReplayAsync(result.InstanceId, 1));
        await using var dap = new Client(ops, Options());
        await dap.Ok("initialize"); await dap.Ok("attach", new { instanceId = result.InstanceId, sessionToken = SessionToken, replay = true });
        await dap.Ok("configurationDone"); await dap.Event("stopped");
        await dap.Ok("reverseContinue", new { threadId = 0 }); await dap.Event("stopped");
        Assert.False((await dap.Request("setExpression", new { expression = "data.Value", value = "3" })).Value<bool>("success"));
        await dap.Ok("disconnect");
        var waiting = await client.RunAsync(new WorkflowBuilder<Data, object>([], "NoticesDap").WaitFor<Go>("go").Build(), new Data());
        var changes = 0; var unrelated = 0;
        using (ops.WatchChanges(waiting.InstanceId, () => changes++))
        using (ops.WatchChanges("other-instance", () => unrelated++))
            await ops.PauseAsync(waiting.InstanceId, "setup");
        Assert.True(changes > 0); Assert.Equal(0, unrelated);
        var before = changes;
        await ops.ContinueAsync(waiting.InstanceId, "setup");
        Assert.Equal(before, changes);
    }

    private static DebuggingOptions Options(Func<DebugAuthorization, bool>? authorize = null)
    {
        var options = new DebuggingOptions(); options.Tcp(IPAddress.Loopback, 0, SessionToken, "authenticated-developer");
        options.Authorize = (g, _) => ValueTask.FromResult(authorize?.Invoke(g) ?? true); return options;
    }
    private static ServiceProvider Services(InMemoryWorkflowStateRepository store, IWorkflowDebugSourceProvider? sources = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddBreakpointConditions();
        services.AddSingleton<IWorkflowStateRepository>(store); services.AddSingleton<Probe>(); services.AddTransient<Noop>();
        if (sources != null) services.AddSingleton(sources);
        return services.BuildServiceProvider();
    }
    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
    public sealed class Data { public int Value { get; set; } public string Secret { get; set; } = ""; }
    private sealed class SourceBundle(string path) : IWorkflowDebugSourceProvider
    {
        public Task<string?> ReadAsync(string workflow, int version, string sourcePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(sourcePath == path && workflow == "SourcesDap" ? "Published bundle source" : null);
    }
    public sealed class Go;
    public sealed class Probe
    {
        public int Count;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Finish.Task.WaitAsync(cancellationToken); }
    }
    public sealed class Noop(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Count); return Task.CompletedTask; }
    }

    // Both protocol directions are in-memory pipes; the server is the production library's framed reader/writer.
    private sealed class Client : IAsyncDisposable
    {
        private readonly Pipe _toServer = new(), _toClient = new();
        private readonly DebugAdapterSession _session;
        private readonly Stream _input, _output;
        private readonly Task _server, _pump;
        private readonly Channel<JObject> _messages = Channel.CreateUnbounded<JObject>();
        private readonly List<JObject> _pending = [];
        private int _sequence;
        public System.Collections.Concurrent.ConcurrentQueue<JObject> Messages { get; } = new();
        public Client(IWorkflowOperations operations, DebuggingOptions options)
        {
            _session = new(operations, options);
            _input = _toClient.Reader.AsStream(); _output = _toServer.Writer.AsStream();
            _server = _session.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
            _pump = Pump();
        }
        public async Task<JObject> Request(string command, object? args = null)
        {
            var sequence = ++_sequence;
            var json = JObject.FromObject(new { seq = sequence, type = "request", command, arguments = args ?? new { } }).ToString(Newtonsoft.Json.Formatting.None);
            var body = Encoding.UTF8.GetBytes(json);
            await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await _output.WriteAsync(body); await _output.FlushAsync();
            return await Read(m => m.Value<string>("type") == "response" && m.Value<int>("request_seq") == sequence);
        }
        public async Task<JObject> Ok(string command, object? args = null)
        { var response = await Request(command, args); Assert.True(response.Value<bool>("success"), response.ToString()); return response; }
        public Task<JObject> Event(string name) => Read(m => m.Value<string>("event") == name);
        private async Task<JObject> Read(Func<JObject, bool> predicate)
        {
            var index = _pending.FindIndex(m => predicate(m));
            if (index >= 0) { var found = _pending[index]; _pending.RemoveAt(index); return found; }
            while (true)
            {
                var message = await _messages.Reader.ReadAsync().AsTask().WaitAsync(Patience);
                if (predicate(message)) return message;
                _pending.Add(message);
            }
        }
        public async Task Attach(string id)
        {
            await Ok("initialize"); await Ok("attach", new { instanceId = id, sessionToken = SessionToken });
            await Event("initialized"); await Ok("configurationDone");
        }
        public async Task<int> Thread() => (await Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");
        public async Task<int> DataReference(int frameId) => (await Ok("scopes", new { frameId }))["body"]!["scopes"]![0]!.Value<int>("variablesReference");
        private async Task Pump()
        {
            try
            {
                var one = new byte[1];
                while (true)
                {
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await _input.ReadAsync(one) == 0) return;
                        header.Append((char)one[0]);
                    }
                    var length = int.Parse(header.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    var body = new byte[length]; await _input.ReadExactlyAsync(body);
                    var message = JObject.Parse(Encoding.UTF8.GetString(body));
                    Messages.Enqueue(message); await _messages.Writer.WriteAsync(message);
                }
            }
            catch (ObjectDisposedException) { }
            finally { _messages.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            _session.Dispose(); _output.Dispose();
            await _server.WaitAsync(Patience); await _pump.WaitAsync(Patience);
            _input.Dispose();
        }
    }
}
