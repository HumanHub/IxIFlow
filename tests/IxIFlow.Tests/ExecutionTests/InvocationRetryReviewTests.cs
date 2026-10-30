using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.InvocationRetryTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-18")]
[Trait("Feature", "children")]
public sealed class InvocationRetryReviewTests
{
    [Theory]
    [InlineData(false, "GetWorkflowVersionAsync")]
    [InlineData(true, "GetWorkflowVersionAsync")]
    [InlineData(false, "GetWorkflowStructureHashAsync")]
    [InlineData(true, "GetWorkflowStructureHashAsync")]
    public async Task HostCatalogFailureInterruptsWithoutApplyingInvocationPolicy(bool retry, string method)
    {
        var catalog = DispatchProxy.Create<IWorkflowCatalog, CatalogFault>();
        var fault = (CatalogFault)(object)catalog;
        fault.Inner = new WorkflowCatalog();
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        services.AddSingleton(catalog);
        await using var host = services.BuildServiceProvider();
        var client = host.GetRequiredService<IWorkflowClient>();
        var child = Workflow.Create<Data>("CatalogFailureChild").Build();
        await catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "CatalogFailureInvocation")
            .WaitFor<Go>("go").Invoke<Data>(child.Name, 1, setup => setup.OnError(error =>
            {
                if (retry)
                    error.ThenRetry(new RetryPolicy(2));
                else
                    error.ThenIgnore();
            })).Build();

        var waiting = await client.RunAsync(parent, new Data());
        var store = host.GetRequiredService<IWorkflowStateRepository>();
        var before = (await store.GetWorkflowInstanceAsync(waiting.InstanceId))!;
        fault.Method = method;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(waiting.InstanceId, new Go()));
        Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
        var saved = (await store.GetWorkflowInstanceAsync(waiting.InstanceId))!;
        Assert.False(WorkflowStoreRules.IsTerminal(saved.Status));
        Assert.Empty(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords);
        Assert.Empty((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: saved.InstanceId))).Items);
        Assert.DoesNotContain(await client.GetTraceAsync(saved.InstanceId), entry => entry.Payload is RetryTrace or InvocationTrace);
        Assert.Equal(before.Incarnation, saved.Incarnation);
        fault.Method = null;
        await client.RecoverAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.AwaitAsync(saved.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(20))).Status);

        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(saved.InstanceId))!.ExecutionStateJson)
            .InvocationRecords);

        var attempt = Assert.Single(await InvocationTraceAssertions.AttemptsAsync(store, saved.InstanceId, parent));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.DoesNotContain(await client.GetTraceAsync(saved.InstanceId), entry => entry.Payload is RetryTrace);
        await host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellingInvocationBackoffInsideFinallyRunsRemainingCleanupOnce()
    {
        var clock = new FakeTimeProvider();
        await using var host = Services(clock: clock, ledger: new Ledger { Failures = int.MaxValue });
        var client = host.GetRequiredService<IWorkflowClient>();

        var definition = new WorkflowBuilder<Data, object>([], "FinallyInvocationBackoff")
            .Try(body => body.Step<Caught>())
            .Finally(body => body.Invoke<Child, InvocationRetryTests.ChildData>(setup => setup.Input(data => data.Value).From(_ => "input")
                    .OnError(error => error.ThenRetry(new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 2, 1))))
                .Step<Increment>()).Build();

        var id = await client.StartAsync(definition, new Data());
        var store = host.GetRequiredService<IWorkflowStateRepository>();

        await Until(store, id,
            instance => ExecutionCheckpoint.Read(instance.ExecutionStateJson).InvocationRecords.Values.Any(r => r.RetryAfterUtc != null));

        await client.CancelAsync(id, new CancellationReason { ReasonCode = "during-finally-backoff" });
        var ended = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, ended.Status);
        Assert.Equal(1, Assert.IsType<Data>(ended.WorkflowData).Iteration);
        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).InvocationRecords);
        var attempt = Assert.Single(await InvocationTraceAssertions.AttemptsAsync(store, id, definition));
        Assert.Equal("Cancelled", attempt.Disposition);
        Assert.Null(attempt.RetryAfterUtc);
        Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedInvocationHasTheSameClosureInInspectionAndReplay(bool detach)
    {
        await using var host = Services();
        var client = host.GetRequiredService<IWorkflowClient>();
        var child = new WorkflowBuilder<Data, object>([], "AbandonedInvocationChild").WaitFor<Go>("go").Build();
        await host.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);

        var definition = new WorkflowBuilder<Data, object>([], "AbandonedInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.CancelWithParent(!detach)).Build();

        var parent = await client.RunAsync(definition, new Data());
        await client.CancelAsync(parent.InstanceId, new CancellationReason { ReasonCode = "trace-closure" });
        await client.AwaitAsync(parent.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(20));
        var operations = host.GetRequiredService<IWorkflowOperations>();
        Assert.Empty((await operations.InspectAsync(parent.InstanceId))!.Invocations);
        var store = host.GetRequiredService<IWorkflowStateRepository>();
        var replay = TraceReplay.Load(definition, await store.GetTraceAsync(parent.InstanceId));

        while (replay.StepForward())
        {
        }

        var fact = Assert.Single(replay.Current.Invocations);
        Assert.Equal(detach ? "Detached" : "Cancelled", fact.Disposition);
        Assert.NotNull(fact.ChildInstanceId);
        Assert.Null(fact.RetryAfterUtc);

        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(parent.InstanceId))!.ExecutionStateJson)
            .InvocationRecords);

        if (detach)
            await client.CancelAsync(fact.ChildInstanceId!, new CancellationReason { ReasonCode = "cleanup" });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbandonedStagedInvocationEmitsAClosureWithNoStartedChild(bool tracing)
    {
        var definition = new WorkflowBuilder<Data, object>([], "StagedInvocation").Invoke<Data>("child", 1).Build();
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        instance.TracingEnabled = tracing;
        checkpoint.Trace.Configure(instance, checkpoint, new ExecutionLimits(), TimeProvider.System, scopes);
        var continuation = Assert.Single(checkpoint.Continuations);

        var record = new InvocationRecordState
        {
            Id = "invocation", StepId = "root/0", ContinuationId = continuation.Id,
            Attempts = [new() { Id = "attempt", Number = 1, Phase = "WaitingChild", ProposedChildInstanceId = "never-started" }]
        };

        checkpoint.InvocationRecords.Add(record.Id, record);
        continuation.InvocationRecordId = record.Id;
        InvocationRecords.Abandon(checkpoint, continuation, "Detached");
        InvocationRecords.PrepareCommit(checkpoint, instance, DateTime.UtcNow);
        Assert.Null(record.Attempts[0].ChildInstanceId);
        Assert.Equal("AbandonedBeforeStart", record.Attempts[0].Disposition);
        var facts = checkpoint.Trace.Pending.Select(entry => entry.Payload).OfType<InvocationTrace>().ToArray();

        if (tracing)
        {
            Assert.Equal("AbandonedBeforeStart", Assert.Single(facts).Disposition);
            Assert.Null(facts[0].ChildInstanceId);
        }
        else
            Assert.Empty(facts);
    }

    private static async Task Until(IWorkflowStateRepository store, string id, Func<WorkflowInstance, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!predicate((await store.GetWorkflowInstanceAsync(id))!))
            await Task.Delay(10, timeout.Token);
    }

    public class CatalogFault : DispatchProxy
    {
        public IWorkflowCatalog Inner = null!;
        public string? Method;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == Method && args?[0] as string == "CatalogFailureChild")
                throw new IOException("injected host catalog transport failure");

            try
            {
                return method.Invoke(Inner, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }
    }
}