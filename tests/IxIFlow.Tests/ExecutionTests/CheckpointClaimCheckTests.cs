using System.Data.Common;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "H-06")]
public sealed class CheckpointClaimCheckTests
{
    private static readonly string Large = new('x', 300_000);

    [Fact]
    [Trait("Spec", "J-04")]
    public async Task LargeDocumentIsStoredOnceAndSmallCommitsReuseIt()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var workflow = Workflow.Create<Data>("large-document")
            .Step<Noop>().Step<Noop>().Step<Noop>().Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(workflow, new Data { Text = Large });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(Large, ((Data)result.WorkflowData!).Text);
        var data = repository.Commits[0].Parts.Single(p => p.Key == "d").Json;
        Assert.NotNull(data.Reference);
        Assert.Equal(1, probe.Puts[data.Reference.Digest]);
        Assert.Equal(JsonSerializer.Serialize(new Data { Text = Large }), Encoding.UTF8.GetString((await probe.GetAsync(data.Reference))!.Value.Span));
        Assert.All(repository.Commits.Skip(1), commit => Assert.DoesNotContain(commit.Parts, p => p.Key == "d"));
        Assert.All(repository.Commits, commit => Assert.True(Encoding.UTF8.GetByteCount(commit.Payload) < 10_000));
        Assert.Equal(8, repository.Commits.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptDataStopsWithoutChangingPartsAndReleaseContinues(bool corrupt)
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("unreadable-data").Step<Noop>().WaitFor<Go>("go").Step<Noop>().Build();
        var first = await client.RunAsync(workflow, new Data { Text = Large });
        var before = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        probe.Broken = before.WorkflowDataJson.Reference;
        probe.Corrupt = corrupt;
        // An event stays pending when hydration fails, rather than being rejected by event intake.
        var stopped = await client.ResumeAsync(first.InstanceId, "go", new Go(), "go-1");
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, stopped.ErrorKind);
        var after = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        Assert.Equal(before.SavedParts, after.SavedParts);
        Assert.Null(after.RunnableAtUtc);
        var stop = repository.Commits.Last();
        Assert.True(stop.PreservesPayloadPins);
        Assert.Empty(stop.Parts);
        Assert.False(stop.ReplacesParts);
        Assert.Equal(EventOutcome.Pending, (await repository.GetEventAsync(first.InstanceId, "go-1"))!.Outcome);
        Assert.False(await repository.PayloadStore.CollectAsync(before.WorkflowDataJson.Reference!, TimeSpan.Zero));
        probe.Broken = null;
        var released = await client.ReleaseAsync(first.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(Large, ((Data)released.WorkflowData!).Text);
    }

    [Fact]
    public async Task TerminalResultReadFailureDoesNotReopenTheInstance()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Workflow.Create<Data>("terminal-payload").Step<Noop>().Build(), new Data { Text = Large });
        var saved = (await repository.GetWorkflowInstanceAsync(result.InstanceId))!;
        var count = repository.Commits.Count;
        probe.Broken = saved.WorkflowDataJson.Reference;
        var failure = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RecoverAsync(result.InstanceId));
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, failure.Kind);
        Assert.Equal(WorkflowStatus.Completed, (await repository.GetWorkflowInstanceAsync(result.InstanceId))!.Status);
        Assert.Equal(count, repository.Commits.Count);
    }

    [Fact]
    public async Task AWorkflowThrownPayloadErrorRemainsAWorkflowFailure()
    {
        var repository = new RecordingRepository();
        using var services = Services(repository, repository.PayloadStore);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("workflow-payload-error").Step<ThrowPayloadError>().Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.DoesNotContain(repository.Commits, c => c.PreservesPayloadPins);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepublishingOriginalBytesRepairsMissingOrCorruptBodiesWithPinsKept(bool corrupt)
    {
        var repository = new RecordingRepository();
        using var services = Services(repository, repository.PayloadStore);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("repair-body").Step<Noop>().WaitFor<Go>("go").Build();
        var first = await client.RunAsync(workflow, new Data { Text = Large });
        var saved = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        var reference = saved.WorkflowDataJson.Reference!;
        var original = (await repository.PayloadStore.GetAsync(reference))!.Value.ToArray();
        var rows = (Dictionary<string, PayloadRecord>)repository.InnerPayloadStore.GetType()
            .GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(repository.InnerPayloadStore)!;
        rows[reference.Digest].Body = corrupt ? new byte[original.Length] : null;
        var stopped = await client.ResumeAsync(first.InstanceId, "go", new Go(), "repair-event");
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, stopped.ErrorKind);
        Assert.False(await repository.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
        Assert.Equal(reference, await repository.PayloadStore.PutAsync(original));
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReleaseAsync(first.InstanceId)).Status);
    }

    [Fact]
    public async Task InputCopiesPreviousAndBranchChangesAreClaimChecked()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe, options => options.PayloadThresholdBytes = 64);
        var workflow = Workflow.Create<Data>("branch-payloads")
            .Step<Echo>(step => step.Input(a => a.Text).From(c => c.WorkflowData.Text))
            .Parallel(p => p.Do(b => b.Step<Echo>(step => step.Input(a => a.Text).From(c => c.PreviousStep.Text + "branch")
                    .Output(a => a.Text).To(c => c.WorkflowData.Text)).WaitFor<Go>("go"))
                .Do(b => b.Step<Noop>())).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(workflow, new Data { Text = Large });
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Contains(repository.Commits, c => c.Payload.Contains("\"reference\"", StringComparison.Ordinal));
        var saved = (await repository.GetWorkflowInstanceAsync(result.InstanceId))!;
        var reader = new PayloadReader(async reference => (await probe.GetAsync(reference))!.Value, new(20_000_000));
        var state = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, reader));
        Assert.NotNull(state.Continuations.Single(c => c.ParentJoinId == null).Previous?.Json.Reference);
        Assert.Contains(state.Continuations, c => c.DataChanges?.Any(change => change.Value?.Reference != null) == true);
        Assert.Contains(repository.Commits.SelectMany(c => c.Parts), p => p.Key.StartsWith("c/") &&
            p.Json.Reference != null && p.Json.Dependencies.Count > 0);
        Assert.Contains(repository.Commits.SelectMany(c => c.Parts).Where(p => p.Key.StartsWith("c/")),
            part => part.Json.Reference != null || part.Json.InlineText.Contains("\"reference\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachItemsAndResultsRestoreByReference(bool parallel)
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("foreach-payloads").Step<Noop>()
            .ForEach(c => c.WorkflowData.Items, (body, item) => body
                .Step<Echo>(s => s.Input(a => a.Text).From(c => c.Item(item)))
                .WaitFor<Go>("item").Result(c => c.Item(item)), s =>
                { if (parallel) s.Parallel(maxConcurrency: 1); s.Into(c => c.WorkflowData.Results); })
            .Build();
        var first = await client.RunAsync(workflow, new Data { Items = [Large, Large + "2"] });
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        var second = await client.ResumeAsync(first.InstanceId, "item", new Go(), "first");
        Assert.Equal(WorkflowExecutionStatus.Suspended, second.Status);
        var complete = await client.ResumeAsync(first.InstanceId, "item", new Go(), "second");
        Assert.Equal(WorkflowExecutionStatus.Success, complete.Status);
        Assert.Equal(new[] { Large, Large + "2" }, ((Data)complete.WorkflowData!).Results.Select(r => r.Value));
        Assert.Contains(repository.Commits.SelectMany(c => c.Parts), p => p.Key.StartsWith("r/") && p.Json.Reference != null);
        Assert.Contains(repository.Commits.SelectMany(c => c.Parts), p => p.Key.StartsWith("f/") &&
            (p.Json.Reference != null || p.Json.InlineText.Contains("\"reference\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WaitDefaultIsReferencedAndDiscoveryHydratesIt()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("default-payload").Step<Noop>()
            .WaitFor<Data>("data", configure: w => w.DefaultPayload(new Data { Text = Large })).Build();
        var result = await client.RunAsync(workflow, new Data());
        var stored = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
        Assert.NotNull(stored.DefaultPayloadJson!.Reference);
        var discovered = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
        Assert.Equal(Large, JsonSerializer.Deserialize<Data>(discovered.DefaultPayloadJson!)!.Text);
    }

    [Fact]
    public async Task PayloadStopKeepsForEachResultCountsAndCanResumeTheLastItem()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("foreach-payload-stop").Step<Noop>()
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Noop>()
                .WaitFor<Go>("item").Result(c => c.Item(item)), s => s.Into(c => c.WorkflowData.Results)).Build();
        var first = await client.RunAsync(workflow, new Data { Items = [Large, Large + "2"] });
        await client.ResumeAsync(first.InstanceId, "item", new Go(), "first");
        var saved = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        probe.Broken = saved.WorkflowDataJson.Reference;
        var stopped = await client.ResumeAsync(first.InstanceId, "item", new Go(), "second");
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, stopped.ErrorKind);
        Assert.Contains("\"ResultCount\":1", repository.Commits.Last().Payload, StringComparison.Ordinal);
        probe.Broken = null;
        var completed = await client.ReleaseAsync(first.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(new[] { Large, Large + "2" }, ((Data)completed.WorkflowData!).Results.Select(r => r.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundAndAcceptedRepliesAndInboxEventsAreReferenced(bool early)
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var flow = Workflow.Create<Data>("reply-payloads").Step<Noop>().Request<LargeRequest>("answer");
        var workflow = early
            ? flow.WaitFor<Go>("gate").WaitFor<LargeReply>("answer", configure: w => w.Output(r => r.Text).To(c => c.WorkflowData.Text)).Build()
            : flow.WaitFor<LargeReply>("answer", configure: w => w.Output(r => r.Text).To(c => c.WorkflowData.Text)).Build();
        var started = await client.RunAsync(workflow, new Data());
        var reply = await client.ReplyAsync(services.GetRequiredService<TokenBox>().Token, new LargeReply { Text = Large });
        Assert.NotNull((await repository.GetEventAsync(started.InstanceId, reply.Event!.EventId))!.PayloadJson.Reference);
        if (early)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, reply.Status);
            var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
            Assert.NotNull(Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).BoundReplies).Event.Json.Reference);
            reply = await client.ResumeAsync(started.InstanceId, "gate", new Go());
        }
        else
            Assert.Contains(repository.Commits, c => ExecutionCheckpoint.ReadTrunk(c.ReadTrunk().ExecutionStateJson)
                .Continuations.Any(branch => branch.AcceptedWait?.Event.Json.Reference != null));
        Assert.Equal(WorkflowExecutionStatus.Success, reply.Status);
        Assert.Equal(Large, ((Data)reply.WorkflowData!).Text);
    }

    [Fact]
    public async Task UnreadableBranchPartCanBeStoppedWithoutAssembly()
    {
        var repository = new RecordingRepository();
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe, options => options.PayloadThresholdBytes = 64);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("unreadable-branch").Step<Noop>()
            .Parallel(p => p.Do(b => b.WaitFor<Go>("go")).Do(b => b.Step<Noop>())).Build();
        var first = await client.RunAsync(workflow, new Data());
        var saved = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        probe.Broken = saved.SavedParts!.First(p => p.Key.StartsWith("c/")).Value.Reference;
        Assert.NotNull(probe.Broken);
        var stopped = await client.ResumeAsync(first.InstanceId, "go", new Go(), "branch-event");
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, stopped.ErrorKind);
        Assert.True(repository.Commits.Last().PreservesPayloadPins);
        probe.Broken = null;
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReleaseAsync(first.InstanceId)).Status);
    }

    [Fact]
    public async Task PerDigestTransactionsDoNotBlockUnrelatedPins()
    {
        var store = new InMemoryWorkflowStateRepository().PayloadStore;
        var first = await store.PutAsync("1"u8.ToArray());
        var second = await store.PutAsync("2"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = store.InTransactionAsync(async (pins, ct) =>
        {
            await pins.PinAsync(new("checkpoint", "first"), first, ct);
            entered.SetResult();
            await release.Task;
        }).AsTask();
        await entered.Task;
        try
        {
            await store.InTransactionAsync(async (pins, ct) => await pins.PinAsync(new("checkpoint", "second"), second, ct))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await store.CollectAsync(second, TimeSpan.Zero));
        }
        finally { release.SetResult(); await held; }
        Assert.False(await store.CollectAsync(first, TimeSpan.Zero));
    }

    [Fact]
    public Task MemoryCommitsWithDifferentDigestsDoNotWaitForEachOther() =>
        DifferentDigestCommits(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerCommitsWithDifferentDigestsDoNotWaitForEachOther() =>
        DifferentDigestCommits(new SqlWorkflowStateRepository(SqlTestDatabase<CheckpointClaimCheckTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCommitsWithDifferentDigestsDoNotWaitForEachOther() =>
        DifferentDigestCommits(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CheckpointClaimCheckTests>.ConnectionString));

    private static async Task DifferentDigestCommits(IWorkflowStateRepository repository)
    {
        var payloads = repository.PayloadStore;
        var a = await payloads.PutAsync("\"first\""u8.ToArray());
        var b = await payloads.PutAsync("\"second\""u8.ToArray());
        var first = await Commit("digest-a-" + Guid.NewGuid().ToString("N"), a);
        var second = await Commit("digest-b-" + Guid.NewGuid().ToString("N"), b);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = payloads.InTransactionAsync(async (pins, ct) =>
        { await pins.PinAsync(new("reader", "held-first"), a, ct); entered.SetResult(); await release.Task; }).AsTask();
        await entered.Task;
        try
        {
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(second).WaitAsync(TimeSpan.FromSeconds(3))).Status);
        }
        finally { release.SetResult(); await holding; }
        // The original digest can still be pinned safely after the held transaction ends.
        WorkflowCommitResult result;
        do { result = await repository.CommitWorkflowInstanceAsync(first); } while (result.Status == WorkflowCommitStatus.Retry);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        Assert.False(await payloads.CollectAsync(a, TimeSpan.Zero));

        async Task<CheckpointCommit> Commit(string id, PayloadReference reference)
        {
            var lease = await StoreTestSupport.LeaseAsync(repository, id);
            return CheckpointCommit.Create(new WorkflowInstance { InstanceId = id, Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.FromReference(reference), ExecutionStateJson = StoreTestSupport.CheckpointJson }, lease.Epoch, 0);
        }
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqlitePersistsReferencesAndPinsInTheCheckpointTransaction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlowClaimCheck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var registrations = new ServiceCollection().AddLogging();
            registrations.AddIxIFlow(options => options.PayloadThresholdBytes = 256 * 1024).UseSqlite("Data Source=" + Path.Combine(folder, "claim.db"));
            await using var services = registrations.BuildServiceProvider();
            var client = services.GetRequiredService<IWorkflowClient>();
            var result = await client.RunAsync(Workflow.Create<Data>("sqlite-claim").Step<Noop>().Build(), new Data { Text = Large });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(Large, ((Data)result.WorkflowData!).Text);
            var repository = services.GetRequiredService<IWorkflowStateRepository>();
            var saved = (await repository.GetWorkflowInstanceAsync(result.InstanceId))!;
            Assert.NotNull(saved.WorkflowDataJson.Reference);
            Assert.False(await repository.PayloadStore.CollectAsync(saved.WorkflowDataJson.Reference, TimeSpan.Zero));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task EarlierCheckpointColumnEncodingRequiresStoreRecreation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlowClaimSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "schema.db") + ";Pooling=False";
        try
        {
            await using (var repository = new SqliteWorkflowStateRepository(connectionString))
                await repository.GetWorkflowInstanceSummaryAsync("absent");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP INDEX ixiflow_payload_refs_owner";
                await command.ExecuteNonQueryAsync();
            }
            await using var earlier = new SqliteWorkflowStateRepository(connectionString);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => earlier.GetWorkflowInstanceSummaryAsync("absent"));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("must be recreated", error.Message);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public async Task LostAcknowledgementReplaysTheBodyAndPinsTheCheckpointOnce()
    {
        var repository = new RecordingRepository { LoseLargeAcknowledgement = true };
        var probe = new ProbeStore(repository.PayloadStore);
        using var services = Services(repository, probe);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("payload-lost-ack").Step<Noop>().Build(), new Data { Text = Large });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = repository.Commits[0].Parts.Single(p => p.Key == "d").Json.Reference!;
        Assert.Equal(1, probe.Puts[data.Digest]);
        var rows = (Dictionary<string, PayloadRecord>)repository.InnerPayloadStore.GetType()
            .GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(repository.InnerPayloadStore)!;
        Assert.Single(rows[data.Digest].Owners.Where(o => o.Kind == "checkpoint"));
    }

    [Fact]
    public void ImmutableValuesReuseTheirEncodedBytesDigestAndDependencyMetadata()
    {
        var payloads = new CheckpointPayloads(ExecutionLimits.Default with { PayloadThresholdBytes = 256 * 1024 }, new(10_000_000));
        var value = StoredJson.Inline(JsonSerializer.Serialize(Large));
        var first = payloads.Store(value);
        var encoded = value.Encoded;
        Assert.Same(value.Identity, first.Reference);
        payloads.Acknowledged();
        var second = payloads.Store(value);
        Assert.Same(first.Reference, second.Reference);
        Assert.Equal(encoded, value.Encoded);
        Assert.Empty(payloads.Pending);
    }

    [Fact]
    public async Task ReplacingCheckpointPinsNeverReleasesAReaderOrAnotherCheckpoint()
    {
        var store = new InMemoryWorkflowStateRepository().PayloadStore;
        var first = await store.PutAsync("1"u8.ToArray());
        var second = await store.PutAsync("2"u8.ToArray());
        var checkpoint = new PayloadOwner("checkpoint", "changing");
        var other = new PayloadOwner("checkpoint", "other");
        var reader = new PayloadOwner("reader", "hydrating");
        await store.InTransactionAsync(async (pins, ct) =>
        {
            await pins.ReplaceAsync(checkpoint, [first], ct);
            await pins.PinAsync(reader, first, ct);
            await pins.PinAsync(other, first, ct);
        });
        await store.InTransactionAsync(async (pins, ct) => await pins.ReplaceAsync(checkpoint, [second], ct));
        Assert.False(await store.CollectAsync(first, TimeSpan.Zero));
        await store.InTransactionAsync(async (pins, ct) => await pins.ReleaseAsync(reader, ct));
        Assert.False(await store.CollectAsync(first, TimeSpan.Zero));
        await store.InTransactionAsync(async (pins, ct) => await pins.ReleaseAsync(other, ct));
        Assert.True(await store.CollectAsync(first, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(second, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeReadUsesATransactionSnapshotAndAllowsCollectionAfterReply(bool sqlite)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlowReadSnapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var database = sqlite
                ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "read.db")) : null;
            var store = database?.PayloadStore ?? new InMemoryWorkflowStateRepository().PayloadStore;
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Large));
            var reference = await store.PutAsync(body);
            var probe = new ProbeStore(store);
            var assignmentId = Guid.NewGuid();
            var reply = await new ExecutionPayloadTransfer(probe, ExecutionLimits.Default)
                .ReadAsync(new(assignmentId, "snapshot", reference));
            Assert.Equal(assignmentId, reply.AssignmentId);
            Assert.Null(reply.Refusal);
            // Get copies verified bytes under its transaction's digest lock. No durable reader
            // owner extends protection past that transaction, so this unowned body is reclaimable.
            Assert.Equal(0, probe.ReaderPins);
            Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
            Assert.Null(await store.GetAsync(reference));
            Assert.Equal(body, reply.Body);
            reference.Verify(reply.Body!);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public async Task AggregateHydrationLimitAndPutLimitAreEnforced()
    {
        var store = new InMemoryWorkflowStateRepository(TimeProvider.System, payloadSettings: new(64)).PayloadStore;
        var failure = Assert.Throws<WorkflowEngineException>(() => store.PutAsync(new byte[65]));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, failure.Kind);
        var budget = new PayloadBudget(10);
        var reader = new PayloadReader(r => Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("\"1234\"")), budget);
        var value = StoredJson.Inline("\"1234\"");
        await reader.ReadAsync(StoredJson.FromReference(value.Identity));
        var other = StoredJson.Inline("\"5678\"");
        await Assert.ThrowsAsync<PayloadHydrationLimitException>(() => reader.ReadAsync(StoredJson.FromReference(other.Identity)));
    }

    private static ServiceProvider Services(RecordingRepository repository, IPayloadStore payloads, Action<IxIFlowOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        repository.Probe = payloads;
        services.AddIxIFlow(options =>
        {
            options.PayloadThresholdBytes = 256 * 1024;
            configure?.Invoke(options);
        });
        services.AddSingleton<IWorkflowStateRepository>(repository);
        services.AddTransient<Noop>(); services.AddTransient<Echo>();
        services.AddSingleton<TokenBox>();
        return services.BuildServiceProvider();
    }
    private sealed class RecordingRepository : InMemoryWorkflowStateRepository
    {
        public IPayloadStore? Probe { get; set; }
        public override IPayloadStore PayloadStore => Probe ?? base.PayloadStore;
        public IPayloadStore InnerPayloadStore => base.PayloadStore;
        public List<CheckpointCommit> Commits { get; } = [];
        public bool LoseLargeAcknowledgement { get; set; }
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied)
            {
                Commits.Add(commit);
                if (LoseLargeAcknowledgement && commit.PayloadReferences.Count > 0)
                { LoseLargeAcknowledgement = false; return result with { Status = WorkflowCommitStatus.Retry }; }
            }
            return result;
        }
    }
    private sealed class ProbeStore(IPayloadStore inner) : IPayloadStore
    {
        public Dictionary<string, int> Puts { get; } = [];
        public PayloadReference? Broken { get; set; }
        public bool Corrupt { get; set; }
        public int ReaderPins { get; private set; }
        public async ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default)
        { var r = await inner.PutAsync(body, dependencies, ct); Puts[r.Digest] = Puts.GetValueOrDefault(r.Digest) + 1; return r; }
        public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference r, CancellationToken ct = default)
        {
            return r == Broken ? Corrupt ? new byte[(int)r.Bytes] : null : await inner.GetAsync(r, ct);
        }
        public ValueTask<bool> CollectAsync(PayloadReference r, TimeSpan grace, CancellationToken ct = default) => inner.CollectAsync(r, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) => inner.InTransactionAsync((tx, token) => action(new ProbeTransaction(this, tx), token), ct);
        public IPayloadTransaction Enlist(DbTransaction transaction) => new ProbeTransaction(this, inner.Enlist(transaction));
        private sealed class ProbeTransaction(ProbeStore probe, IPayloadTransaction inner) : IPayloadTransaction
        {
            public DbTransaction? DatabaseTransaction => inner.DatabaseTransaction;
            public async ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default)
            {
                var reference = await inner.PutAsync(body, dependencies, ct);
                probe.Puts[reference.Digest] = probe.Puts.GetValueOrDefault(reference.Digest) + 1;
                return reference;
            }
            public ValueTask LockAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default) => inner.LockAsync(owner, references, ct);
            public ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default)
            { if (owner.Kind == "reader") probe.ReaderPins++; return inner.PinAsync(owner, reference, ct); }
            public ValueTask ReplaceAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default) => inner.ReplaceAsync(owner, references, ct);
            public ValueTask ReleaseAsync(PayloadOwner owner, CancellationToken ct = default) => inner.ReleaseAsync(owner, ct);
        }
    }
    public sealed class Data
    {
        public string Text { get; set; } = "";
        public List<string> Items { get; set; } = [];
        public List<ItemResult<string>> Results { get; set; } = [];
    }
    public sealed class Go;
    public sealed class LargeReply { public string Text { get; set; } = ""; }
    public sealed class TokenBox { public string Token { get; set; } = ""; }
    public sealed class LargeRequest(TokenBox box) : IRequestActivity<LargeReply>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { box.Token = context.Reply!.Token; return Task.CompletedTask; }
    }
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class ThrowPayloadError : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new WorkflowEngineException(WorkflowErrorKind.PayloadUnavailable, "raised by workflow code");
    }
    public sealed class Echo : IAsyncActivity
    { public string Text { get; set; } = ""; public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
