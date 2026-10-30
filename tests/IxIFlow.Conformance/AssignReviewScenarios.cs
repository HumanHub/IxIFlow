using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignReviewScenarios
{
    public static IEnumerable<object[]> Boundaries => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "wait", "pause", "join" }.SelectMany(boundary =>
            new[] { "before", "lost-ack" }.SelectMany(fault =>
                new[] { "fluent", "yaml" }.Select(authoring => new object[] { profile, boundary, fault, authoring }))));

    [Theory, MemberData(nameof(Boundaries))]
    public async Task AssignImmediatelyBeforeWaitPauseAndJoinRecoversAtTheSelectedCommit(
        string profile, string boundary, string fault, string authoring)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();
        var fluent = Definition(boundary);
        var yaml = Yaml(boundary);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        var definition = authoring == "fluent" ? fluent : yaml;
        var writes = Instrument(definition);
        await catalog.RegisterWorkflowAsync(definition);
        var armed = false;

        bool Target(CheckpointCommit commit)
        {
            if (!armed || writes() == 0)
                return false;

            var checkpoint = ExecutionCheckpoint.ReadTrunk(commit.ReadTrunk().ExecutionStateJson);

            return boundary switch
            {
                "pause" => commit.Instance.Status == WorkflowStatus.Paused && checkpoint.Debug.Stop?.StepId == "root/2",
                "wait" => commit.OpenedWaits.Any(wait => wait.Key == "saved"),
                _ => checkpoint.Joins.Count == 0 && commit.OpenedWaits.Any(wait => wait.Key == "joined") &&
                    commit.TraceEntries.Any(entry =>
                        JsonSerializer.Deserialize<ExecutionTraceEntry>(entry)!.EntryType == TraceEntryType.JoinCompleted)
            };
        }

        var decorated = CommitInterruption.Wrap(repository, Target, fault == "lost-ack");
        string id;

        await using (var first = Services(store, decorated, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            Func<Task<WorkflowExecutionResult>> crossBoundary;

            if (boundary == "pause")
            {
                var operations = first.GetRequiredService<IWorkflowOperations>();
                await operations.SetBreakpointsAsync(id, [new("before-write", "assign")], "test");
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RecoverAsync(id)).Status);
                var before = await client.ResumeAsync(id, "start", new Go());
                Assert.Equal(WorkflowExecutionStatus.Paused, before.Status);
                Assert.Equal(0, Assert.IsType<Data>(before.WorkflowData).Count);
                await operations.SetBreakpointsAsync(id, [], "test");
                Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(id)).Status);
                var thread = Assert.Single((await operations.InspectAsync(id))!.Threads);
                crossBoundary = () => operations.StepAsync(id, thread.ContinuationId, DebugStepKind.Into, "test");
            }
            else
                crossBoundary = () => client.ResumeAsync(id, "start", new Go());

            armed = true;

            if (boundary == "pause" && fault == "lost-ack")
            {
                // A debug command can reconcile its saved acknowledgement after the host disappears.
                // Either receipt outcome must leave the same committed pause and assigned value.
                try
                {
                    var reconciled = await crossBoundary();
                    Assert.Equal(WorkflowExecutionStatus.Paused, reconciled.Status);
                    Assert.Equal(1, Assert.IsType<Data>(reconciled.WorkflowData).Count);
                }
                catch (WorkflowEngineException error)
                {
                    Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
                }
            }
            else
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(crossBoundary);
                Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
            }

            Assert.True(((CommitInterruption)decorated).Injected, $"The {boundary} commit was not injected");
        }

        var firstWrites = boundary == "join" ? 2 : 1;
        var firstAttempts = writes();
        Assert.Equal(firstWrites, firstAttempts);
        repository = await Restart(store, repository);

        await using (var second = Services(store, repository, catalog))
        {
            var client = second.GetRequiredService<IWorkflowClient>();

            if (fault == "before")
            {
                var recovered = await client.RecoverAsync(id);

                Assert.True(recovered.Status == (boundary == "pause" ? WorkflowExecutionStatus.Paused : WorkflowExecutionStatus.Suspended),
                    recovered.ErrorMessage + "\n" + recovered.ErrorStackTrace);
            }

            var saved = (await repository.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(boundary == "pause" ? WorkflowStatus.Paused : WorkflowStatus.Suspended, saved.Status);
            Assert.Equal(boundary == "join" ? 3 : 1, (await ReadData(repository, saved)).Count);
            Assert.Equal(firstAttempts + (fault == "before" ? firstWrites : 0), writes());
        }

        // Reopen again to verify that recovery itself persisted the data and the advanced cursor.
        repository = await Restart(store, repository);
        await using var third = Services(store, repository, catalog);

        var result = boundary == "pause"
            ? await third.GetRequiredService<IWorkflowOperations>().ContinueAsync(id, "test")
            : await third.GetRequiredService<IWorkflowClient>().ResumeAsync(id, boundary == "join" ? "joined" : "saved", new Go());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(boundary == "join" ? 3 : 1, Assert.IsType<Data>(result.WorkflowData).Count);

        if (boundary == "pause")
            Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Copy);

        var trace = await third.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        Assert.Equal(boundary is "join" or "pause" ? 2 : 1, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned));

        if (boundary == "join")
            Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.JoinCompleted);
    }

    public static IEnumerable<object[]> ParallelItems => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "before", "lost-ack" }.SelectMany(fault =>
            new[] { "fluent", "yaml" }.Select(authoring => new object[] { profile, fault, authoring })));

    [Theory, MemberData(nameof(ParallelItems))]
    public async Task AssignParallelForEachMergesEveryItemsChangesAcrossRecovery(string profile, string fault, string authoring)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();

        var fluent = new WorkflowBuilder<Data, object>([], "AssignReviewEach")
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (body, item) => body
                    .Assign(ctx => ctx.WorkflowData.Total, ctx => ctx.WorkflowData.Total + ctx.Item(item))
                    .WaitFor<Go>("item")
                    .Assign(ctx => ctx.WorkflowData.Seen, ctx => ctx.WorkflowData.Seen + ctx.Index(item) + 1),
                setup: setup => setup.Parallel().Combine<int, Sum<int>>(data => data.Total).Combine<int, Sum<int>>(data => data.Seen))
            .WaitFor<Go>("joined").Build();

        var yaml = IxIFlow.Conformance.Generated.Workflow_assign_review_each_workflow.Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        var definition = authoring == "fluent" ? fluent : yaml;
        var writes = Instrument(definition);
        await catalog.RegisterWorkflowAsync(definition);

        var decorated = CommitInterruption.Wrap(repository,
            commit => writes() == 4 && commit.OpenedWaits.Count(wait => wait.Key == "item") == 4, fault == "lost-ack");

        var id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, decorated, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                client.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }));

            Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
            Assert.True(((CommitInterruption)decorated).Injected, "The four active item waits were not injected");
        }

        repository = await Restart(store, repository);

        await using (var second = Services(store, repository, catalog))
        {
            var client = second.GetRequiredService<IWorkflowClient>();

            if (fault == "before")
            {
                var recovered = await client.RecoverAsync(id);

                Assert.True(recovered.Status == WorkflowExecutionStatus.Suspended,
                    recovered.ErrorMessage + "\n" + recovered.ErrorStackTrace);
            }

            Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(id))!.Status);
            var checkpoint = await ReadState(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            Assert.Equal(4, checkpoint.Waits.Count(wait => wait.Key == "item"));
            Assert.Single(checkpoint.Joins);
            Assert.Equal(fault == "before" ? 8 : 4, writes());
        }

        // Finish one durable item event per run, reopening SQLite and the host between every run.
        for (var item = 0; item < 4; item++)
        {
            repository = await Restart(store, repository);
            await using var host = Services(store, repository, catalog);
            var checkpoint = await ReadState(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            Assert.Equal(4 - item, checkpoint.Waits.Count(wait => wait.Key == "item"));
            var wait = checkpoint.Waits.First(wait => wait.Key == "item");

            await repository.AppendEventAsync(new(id, Guid.NewGuid().ToString("N"), WorkflowTypeIdentity.SavedName(typeof(Go)),
                JsonSerializer.Serialize(new Go()), Key: "item", TargetWaitId: wait.Id));

            Assert.Equal(WorkflowExecutionStatus.Suspended,
                (await host.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        }

        repository = await Restart(store, repository);
        await using var final = Services(store, repository, catalog);
        var joined = await ReadState(repository, (await repository.GetWorkflowInstanceAsync(id))!);
        Assert.Empty(joined.Joins);
        Assert.Equal("joined", Assert.Single(joined.Waits).Key);
        var result = await final.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "joined", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(27, data.Total);
        Assert.Equal(30, data.Seen);
        Assert.Equal(new[] { 2, 3, 5, 7 }, data.Items);
        Assert.Equal(fault == "before" ? 12 : 8, writes());
        var trace = await final.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        Assert.Equal(8, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned));
        Assert.Equal(4, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.ForEachItemEnded));
    }

    private static Func<int> Instrument(WorkflowDefinition definition)
    {
        var writes = 0;

        foreach (var step in ParallelRules.Flatten(definition.Steps).Where(step => step.Assignment != null))
        {
            var apply = step.Assignment!.Apply!;

            step.Assignment.Apply = context =>
            {
                apply(context);
                Interlocked.Increment(ref writes);
            };
        }

        return () => Volatile.Read(ref writes);
    }

    private static WorkflowDefinition Definition(string boundary)
    {
        var builder = new WorkflowBuilder<Data, object>([], "AssignReview" + boundary).WaitFor<Go>("start");

        if (boundary == "join")
            return builder.Parallel(parallel => parallel.Combine<int, Sum<int>>(data => data.Count)
                .Do(body => body.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1))
                .Do(body => body.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 2))).WaitFor<Go>("joined").Build();

        var assigned = builder.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign");

        return boundary == "pause"
            ? assigned.Assign(ctx => ctx.WorkflowData.Copy, ctx => ctx.WorkflowData.Count).WithId("after").Build()
            : assigned.WaitFor<Go>("saved").Build();
    }

    private static WorkflowDefinition Yaml(string boundary) => boundary switch
    {
        "wait" => IxIFlow.Conformance.Generated.Workflow_assign_review_wait_workflow.Build(),
        "pause" => IxIFlow.Conformance.Generated.Workflow_assign_review_pause_workflow.Build(),
        _ => IxIFlow.Conformance.Generated.Workflow_assign_review_join_workflow.Build()
    };

    private static async Task<ExecutionCheckpoint> ReadState(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(
            async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"),
            new PayloadBudget(100_000_000));

        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }

    private static async Task<Data> ReadData(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(
            async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"),
            new PayloadBudget(100_000_000));

        return JsonSerializer.Deserialize<Data>(await reader.ReadAsync(instance.WorkflowDataJson))!;
    }

    private static async Task<IWorkflowStateRepository> Restart(ProfileStore store, IWorkflowStateRepository repository)
    {
        if (store.Profile == StoreProfiles.LocalSqlite && repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        return store.NewRepository();
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(options =>
        {
            options.Debugging = true;
            options.PayloadThresholdBytes = 1;
        }).Services;

        services.AddSingleton(repository).AddSingleton(catalog);
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

        return provider;
    }

    // Once the selected commit loses its host, every resend from that host remains unavailable.
    // Infrastructure failures keep the last durable checkpoint recoverable at all boundary kinds.
    public class CommitInterruption : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        private Func<CheckpointCommit, bool> _target = null!;
        private bool _apply;
        public bool Injected { get; private set; }

        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, Func<CheckpointCommit, bool> target, bool apply)
        {
            var proxy = Create<IWorkflowStateRepository, CommitInterruption>();
            var interruption = (CommitInterruption)proxy;
            interruption._inner = inner;
            interruption._target = target;
            interruption._apply = apply;

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync))
                return Commit((CheckpointCommit)args![0]!);

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();

                throw;
            }
        }

        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            if (!Injected && !_target(commit))
                return await _inner.CommitWorkflowInstanceAsync(commit);

            if (!Injected)
            {
                Injected = true;

                if (_apply)
                    Assert.Equal(WorkflowCommitStatus.Applied, (await _inner.CommitWorkflowInstanceAsync(commit)).Status);
            }

            throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure,
                "Injected host interruption at the selected Assign boundary");
        }
    }

    public sealed class Data
    {
        public int Count { get; set; }
        public int Copy { get; set; }
        public int Total { get; set; } = 10;
        public int Seen { get; set; } = 20;
        public List<int> Items { get; set; } = [2, 3, 5, 7];
    }

    public sealed class Go
    {
    }
}