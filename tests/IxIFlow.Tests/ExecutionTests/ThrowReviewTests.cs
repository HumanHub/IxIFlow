using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;
using Json = System.Text.Json.JsonSerializer;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowReviewTests
{
    [Fact]
    public async Task RethrownChildFaultPayloadSurvivesRestartChildDeletionAndCollectionBeforeParentSettlement()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        var child = new WorkflowBuilder<Data, object>([], "RethrowPayloadChild")
            .Try(t => t.Throw<BusinessError>(ctx => new("original") { Code = 73, Detail = new string('x', 8192) }))
            .Catch<BusinessError, DetailFault>(c => c.Step<ReadDetail>(s => s
                    .Input(x => x.Detail).From(ctx => ctx.Fault.Detail)
                    .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail))
                .WaitFor<Go>("rethrow").Rethrow()).Build();

        var parent = new WorkflowBuilder<Data, object>([], "RethrowPayloadParent")
            .Try(t => t.Invoke<Data>(child.Name, 1))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();

        await catalog.RegisterWorkflowAsync(child);
        string parentId, childId;
        long pausedRevision;
        PayloadReference faultPayload, childDataPayload;

        await using (var first = ClaimCheckedServices())
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var started = await client.RunAsync(parent, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            parentId = started.InstanceId;
            childId = await store.WaitingInstanceAsync(child.Name, "rethrow");
            var savedChild = (await store.GetWorkflowInstanceAsync(childId))!;
            var checkpoint = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(savedChild, Reader()));
            var caught = Assert.Single(checkpoint.Continuations[0].Stack, f => f.TryState?.Phase == TryPhase.Catch).TryState!.Error!.Error;
            faultPayload = Assert.IsType<PayloadReference>(caught.Fault!.Json.Reference);
            childDataPayload = Assert.IsType<PayloadReference>(savedChild.WorkflowDataJson.Reference);

            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await first.GetRequiredService<IWorkflowOperations>().PauseAsync(parentId, "test")).Status);

            pausedRevision = (await store.GetWorkflowInstanceAsync(parentId))!.Revision;
        }

        await using var restarted = ClaimCheckedServices();

        Assert.Equal(WorkflowExecutionStatus.Faulted,
            (await restarted.GetRequiredService<IWorkflowClient>().ResumeAsync(childId, "rethrow", new Go())).Status);

        var notice = (await store.GetEventAsync(parentId, "child-completed-" + childId))!;
        var completion = Json.Deserialize<ChildCompleted>(await Reader().ReadAsync(notice.PayloadJson))!;
        Assert.Equal(faultPayload, completion.Error!.Fault!.Json.Reference);
        Assert.Equal(EventOutcome.Pending, notice.Outcome);
        await store.DeleteWorkflowInstanceAsync(childId);
        Assert.Null(await store.GetWorkflowInstanceAsync(childId));
        Assert.True(await store.PayloadStore.CollectAsync(childDataPayload, TimeSpan.Zero));
        Assert.False(await store.PayloadStore.CollectAsync(faultPayload, TimeSpan.Zero));
        Assert.Equal(pausedRevision, (await store.GetWorkflowInstanceAsync(parentId))!.Revision);

        var result = await restarted.GetRequiredService<IWorkflowOperations>().ContinueAsync(parentId, "test");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Result);
        var committed = (await store.GetWorkflowInstanceAsync(parentId))!;
        Assert.Equal(WorkflowStatus.Completed, committed.Status);
        Assert.True(committed.Revision > pausedRevision);
        var final = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(committed, Reader()));
        Assert.Empty(final.InvocationRecords);
        Assert.DoesNotContain(committed.SavedParts!.Keys, key => key.StartsWith("v/", StringComparison.Ordinal));
        var attempt = Assert.Single(await InvocationTraceAssertions.AttemptsAsync(store, parentId, parent));
        Assert.Equal(WorkflowStatus.Failed, attempt.ChildStatus);
        Assert.Equal("Propagate", attempt.Disposition);
        Assert.Equal(faultPayload, completion.Error!.Fault!.Json.Reference);

        // The settled inbox row owns its completion envelope independently of the pruned invocation.
        Assert.False(await store.PayloadStore.CollectAsync(faultPayload, TimeSpan.Zero));

        PayloadReader Reader() => new(async reference => (await store.PayloadStore.GetAsync(reference))!.Value,
            new PayloadBudget(long.MaxValue));

        ServiceProvider ClaimCheckedServices()
        {
            var services = new ServiceCollection().AddLogging();

            services.AddIxIFlow(o =>
            {
                o.Debugging = true;
                o.VerifyCheckpointParts = true;
                o.PayloadThresholdBytes = 256;
            });

            return services.AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog)
                .BuildServiceProvider();
        }
    }

    [Fact]
    public async Task ThrowUnavailableCatchDtoAfterRestartBypassesAnOuterBusinessCatch()
    {
        var definition = new WorkflowBuilder<Data, object>([], "ThrowUnavailableContext").Try(o => o
                .Try(t => t.Throw<BusinessError>(ctx => new("original") { Code = 73 }))
                .Catch<BusinessError, CodeFault>(c => c.Throw<BusinessError>(ctx => new("next") { Code = ctx.Fault.Code })))
            .Catch<Exception>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => 999)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();

        var (instance, checkpoint, _) = CaughtCheckpoint(definition);
        var saved = checkpoint.Continuations[0].Stack.Single(f => f.TryState?.Phase == TryPhase.Catch).TryState!.Error!.Error;
        saved.Fault!.Type = "Unavailable.Dto, Unavailable.Artifact";
        await AssertEngineFailureAfterRestart(definition, instance, checkpoint, WorkflowErrorKind.ArtifactUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RethrowMissingRequiredPropertyAfterRestartIsCorruptionEvenWhenConstructorRestorable(bool restorable)
    {
        var definition = new WorkflowBuilder<Data, object>([], "ThrowCorruption" + restorable).Try(o =>
        {
            if (restorable)
                o.Try(t => t.Throw<ThrowFaultCaptureTests.RestorableError>(ctx => new("saved", 73)))
                    .Catch<ThrowFaultCaptureTests.RestorableError, CodeFault>(c => c.Rethrow());
            else
                o.Try(t => t.Throw<BusinessError>(ctx => new("saved") { Code = 73 }))
                    .Catch<BusinessError, CodeFault>(c => c.Rethrow());
        }).Catch<Exception>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => 999)
            .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();

        var (instance, checkpoint, _) = CaughtCheckpoint(definition);
        var saved = checkpoint.Continuations[0].Stack.Single(f => f.TryState?.Phase == TryPhase.Catch).TryState!.Error!.Error;
        Assert.Equal(restorable, saved.IsRestorable);
        saved.Properties.Clear();
        await AssertEngineFailureAfterRestart(definition, instance, checkpoint, WorkflowErrorKind.InvalidState);
    }

    [Fact]
    public async Task ThrowTerminalTypeSurvivesSiblingCleanupWaitAndRestart()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowTerminalCleanup").Parallel(p => p
            .Do(b => b.WaitFor<Go>("raise").Throw<BusinessError>(ctx => new("terminal") { Code = 73 }))
            .Do(b => b.Try(t => t.WaitFor<Go>("held")).Finally(f => f.WaitFor<Go>("cleanup")))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(store, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data());
            id = idle.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(id, "raise", new Go())).Status);
            var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
            Assert.False(saved.UnhandledError!.Error.IsRestorable);
            Assert.NotEmpty(saved.Joins);
        }

        await using var second = Services(store, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(typeof(BusinessError).FullName, result.ErrorType);
        var terminal = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
        Assert.Equal(typeof(BusinessError).FullName, terminal.ResultDiagnostic!.ErrorType);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(BusinessError)), terminal.UnhandledError!.Type);
    }

    [Fact]
    public async Task ThrowCachedActivityExceptionHasDistinctOriginsAcrossInstancesAndRetryAttempts()
    {
        var cached = new CachedFailure();
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.AddSingleton(cached).BuildServiceProvider();

        var definition = new WorkflowBuilder<Data, object>([], "ThrowCachedActivity").Saga(s => s
                .Step<CachedActivity>(a => a.OnError<BusinessError>(e => e.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, 1)))))
            .OnError<BusinessError, CodeFault>(e => e.WaitFor<Go>("caught").Compensate().ThenContinue()).Build();

        var origins = new List<WorkflowFaultLocation>();

        for (var run = 0; run < 2; run++)
        {
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            var repository = services.GetRequiredService<IWorkflowStateRepository>();

            var checkpoint =
                ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await repository.GetWorkflowInstanceAsync(result.InstanceId))!));

            var fault = Assert.Single(checkpoint.Continuations[0].Stack, f => f.SagaState != null).SagaState!.Error!.Error;
            var attempts = cached.Attempts.Where(a => a.Instance == result.InstanceId).ToArray();
            Assert.Equal(2, attempts.Length);
            Assert.Equal(attempts[1].Attempt, fault.OriginalFaultLocation!.AttemptId);
            Assert.NotEqual(attempts[0].Attempt, fault.OriginalFaultLocation.AttemptId);
            origins.Add(fault.OriginalFaultLocation);
        }

        Assert.NotEqual(origins[0], origins[1]);
    }

    [Fact]
    public void ThrowSharedFactoryResultStartsANewOccurrenceInTheSameExecution()
    {
        var definition = new WorkflowBuilder<Data, object>([], "ThrowSharedFactory")
            .Throw<BusinessError>(ctx => new("shared") { Code = 73 }).Build();

        var shared = new BusinessError("shared") { Code = 73 };
        definition.Steps[0].ExceptionFactory = _ => shared;
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint { RootData = new Data() };
        using var services = Services();

        checkpoint.Trace.Configure(new WorkflowInstance { InstanceId = "shared-factory" }, checkpoint,
            ExecutionLimits.Default, TimeProvider.System, scopes);

        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        var origins = new List<WorkflowFaultLocation>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var branch = new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] };
            checkpoint.Continuations.Add(branch);
            Assert.Same(shared, Assert.Throws<BusinessError>(() => transitions.Advance(scopes, checkpoint, branch, 100, 100)));
            origins.Add(SerializedException.Capture(shared, scopes, checkpoint, branch, newOccurrence: false).OriginalFaultLocation!);
        }

        Assert.NotEqual(origins[0], origins[1]);
    }

    private static (WorkflowInstance, ExecutionCheckpoint, WorkflowScopeCatalog) CaughtCheckpoint(WorkflowDefinition definition)
    {
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new WorkflowOptions(), DateTime.UtcNow);
        checkpoint.RootData = new Data();
        using var services = Services();
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, TimeProvider.System, scopes);
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        var branch = checkpoint.Continuations[0];
        transitions.Advance(scopes, checkpoint, branch, 100, 100);
        transitions.Advance(scopes, checkpoint, branch, 100, 100);
        var error = Assert.ThrowsAny<Exception>(() => transitions.Advance(scopes, checkpoint, branch, 100, 100));
        Assert.True(transitions.CaptureFailure(scopes, checkpoint, branch, error));
        transitions.Advance(scopes, checkpoint, branch, 100, 100);

        return (instance, checkpoint, scopes);
    }

    private static async Task AssertEngineFailureAfterRestart(WorkflowDefinition definition, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, WorkflowErrorKind kind)
    {
        instance.ExecutionStateJson = Json.Serialize(checkpoint);
        var store = new InMemoryWorkflowStateRepository();
        await store.SeedAsync(instance);
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        await using var restarted = Services(store, catalog);

        var failure = await Record.ExceptionAsync(async () =>
        {
            var result = await restarted.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Result);
        });

        if (failure != null)
            Assert.Equal(kind, Assert.IsType<WorkflowEngineException>(failure).Kind);

        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;

        if (saved.Status == WorkflowStatus.Failed)
        {
            Assert.Equal(typeof(WorkflowEngineException).FullName,
                ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).ResultDiagnostic!.ErrorType);

            Assert.Contains(kind == WorkflowErrorKind.InvalidState ? "Saved exception property" : "unavailable", saved.LastError);
        }
    }

    public sealed class CachedFailure
    {
        public Task Failure { get; } = Task.FromException(new BusinessError("cached") { Code = 73 });
        public List<(string Instance, string Attempt)> Attempts { get; } = [];
    }

    public sealed class CachedActivity(CachedFailure cached) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            cached.Attempts.Add((context.WorkflowInstanceId, context.AttemptId));

            return cached.Failure;
        }
    }
}