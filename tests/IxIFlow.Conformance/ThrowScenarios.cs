using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowScenarios
{
    public static IEnumerable<object[]> Shapes() => StoreProfiles.ProfileNames.SelectMany(p =>
        new[] { "catch", "saga", "unhandled", "switch", "inline", "flowchart", "state", "parallel", "each" }.SelectMany(s =>
            new[] { false, true }.Select(yaml => new object[] { p, s, yaml })));

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ThrowFaultAndOriginalLocationSurviveProviderRestart(string profile, string shape, bool yaml)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var fluent = Definition(shape);
        var generated = YamlDefinition(shape);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var definition = yaml ? generated : fluent;
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;
        var repository = store.NewRepository(clock);

        await using (var first = Services(store, repository, catalog, clock))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
            var trace = await first.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
            var raised = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRaised);
            Assert.Equal(WorkflowStepType.Throw, Assert.IsType<FaultTrace>(raised.Entry.Payload).StepKind);
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        await using var second = Services(store, store.NewRepository(clock), catalog, clock);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "handler", new Go());
        Assert.Equal(shape == "unhandled" ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Success, result.Status);

        if (shape != "unhandled")
            Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);

        var entries = await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        Assert.Single(entries, e => e.Entry.EntryType == TraceEntryType.FaultRaised);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ThrowCatchWaitRethrowKeepsDifferentProjectionAndOriginalLocation(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowProjectionRestart").Try(o => o
                .Try(t => t.Throw<BusinessError>(ctx => new("private") { Code = 73, Detail = new string('x', 8192) }).WithId("origin"))
                .Catch<BusinessError, CodeFault>(c => c.WaitFor<Go>("handler").Rethrow()))
            .Catch<BusinessError, DetailFault>(c => c.Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Input(x => x.Detail).From(ctx => ctx.Fault.Detail).Output(x => x.Value).To(ctx => ctx.WorkflowData.Value)
                .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;
        WorkflowFaultLocation origin;
        var repository = store.NewRepository();

        await using (var first = Services(store, repository, catalog))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            id = run.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            origin = Assert.Single((await first.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!.CatchFaults).OriginalLocation!;
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        await using var second = Services(store, store.NewRepository(), catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "handler", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(new string('x', 8192), Assert.IsType<Data>(result.WorkflowData).Detail);

        var rethrow = Assert.Single(await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id),
            e => e.Entry.EntryType == TraceEntryType.FaultRethrown);

        Assert.Equal(origin, Assert.IsType<FaultTrace>(rethrow.Entry.Payload).OriginalLocation);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ThrowFinallyWaitUsesStoreClockBeforeOuterCatch(string profile)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(clock);
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowFinallyDeadline").Try(o => o
                .Try(b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 }))
                .Finally(b => b.WaitFor<Go>("cleanup",
                    configure: s => s.TimeoutAfter(TimeSpan.FromMilliseconds(100)).OnTimeout(b => b.Step<Read>()))))
            .Catch<BusinessError, CodeFault>(c => c.Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(store, repository, catalog, clock))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            id = run.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        var successor = store.NewRepository(clock);
        await FlowchartScenarios.WaitForTimeoutAsync(successor, id, clock);
        await using var second = Services(store, successor, catalog, clock);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
    }

    public static IEnumerable<object[]> DeferredProfiles() => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.SelectMany(each =>
            new[] { DebugStepKind.Into, DebugStepKind.Over }.Select(kind => new object[] { profile, each, kind })));

    [Theory]
    [MemberData(nameof(DeferredProfiles))]
    public async Task ThrowDeferredByDebuggerRetainsPropertiesAcrossClaimCheckAndRestart(string profile, bool each, DebugStepKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowDeferred" + each + kind).WaitFor<Go>("start").Try(t =>
        {
            if (each)
                t.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items,
                    body: (b, item) =>
                        b.Throw<BusinessError>(ctx => new("private") { Code = ctx.Item(item), Detail = new string('x', 8192) })
                            .WithId("throw"), s => s.Parallel(2));
            else
                t.Parallel(p => p
                    .Do(b => b.Throw<BusinessError>(ctx => new("private") { Code = 73, Detail = new string('x', 8192) }).WithId("throw"))
                    .Do(b => b.Delay(TimeSpan.FromHours(1))));
        }).Catch<BusinessError, DetailFault>(c => c.Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
            .Input(x => x.Detail).From(ctx => ctx.Fault.Detail).Output(x => x.Value).To(ctx => ctx.WorkflowData.Value)
            .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(store, repository, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            var idle = await client.RunAsync(definition, new Data());
            id = idle.InstanceId;
            await ops.SetBreakpointsAsync(id, [new("throw", "throw")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Go())).Status);
            var before = (await ops.InspectAsync(id))!;
            var selected = before.DebugState.Stop!.BranchId;
            var held = before.Threads.Where(t => t.ContinuationId != selected).ToArray();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, selected, kind, "step")).Status);
            var blocked = (await ops.InspectAsync(id))!;
            Assert.Equal("Blocked", blocked.DebugState.Stop!.Reason);
            Assert.Empty(blocked.CatchFaults);

            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(held),
                System.Text.Json.JsonSerializer.Serialize(blocked.Threads.Where(t => t.ContinuationId != selected)));
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        await using var second = Services(store, store.NewRepository(), catalog);
        var resumed = second.GetRequiredService<IWorkflowClient>();
        var operations = second.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await resumed.RecoverAsync(id)).Status);
        await operations.SetBreakpointsAsync(id, [], "clear");
        var result = await operations.ContinueAsync(id, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(new string('x', 8192), Assert.IsType<Data>(result.WorkflowData).Detail);
        var trace = await operations.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, trace);
        Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRaised);

        foreach (var pick in trace.Where(e => e.Entry.EntryType is TraceEntryType.StepEntered or TraceEntryType.ActivityStarted))
        {
            replay.Seek(pick.Sequence);
            Assert.Equal(pick.Entry.StepId, replay.CurrentStepId);
        }

        Assert.Contains(trace, e => e.Entry.EntryType == TraceEntryType.Paused);
        Assert.Contains(trace, e => e.Entry.EntryType == TraceEntryType.Continued);
    }

    public static IEnumerable<object[]> Boundaries() => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "before", "lost-ack" }.Select(fault => new object[] { profile, fault }));

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task ThrowCatchBoundaryRecoversBeforeCommitAndAfterLostAcknowledgement(string profile, string fault)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowBoundary").WaitFor<Go>("start")
            .Try(t => t.Throw<BusinessError>(ctx => new("private") { Code = 73 }))
            .Catch<BusinessError, CodeFault>(c => c.WaitFor<Go>("handler").Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        var injecting = false;
        var interruptions = 0;
        var acknowledgementLost = false;
        ExecutionCheckpoint? interrupted = null;

        bool Interrupt(WorkflowInstance instance)
        {
            if (!injecting || instance.Status != WorkflowStatus.Suspended)
                return false;

            interrupted = ReadCheckpoint(repository, instance).GetAwaiter().GetResult();
            Interlocked.Increment(ref interruptions);

            if (fault == "lost-ack")
                acknowledgementLost = true;

            return true;
        }

        var faults = new FaultRepository(repository)
        {
            FailCommit = instance => fault == "before" && Interrupt(instance) || acknowledgementLost,
            LoseAcknowledgement = instance => fault == "lost-ack" && Interrupt(instance)
        };

        string id;

        await using (var first = Services(store, faults, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data());
            id = idle.InstanceId;
            injecting = true;
            var failure = await Record.ExceptionAsync(() => client.ResumeAsync(id, "start", new Go()));
            Assert.IsType<WorkflowEngineException>(failure);
            Assert.True(interruptions > 0);
            var candidate = Assert.IsType<ExecutionCheckpoint>(interrupted);
            Assert.Equal(TryPhase.Catch, Assert.Single(candidate.Continuations[0].Stack, f => f.TryState != null).TryState!.Phase);
            Assert.Null(candidate.Continuations[0].PendingActivity);
            Assert.Single(candidate.Waits, w => w.Key == "handler");
            var committed = await ReadCheckpoint(repository, (await repository.GetWorkflowInstanceAsync(id))!);

            if (fault == "lost-ack")
                Assert.Equal(TryPhase.Catch, Assert.Single(committed.Continuations[0].Stack, f => f.TryState != null).TryState!.Phase);
            else
                Assert.DoesNotContain(committed.Continuations[0].Stack, f => f.TryState?.Phase == TryPhase.Catch);

            Assert.Null(committed.Continuations[0].PendingActivity);
            injecting = false;
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        await using var second = Services(store, store.NewRepository(), catalog);
        var recovered = second.GetRequiredService<IWorkflowClient>();

        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (fault == "before" ? await recovered.RecoverAsync(id) : await recovered.AwaitAsync(id, WorkflowAwaitCondition.Idle)).Status);

        var result = await recovered.ResumeAsync(id, "handler", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);

        Assert.Single(await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id),
            e => e.Entry.EntryType == TraceEntryType.FaultRaised);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ThrowFaultSurvivesRestartDuringSagaCompensationBeforeHandlerWait(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();
        var effects = new UndoEffects();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowSagaCompensationRestart").Saga(s => s
                .Step<Read>(a => a.CompensateWith<RecoverableUndo>())
                .Throw<BusinessError>(ctx => new("private") { Code = 73, Detail = new string('x', 8192) }))
            .OnError<BusinessError, DetailFault>(e => e.WaitFor<Go>("handler").Step<Read>(s => s.Input(x => x.Value)
                .From(ctx => ctx.Fault.Code)
                .Input(x => x.Detail).From(ctx => ctx.Fault.Detail).Output(x => x.Value).To(ctx => ctx.WorkflowData.Value)
                .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail)).Compensate().ThenContinue()).Build();

        var raised = ParallelRules.Flatten(definition.Steps).Single(s => s.StepType == WorkflowStepType.Throw);
        var factory = raised.ExceptionFactory!;

        raised.ExceptionFactory = context =>
        {
            Interlocked.Increment(ref effects.Raised);

            return factory(context);
        };

        await catalog.RegisterWorkflowAsync(definition);
        ExecutionCheckpoint? interrupted = null;
        var interruptions = 0;
        var acknowledgementLost = false;

        var faults = new FaultRepository(repository)
        {
            FailCommit = _ => acknowledgementLost,
            LoseAcknowledgement = instance =>
            {
                var candidate = ReadCheckpoint(repository, instance).GetAwaiter().GetResult();

                if (!candidate.Continuations.Any(c => c.PendingActivity != null &&
                        c.Stack.Any(f => f.SagaState?.Phase == SagaPhase.Compensating)))
                    return false;

                interrupted = candidate;
                acknowledgementLost = true;
                Interlocked.Increment(ref interruptions);

                return true;
            }
        };

        var id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, faults, catalog, effects: effects))
        {
            var error = await Record.ExceptionAsync(() =>
                first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }));

            Assert.IsType<WorkflowEngineException>(error);
            Assert.True(interruptions > 0);
            var committed = await ReadCheckpoint(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            var branch = Assert.Single(committed.Continuations);
            Assert.Equal(SagaPhase.Compensating, Assert.Single(branch.Stack, f => f.SagaState != null).SagaState!.Phase);
            var pending = Assert.IsType<ActivityInvocationState>(branch.PendingActivity);
            var pendingAttempt = Assert.Single(pending.Attempts);
            Assert.Equal("Execute", pendingAttempt.Kind);
            Assert.Null(pendingAttempt.EndedAtUtc);
            Assert.Null(pendingAttempt.Observation);
            Assert.NotNull(pending.RecoveryState);
            Assert.Equal(interrupted!.Continuations[0].PendingActivity!.Id, pending.Id);
            Assert.Equal(interrupted.Continuations[0].PendingActivity!.Attempts[0].Id, pendingAttempt.Id);
            Assert.Empty(effects.Invocations);
        }

        if (repository is IAsyncDisposable oldRepository)
            await oldRepository.DisposeAsync();

        await using var second = Services(store, store.NewRepository(), catalog, effects: effects);
        var client = second.GetRequiredService<IWorkflowClient>();
        var resumed = await client.RecoverAsync(id);

        if (resumed.Status != WorkflowExecutionStatus.Suspended)
            resumed = await client.AwaitAsync(id, WorkflowAwaitCondition.Idle);

        Assert.Equal(WorkflowExecutionStatus.Suspended, resumed.Status);
        var result = await client.ResumeAsync(id, "handler", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(new string('x', 8192), Assert.IsType<Data>(result.WorkflowData).Detail);
        Assert.Single(effects.Invocations);
        Assert.Equal(1, effects.Raised);
        Assert.True(effects.Recovered);
    }

    public static IEnumerable<object[]> BrokenFaults() => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(artifact => new object[] { profile, artifact }));

    [Theory]
    [MemberData(nameof(BrokenFaults))]
    public async Task ThrowSavedPropertyAndArtifactFailuresBypassBusinessCatchAfterRestart(string profile, bool artifact)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);

        var definition = new WorkflowBuilder<Data, object>([], "ThrowBrokenSavedFault").Try(o => o
                .Try(t => t.Throw<BusinessError>(ctx => new("private") { Code = 73, Detail = "saved" }))
                .Catch<BusinessError, CodeFault>(c => c.Rethrow()))
            .Catch<BusinessError, DetailFault>(c => c.Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value))).Build();

        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new WorkflowOptions(), DateTime.UtcNow);
        checkpoint.RootData = new Data();

        using (var setup = new ServiceCollection().BuildServiceProvider())
        {
            checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, TimeProvider.System, scopes);
            var transitions = new ContinuationTransitions(setup, TimeProvider.System);
            var branch = checkpoint.Continuations[0];
            transitions.Advance(scopes, checkpoint, branch, 100, 100);
            transitions.Advance(scopes, checkpoint, branch, 100, 100);
            var error = Assert.Throws<BusinessError>(() => transitions.Advance(scopes, checkpoint, branch, 100, 100));
            Assert.True(transitions.CaptureFailure(scopes, checkpoint, branch, error));
            var saved = branch.Stack[^1].TryState!.Error!.Error;
            Assert.NotNull(saved.OriginalFaultLocation);
            Assert.NotNull(saved.Fault);

            if (artifact)
                saved.Type = "Unavailable.BusinessError, Unavailable.Artifact";
            else
                saved.Properties.Remove(FaultProjection.PropertyKey(typeof(BusinessError).GetProperty(nameof(BusinessError.Code))!));
        }

        instance.ExecutionStateJson = System.Text.Json.JsonSerializer.Serialize(checkpoint);
        var repository = store.NewRepository();
        await repository.SeedAsync(instance);
        await using var recovered = Services(store, repository, catalog);

        if (artifact)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                recovered.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId));

            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        }
        else
        {
            var result = await recovered.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            var stored = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;

            Assert.Equal(typeof(WorkflowEngineException).FullName,
                ExecutionCheckpoint.ReadTrunk(stored.ExecutionStateJson).ResultDiagnostic!.ErrorType);

            Assert.Contains("Saved exception property", stored.LastError);
            Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Value);
        }
    }

    private static async Task<ExecutionCheckpoint> ReadCheckpoint(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference)
            ?? throw new InvalidDataException("Missing checkpoint payload"), new PayloadBudget(100_000_000));

        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }

    private static WorkflowDefinition YamlDefinition(string shape) => shape switch
    {
        "catch" => IxIFlow.Conformance.Generated.Workflow_throw_catch_workflow.Build(),
        "saga" => IxIFlow.Conformance.Generated.Workflow_throw_saga_workflow.Build(),
        "unhandled" => IxIFlow.Conformance.Generated.Workflow_throw_unhandled_workflow.Build(),
        "switch" => IxIFlow.Conformance.Generated.Workflow_throw_switch_workflow.Build(),
        "flowchart" => IxIFlow.Conformance.Generated.Workflow_throw_flowchart_workflow.Build(),
        "state" => IxIFlow.Conformance.Generated.Workflow_throw_state_workflow.Build(),
        "parallel" => IxIFlow.Conformance.Generated.Workflow_throw_parallel_workflow.Build(),
        "each" => IxIFlow.Conformance.Generated.Workflow_throw_each_workflow.Build(),
        _ => new WorkflowBuilder<Data, object>([], "ThrowConformanceinline").Try(t => UseInline(t, true))
            .Catch<BusinessError, CodeFault>(c => c.WaitFor<Go>("handler").Step<Read>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value))).Build()
    };

    // Both inline forms graft fresh private bodies; the callee itself is authored in fluent C# or YAML.
    private static void UseInline(IWorkflowBuilder<Data, object> builder, bool yaml)
    {
        var child = InlineDefinition();

        var pin = new WorkflowInlinePin(child.Name, child.Version, WorkflowDefinitionValidator.Inspect(child),
            WorkflowDefinitionValidator.GetContract(child).Hash);

        builder.Use<Data>(() => yaml ? IxIFlow.Conformance.Generated.Workflow_throw_inline_child_workflow.Build() : InlineDefinition(),
            pin);
    }

    private static WorkflowDefinition InlineDefinition() => Workflow.Create<Data>("ThrowConformanceInline")
        .Throw<BusinessError>(ctx => new("private") { Code = 73 }).Build();

    private static WorkflowDefinition Definition(string shape)
    {
        var builder = new WorkflowBuilder<Data, object>([], "ThrowConformance" + shape);

        if (shape == "saga")
            return builder.Saga(b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 }))
                .OnError<BusinessError, CodeFault>(e => e.WaitFor<Go>("handler").Step<Read>(s => s.Input(x => x.Value)
                    .From(ctx => ctx.Fault.Code)
                    .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value)).Compensate().ThenContinue()).Build();

        var attempt = builder.Try(t =>
        {
            switch (shape)
            {
                case "switch":
                    t.Switch(ctx => 1, c => c.Case(1, b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 })));

                    break;
                case "inline":
                    UseInline(t, false);

                    break;
                case "flowchart":
                    t.Flowchart(g => g.Start("one").Node("one", b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 })));

                    break;
                case "state":
                    t.StateMachine(m =>
                        m.Start("one")
                            .State("one",
                                s => s.Entry(b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 })).On<Go>("go", "done"))
                            .State("done", s => s.Final()));

                    break;
                case "parallel":
                    t.Parallel(p =>
                        p.Do(b => b.Throw<BusinessError>(ctx => new("private") { Code = 73 })).Do(b => b.Delay(TimeSpan.FromHours(1))));

                    break;
                case "each":
                    t.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items,
                        body: (b, item) => b.Throw<BusinessError>(ctx => new("private") { Code = ctx.Item(item) }), s => s.Parallel(2));

                    break;
                default:
                    t.Throw<BusinessError>(ctx => new("private") { Code = 73 });

                    break;
            }
        });

        if (shape == "unhandled")
            return attempt.Finally(b => b.WaitFor<Go>("handler")).Build();

        return attempt.Catch<BusinessError, CodeFault>(c => c.WaitFor<Go>("handler").Step<Read>(s => s.Input(x => x.Value)
            .From(ctx => ctx.Fault.Code)
            .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value))).Build();
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog,
        TimeProvider? time = null, UndoEffects? effects = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.PayloadThresholdBytes = 1;
        });

        services.AddSingleton(repository);
        services.AddSingleton(catalog);

        if (time != null)
            services.AddSingleton(time);

        if (effects != null)
            services.AddSingleton(effects);

        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

        return provider;
    }

    public sealed class UndoEffects
    {
        public int Raised;
        public bool Recovered;
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Invocations { get; } = new();
    }

    public sealed class RecoverableUndo(UndoEffects effects) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.True(effects.Invocations.TryAdd(context.InvocationId, true));

            return Task.CompletedTask;
        }

        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(state, context.InvocationId);
            effects.Recovered = true;

            return Task.FromResult<ActivityRecoveryResult<string>>(effects.Invocations.ContainsKey(state)
                ? new ActivityRecoveryResult<string>.Completed()
                : new ActivityRecoveryResult<string>.Execute());
        }
    }

    public sealed class Data
    {
        public int Value { get; set; }
        public string Detail { get; set; } = "";
        public int[] Items { get; set; } = [73, 74];
    }

    public sealed class Go;

    public sealed class BusinessError(string message) : Exception(message)
    {
        public int Code { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class CodeFault
    {
        public int Code { get; set; }
    }

    public sealed class DetailFault
    {
        public int Code { get; set; }
        public string Detail { get; set; } = "";
    }

    public sealed class Read : IAsyncActivity
    {
        [ActivityInputOutput] public int Value { get; set; }
        [ActivityInputOutput] public string Detail { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Workflow("ThrowConformanceInline", Version = 1)]
    public sealed class InlineBody : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Throw<BusinessError>(ctx => new("private") { Code = 73 });
    }
}