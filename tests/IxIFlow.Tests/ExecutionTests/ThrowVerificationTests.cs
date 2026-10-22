using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowVerificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowResolvedChildPreservesCallerFaultPropertiesAcrossRestart(bool restorable)
    {
        const string childName = "ThrowResolvedChild";
        var store = new FaultInjectingRepository
        {
            FailCommit = instance => instance.WorkflowName == childName &&
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations.Any(c =>
                    c.PendingActivity == null && c.Stack.Any(f => f.NextStepIndex > 0))
        };
        var catalog = new WorkflowCatalog();
        var child = new WorkflowBuilder<Data, object>([], childName).Step<Echo>().Build();
        var parent = new WorkflowBuilder<Data, object>([], "ThrowResolutionParent")
            .Try(t => t.Invoke<Data>(childName, 1))
            .Catch<BusinessError, CodeFault>(c => c.WaitFor<Go>("project")
                .Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                    .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await catalog.RegisterWorkflowAsync(child);
        await catalog.RegisterWorkflowAsync(parent);
        string parentId;
        await using (var first = Services(store, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var started = await client.RunAsync(parent, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            parentId = started.InstanceId;
            var parentState = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(parentId))!));
            var childId = Assert.Single(parentState.Continuations).PendingActivity!.ChildWorkflowId!;
            await Until(async () =>
            {
                try { return (await client.RecoverAsync(childId)).Status == WorkflowExecutionStatus.NeedsResolution; }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure) { return false; }
            });
            store.FailCommit = _ => false;
            var pending = Assert.Single(await client.GetPendingActivitiesAsync(childId));
            BusinessError error = restorable ? new ConstructorBusinessError("resolved", 73)
                : new BusinessError("resolved") { Code = 73 };
            // Keep the parent from consuming the completion until a new host reads the saved envelope.
            store.FailRead = id => id == parentId;
            Assert.Equal(WorkflowExecutionStatus.Faulted,
                (await client.ResolveAsync(childId, pending.InvocationId, ActivityResolution.Failed(error))).Status);
            var childState = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(childId))!));
            Assert.Equal(restorable, childState.UnhandledError!.IsRestorable);
            Assert.Equal(73, System.Text.Json.JsonSerializer.Deserialize<int>(
                Assert.Single(childState.UnhandledError.Properties).Value.Json!));
            await Until(() => Task.FromResult(store.FailedReads > 0));
        }
        store.FailRead = _ => false;
        await using var restarted = Services(store, catalog);
        var restartedClient = restarted.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await restartedClient.RecoverAsync(parentId)).Status);
        var result = await restartedClient.ResumeAsync(parentId, "project", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowCachedPreparationExceptionStartsANewOccurrenceAtEachSite(bool invocationInput)
    {
        var failures = new CachedPreparation();
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton(failures).BuildServiceProvider();
        var definition = new WorkflowBuilder<Data, object>([], "ThrowPreparationOccurrences")
            .Try(t =>
            {
                if (invocationInput) t.Invoke<Data>("ThrowInputChild", 1, s => s.Input(d => d.Code).From(_ => 11));
                else t.Step<FailPreparation>(s => s.Input(a => a.Code).From(_ => 11));
            })
            .Catch<MutableBusinessError, CodeFault>(c => c.Step<RecordFault>(s => s.Input(a => a.Code).From(ctx => ctx.Fault.Code)))
            .Try(t =>
            {
                if (invocationInput) t.Invoke<Data>("ThrowInputChild", 1, s => s.Input(d => d.Code).From(_ => 73));
                else t.Step<FailPreparation>(s => s.Input(a => a.Code).From(_ => 73));
            })
            .Catch<MutableBusinessError, CodeFault>(c => c.Step<RecordFault>(s => s.Input(a => a.Code).From(ctx => ctx.Fault.Code)))
            .Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var sites = scopes.AllSteps.Where(s => invocationInput ? s.Value.StepType == WorkflowStepType.WorkflowInvocation
            : s.Value.ActivityType == typeof(FailPreparation)).ToArray();
        if (invocationInput)
        {
            await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(
                new WorkflowBuilder<Data, object>([], "ThrowInputChild").Build());
            foreach (var (site, code) in sites.Zip(new[] { 11, 73 }))
                Assert.Single(site.Value.InputMappings).SourceFunction = _ => failures.Raise(code);
        }
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { 11, 73 }, failures.Codes);
        Assert.Equal(2, sites.Length);
        Assert.Equal(sites.Select(s => s.Key), failures.Origins.Select(o => o.StepId));
        Assert.NotEqual(failures.Origins[0], failures.Origins[1]);
        Assert.Equal(2, failures.Raised.Count);
        Assert.All(failures.Raised, error => Assert.Same(failures.Error, error));
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The workflow did not reach the expected state");
            await Task.Delay(20);
        }
    }

    public sealed class ConstructorBusinessError : BusinessError
    {
        public ConstructorBusinessError(string message, int code) : base(message) { Code = code; }
    }
    public sealed class MutableBusinessError : Exception { public int Code { get; set; } }
    public sealed class CachedPreparation
    {
        public MutableBusinessError Error { get; } = new();
        public List<Exception> Raised { get; } = [];
        public List<int> Codes { get; } = [];
        public List<WorkflowFaultLocation> Origins { get; } = [];
        public int Raise(int code)
        {
            Error.Code = code;
            Raised.Add(Error);
            throw Error;
        }
    }
    public sealed class FailPreparation(CachedPreparation failures) : IRecoverableActivity<string>
    {
        public int Code { get; set; }
        public string CaptureRecoveryState(IActivityContext context)
        {
            failures.Raise(Code);
            throw new InvalidOperationException("The cached error should have been raised");
        }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preparation should fail before execution");
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No start was committed");
    }
    public sealed class RecordFault(CachedPreparation failures, IWorkflowStateRepository store) : IAsyncActivity
    {
        public int Code { get; set; }
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            failures.Codes.Add(Code);
            var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(context.WorkflowInstanceId))!));
            failures.Origins.Add(Assert.Single(saved.Continuations.SelectMany(c => c.Stack), f => f.TryState?.Phase == TryPhase.Catch)
                .TryState!.Error!.OriginalFaultLocation!);
        }
    }
}
