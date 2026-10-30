using System.Net;
using System.Text.Json;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
[Trait("Spec", "G-04"), Trait("Spec", "G-06"), Trait("Spec", "G-08"), Trait("Spec", "G-10"), Trait("Spec", "G-11"), Trait("Spec", "H-08")]
public sealed class InlineCallerDebuggerTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task CalleeSourceBreakpointsConditionsFrameScopesAndEditsReachDirectAndNestedSites(bool published)
    {
        var repository = new InMemoryWorkflowStateRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineDap", Guid.NewGuid().ToString("N"));

        if (published)
            await InlineDebuggerFixture.Publish(repository);

        try
        {
            await using var host = InlineDebuggerFixture.Host(repository, new WorkflowCatalog(), published ? cache : null,
                services => services.AddBreakpointConditions());

            var id = await InlineDebuggerFixture.Start(host, published);
            var ops = host.GetRequiredService<IWorkflowOperations>();
            var table = await ops.GetStepTableAsync(id);
            var sites = table.Where(s => s.AuthorId.EndsWith("/enter", StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { "first/enter", "second/nested/enter" }, sites.Select(s => s.AuthorId));
            var location = Assert.Single(sites.Select(s => s.SourceLocation).Distinct())!;
            var colon = location.LastIndexOf(':');
            var path = location[..colon];
            var line = int.Parse(location[(colon + 1)..]);
            Assert.Equal(published ? "inline/InlineDebugLeaf@1/workflow.yaml" : "workflow.yaml", path);
            await using var dap = new Client(ops);
            await dap.Ok("initialize", new { linesStartAt1 = true, columnsStartAt1 = true });
            var clientPath = published ? "S:/sources/InlineDebugLeaf/workflow.yaml" : path;

            await dap.Ok("attach", new
            {
                instanceId = id, sessionToken = Client.Token,
                sourceRoots = published
                    ? new Dictionary<string, string> { ["inline/InlineDebugLeaf@1"] = "S:/sources/InlineDebugLeaf" }
                    : []
            });

            await dap.Ok("configurationDone");
            var loaded = (JArray)(await dap.Ok("loadedSources"))["body"]!["sources"]!;
            var source = Assert.Single(loaded, s => s.Value<string>("path") == clientPath);

            if (published)
            {
                var content = (await dap.Ok("source", new { sourceReference = source.Value<int>("sourceReference") }))["body"]!
                    .Value<string>("content")!;

                Assert.Equal(await ops.GetSourceAsync(id, path), content);
                Assert.Equal("InlineDebugLeaf", IxIFlow.Dsl.Documents.WorkflowYamlSerializer.Read(content).Workflow.Name);
                Assert.Contains("assign", content.Split('\n')[line - 1]);
                Assert.Contains(loaded, s => s.Value<string>("path") == "workflow.yaml");
            }

            var configured = await dap.Ok("setBreakpoints", new
            {
                source = new { path = clientPath },
                breakpoints = new[] { new { line, condition = InlineDebuggerFixture.Condition } }
            });

            Assert.True(configured["body"]!["breakpoints"]![0]!.Value<bool>("verified"));
            var result = await host.GetRequiredService<IWorkflowClient>().ResumeAsync(id, new Signal());

            foreach (var author in new[] { "first/enter", "second/nested/enter" })
            {
                Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
                var inspection = (await ops.InspectAsync(id))!;
                Assert.Equal(author, Assert.Single(inspection.Threads).AuthorStepId);
                Assert.Empty(inspection.DebugState.Stop!.Diagnostics);
                Assert.Equal(2, inspection.Breakpoints.Count);
                var thread = (await dap.Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");
                var stack = (JArray)(await dap.Ok("stackTrace", new { threadId = thread }))["body"]!["stackFrames"]!;
                Assert.Equal(clientPath, stack[0]["source"]!.Value<string>("path"));
                Assert.Equal(line, stack[0].Value<int>("line"));
                var scopes = (JArray)(await dap.Ok("scopes", new { frameId = stack[0].Value<int>("id") }))["body"]!["scopes"]!;
                var dataScope = Assert.Single(scopes, s => s.Value<string>("name") == "Workflow data");

                var variables =
                    (JArray)(await dap.Ok("variables", new { variablesReference = dataScope.Value<int>("variablesReference") }))["body"]![
                        "variables"]!;

                Assert.Contains(variables, v => v.Value<string>("name") == "Secret");
                Assert.DoesNotContain(InlineDebuggerFixture.Secret, variables.ToString());

                // The root frame below every inline body still presents the caller DTO.
                var rootScopes =
                    (JArray)(await dap.Ok("scopes", new { frameId = stack[stack.Count - 1].Value<int>("id") }))["body"]!["scopes"]!;

                var rootData = rootScopes.Single(s => s.Value<string>("name") == "Workflow data");

                var rootValues =
                    (JArray)(await dap.Ok("variables", new { variablesReference = rootData.Value<int>("variablesReference") }))["body"]![
                        "variables"]!;

                Assert.DoesNotContain(rootValues, v => v.Value<string>("name") == "Secret");

                if (author == "first/enter")
                {
                    await dap.Ok("setVariable",
                        new { variablesReference = dataScope.Value<int>("variablesReference"), name = "Value", value = "\"dap-edited\"" });

                    var afterEdit = (await ops.InspectAsync(id))!;
                    Assert.Equal("dap-edited", afterEdit.Data.Single(d => d.FrameId != null).Value.GetProperty("Value").GetString());
                    Assert.Equal("initial", afterEdit.Data.Single(d => d.FrameId == null).Value.GetProperty("Value").GetString());
                    var refreshed = (await dap.Ok("stackTrace", new { threadId = thread }))["body"]!["stackFrames"]![0]!;

                    var editedSecret = await dap.Ok("setExpression",
                        new { frameId = refreshed.Value<int>("id"), expression = "data.Secret", value = "\"replacement-secret\"" });

                    Assert.DoesNotContain("replacement-secret", editedSecret.ToString());
                    Assert.DoesNotContain("replacement-secret", JsonSerializer.Serialize(await ops.InspectAsync(id)));
                }

                result = await ops.ContinueAsync(id, "next-site");
            }

            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

            Assert.Equal("dap-edited-enteredjoined-enteredjoined",
                result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));

            var audits = (await ops.GetTraceAsync(id)).Where(e => e.Entry.EntryType == TraceEntryType.DataEdited).ToArray();
            Assert.Equal(2, audits.Length);

            Assert.All(audits, entry =>
            {
                var audit = Assert.IsType<DataEditedTrace>(entry.Entry.Payload);
                Assert.NotNull(audit.Target.FrameId);
                Assert.Equal("dap-editor", audit.Actor);
            });
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Fact]
    public async Task QualifiedFunctionBreakpointSelectsOnlyOneGraftSite()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var host = InlineDebuggerFixture.Host(store, new WorkflowCatalog(), null, s => s.AddBreakpointConditions());
        var id = await InlineDebuggerFixture.Start(host, false);
        var ops = host.GetRequiredService<IWorkflowOperations>();
        await using var dap = new Client(ops);
        await dap.Ok("initialize");
        await dap.Ok("attach", new { instanceId = id, sessionToken = Client.Token });
        await dap.Ok("configurationDone");

        var response = await dap.Ok("setFunctionBreakpoints",
            new { breakpoints = new[] { new { name = "second/nested/enter", condition = InlineDebuggerFixture.Condition } } });

        Assert.True(response["body"]!["breakpoints"]![0]!.Value<bool>("verified"));

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await host.GetRequiredService<IWorkflowClient>().ResumeAsync(id, new Signal())).Status);

        var inspection = (await ops.InspectAsync(id))!;
        Assert.Equal("second/nested/enter", Assert.Single(inspection.Threads).AuthorStepId);
        Assert.Single(inspection.Breakpoints);
        Assert.Empty(inspection.DebugState.Stop!.Diagnostics);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(id, "done")).Status);
    }

    // Production framed protocol, in memory, without sockets or spawned processes.
    private sealed class Client : IAsyncDisposable
    {
        internal const string Token = InlineDebugProtocolClient.Token;
        private readonly InlineDebugProtocolClient _protocol;

        internal Client(IWorkflowOperations operations)
        {
            var options = new DebuggingOptions();
            options.Tcp(IPAddress.Loopback, 0, Token, "dap-editor");
            options.Authorize = (_, _) => ValueTask.FromResult(true);
            var session = new DebugAdapterSession(operations, options);
            _protocol = new((input, output) => session.RunAsync(input, output), session);
        }

        internal async Task<JObject> Ok(string command, object? args = null) =>
            JObject.Parse((await _protocol.Ok(command, args)).GetRawText());

        public ValueTask DisposeAsync() => _protocol.DisposeAsync();
    }
}