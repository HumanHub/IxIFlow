using System.Text.Json;
using System.Net;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
[Trait("Spec", "G-04"), Trait("Spec", "G-06"), Trait("Spec", "G-08"), Trait("Spec", "G-10"), Trait("Spec", "G-11"), Trait("Spec", "H-08")]
public sealed class InlineDebuggerScenarios
{
    public static IEnumerable<object[]> Cases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(published => new object[] { profile, published }));

    [Theory, MemberData(nameof(Cases))]
    public async Task IntoEditRestartOutAndOverNestedInlineParallelJoin(string profile, bool published)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        var catalog = new WorkflowCatalog();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineDebugger", Guid.NewGuid().ToString("N"));
        if (published) await InlineDebuggerFixture.Publish(repository);
        string id, frameId, threadId;
        long revision;
        DataEditTarget target;
        DataEditResult receipt;
        var patches = new[] { new DataPatch(["Value"], JsonSerializer.SerializeToElement("edited")),
            new DataPatch(["Secret"], JsonSerializer.SerializeToElement("edited-secret")) };
        void Configure(IServiceCollection services) { services.AddBreakpointConditions(); database.ConfigureHost(services); }
        try
        {
            await using (var first = InlineDebuggerFixture.Host(repository, catalog, published ? Path.Combine(cache, "first") : null, Configure))
            {
                await database.StartHostLinksAsync(first);
                id = await InlineDebuggerFixture.Start(first, published);
                var operations = first.GetRequiredService<IWorkflowOperations>();
                await operations.SetBreakpointsAsync(id, [new("call", "first")], "configure");
                Assert.Equal(WorkflowExecutionStatus.Paused,
                    (await first.GetRequiredService<IWorkflowClient>().ResumeAsync(id, new Signal())).Status);
                await operations.SetBreakpointsAsync(id, [], "clear");
                var before = (await operations.InspectAsync(id))!;
                threadId = Assert.Single(before.Threads).ContinuationId;
                Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(id, threadId, DebugStepKind.Into, "into")).Status);
                var inside = (await operations.InspectAsync(id))!;
                Assert.Equal("first/enter", Assert.Single(inside.Threads).AuthorStepId);
                Assert.Equal("Step", inside.DebugState.Stop!.Reason);
                var frame = Assert.Single(inside.Threads[0].Frames, f => f.Kind == "InlineWorkflow");
                frameId = frame.ActivationId;
                Assert.Contains(nameof(DebugData), frame.DataType);
                var data = Assert.Single(inside.Data, d => d.FrameId == frameId);
                Assert.Equal(frame.DataType, data.DataType);
                Assert.Equal("initial", data.Value.GetProperty("Value").GetString());
                Assert.DoesNotContain(InlineDebuggerFixture.Secret, JsonSerializer.Serialize(inside));
                Assert.DoesNotContain("Secret", Assert.Single(inside.Data, d => d.FrameId == null).Value.GetRawText());
                revision = inside.Revision;
                target = new(inside.DebugState.PauseGeneration, threadId, frameId);
                receipt = await operations.EditDataAsync(id, "frame-edit", revision, target, patches, "frame-editor");
                Assert.Equal(revision + 1, receipt.Revision);
                var edited = (await operations.InspectAsync(id))!;
                Assert.Equal("initial", edited.Data.Single(d => d.FrameId == null).Value.GetProperty("Value").GetString());
                Assert.DoesNotContain("edited-secret", JsonSerializer.Serialize(edited));
                var wrongType = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.EditDataAsync(id, "caller-property",
                    receipt.Revision, target, [new(["Items"], JsonSerializer.SerializeToElement(new Dictionary<string, int>()))], "editor"));
                Assert.Equal(WorkflowErrorKind.InvalidRequest, wrongType.Kind);
                var invalid = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.EditDataAsync(id, "bad-frame",
                    receipt.Revision, target with { FrameId = "missing" }, patches, "editor"));
                Assert.Equal(WorkflowErrorKind.InvalidRequest, invalid.Kind);
                Assert.Equal(receipt.Revision, (await operations.InspectAsync(id))!.Revision);
                await first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            }
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
            repository = database.NewRepository();
            await using var second = InlineDebuggerFixture.Host(repository, catalog, published ? Path.Combine(cache, "second") : null, Configure);
            await database.StartHostLinksAsync(second);
            var ops = second.GetRequiredService<IWorkflowOperations>();
            var restored = (await ops.InspectAsync(id))!;
            Assert.Equal(WorkflowStatus.Paused, restored.Summary.Status);
            Assert.Equal(frameId, Assert.Single(restored.Threads[0].Frames, f => f.Kind == "InlineWorkflow").ActivationId);
            Assert.Equal("edited", restored.Data.Single(d => d.FrameId == frameId).Value.GetProperty("Value").GetString());
            if (published)
                Assert.Equal("inline/InlineDebugLeaf@1/workflow.yaml", (await ops.GetStepTableAsync(id)).Single(s => s.AuthorId == "first/enter").SourceLocation!.Split(':')[0]);
            Assert.Equal(receipt, await ops.EditDataAsync(id, "frame-edit", revision, target, patches, "frame-editor"));
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, threadId, DebugStepKind.Out, "out")).Status);
            var outside = (await ops.InspectAsync(id))!;
            Assert.Equal("second", Assert.Single(outside.Threads).AuthorStepId);
            Assert.DoesNotContain(outside.Data, d => d.FrameId == frameId);
            Assert.Equal("edited-enteredjoined", outside.Data.Single(d => d.FrameId == null).Value.GetProperty("Result").GetString());
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, threadId, DebugStepKind.Over, "over")).Status);
            var after = (await ops.InspectAsync(id))!;
            Assert.Equal("after", Assert.Single(after.Threads).AuthorStepId);
            Assert.DoesNotContain(after.Threads[0].Frames, f => f.Kind == "InlineWorkflow");
            var done = await ops.ContinueAsync(id, "done");
            Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
            Assert.Equal("edited-enteredjoined-enteredjoined", done.WorkflowData!.GetType().GetProperty("Value")!.GetValue(done.WorkflowData));
            Assert.All((await repository.GetDebugStatesAsync([id]))[id].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
            var trace = await ops.GetTraceAsync(id);
            Assert.Equal(3, trace.Count(t => t.Entry.EntryType == TraceEntryType.InlineEntered));
            Assert.Equal(3, trace.Count(t => t.Entry.EntryType == TraceEntryType.InlineExited));
            var audit = Assert.IsType<DataEditedTrace>(Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.DataEdited).Entry.Payload);
            Assert.Equal(target, audit.Target);
            Assert.Equal("frame-editor", audit.Actor);
            Assert.Equal(new[] { "Value", "Secret" }, audit.Paths.Select(p => Assert.Single(p)));
            Assert.DoesNotContain(InlineDebuggerFixture.Secret, JsonSerializer.Serialize(trace));
            using var checkpoint = JsonDocument.Parse((await repository.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
            Assert.Equal(1, checkpoint.RootElement.GetProperty("SchemaVersion").GetInt32());
            await second.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task ConditionsUseTheCalleeTypeAtEveryDirectAndNestedSite(string profile, bool published)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineConditions", Guid.NewGuid().ToString("N"));
        if (published) await InlineDebuggerFixture.Publish(repository);
        try
        {
            await using var host = InlineDebuggerFixture.Host(repository, new WorkflowCatalog(), published ? cache : null,
                services => { services.AddBreakpointConditions(); database.ConfigureHost(services); });
            await database.StartHostLinksAsync(host);
            var id = await InlineDebuggerFixture.Start(host, published);
            var ops = host.GetRequiredService<IWorkflowOperations>();
            var sites = (await ops.GetStepTableAsync(id)).Where(s => s.AuthorId.EndsWith("/enter", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, sites.Length);
            var location = Assert.Single(sites.Select(s => s.SourceLocation).Distinct())!;
            var colon = location.LastIndexOf(':');
            var path = location[..colon];
            var line = int.Parse(location[(colon + 1)..]);
            var options = new DebuggingOptions();
            options.Tcp(IPAddress.Loopback, 0, InlineDebugProtocolClient.Token, "source-operator");
            options.Authorize = (_, _) => ValueTask.FromResult(true);
            var session = new DebugAdapterSession(ops, options);
            await using var dap = new InlineDebugProtocolClient((input, output) => session.RunAsync(input, output), session);
            await dap.Ok("initialize");
            await dap.Ok("attach", new { instanceId = id, sessionToken = InlineDebugProtocolClient.Token });
            await dap.Ok("configurationDone");
            if (published)
            {
                Assert.Equal("inline/InlineDebugLeaf@1/workflow.yaml", path);
                var content = await dap.Ok("source", new { source = new { path } });
                Assert.Equal(await ops.GetSourceAsync(id, path), content.GetProperty("body").GetProperty("content").GetString());
            }
            var configured = await dap.Ok("setBreakpoints", new { source = new { path },
                breakpoints = new[] { new { line, condition = InlineDebuggerFixture.Condition } } });
            Assert.True(configured.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean());
            var result = await host.GetRequiredService<IWorkflowClient>().ResumeAsync(id, new Signal());
            foreach (var site in sites)
            {
                Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
                var paused = (await ops.InspectAsync(id))!;
                Assert.Equal(site.AuthorId, Assert.Single(paused.Threads).AuthorStepId);
                Assert.Equal(2, paused.Breakpoints.Count);
                Assert.Empty(paused.DebugState.Stop!.Diagnostics);
                Assert.All(paused.Data.Where(d => d.FrameId != null), data => Assert.Contains(nameof(DebugData), data.DataType));
                Assert.DoesNotContain(InlineDebuggerFixture.Secret, JsonSerializer.Serialize(paused));
                result = await ops.ContinueAsync(id, "next");
            }
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            await host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task InlineFrameEditWithLiveBranchesChangesTheirBaseline(string profile, bool published)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineBaseline", Guid.NewGuid().ToString("N"));
        if (published) await InlineDebuggerFixture.Publish(repository, baseline: true);
        try
        {
            await using var host = InlineDebuggerFixture.Host(repository, new WorkflowCatalog(), published ? cache : null, database.ConfigureHost);
            await database.StartHostLinksAsync(host);
            var id = await InlineDebuggerFixture.Start(host, published, baseline: true);
            var client = host.GetRequiredService<IWorkflowClient>();
            var ops = host.GetRequiredService<IWorkflowOperations>();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(id, new Signal())).Status);
            await ops.PauseAsync(id, "pause");
            var paused = (await ops.InspectAsync(id))!;
            var frameData = Assert.Single(paused.Data, d => d.FrameId != null);
            var target = new DataEditTarget(paused.DebugState.PauseGeneration, frameData.ContinuationId, frameData.FrameId);
            await ops.EditDataAsync(id, "baseline", paused.Revision, target,
                [new(["Value"], JsonSerializer.SerializeToElement("baseline")), new(["Result"], JsonSerializer.SerializeToElement("edited-baseline"))], "editor");
            var edited = (await ops.InspectAsync(id))!;
            var branches = edited.Threads.Where(t => t.ParentJoinId != null).ToArray();
            Assert.Equal(2, branches.Length);
            Assert.All(branches, branch => Assert.Equal("edited-baseline", edited.Data.Single(d => d.ContinuationId == branch.ContinuationId && d.FrameId == null).Value.GetProperty("Result").GetString()));
            Assert.Contains(branches, branch => edited.Data.Single(d => d.ContinuationId == branch.ContinuationId && d.FrameId == null).Value.GetProperty("Value").GetString() == "branch");
            Assert.Contains(branches, branch => edited.Data.Single(d => d.ContinuationId == branch.ContinuationId && d.FrameId == null).Value.GetProperty("Value").GetString() == "baseline");
            await ops.ContinueAsync(id, "continue");
            await client.ResumeAsync(id, "left", new Signal(), "left-event");
            var done = await client.ResumeAsync(id, "right", new Signal(), "right-event");
            Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
            Assert.Equal("branch", done.WorkflowData!.GetType().GetProperty("Value")!.GetValue(done.WorkflowData));
            Assert.Equal("edited-baseline", done.WorkflowData.GetType().GetProperty("Result")!.GetValue(done.WorkflowData));
            await host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
