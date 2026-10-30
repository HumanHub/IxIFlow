using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryContextScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InvocationRetryRebuildsDefaultsAndKeepsPreviousAndNestedItemContext(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var inputs = new Inputs();
        var host = await fixture.Open(services => services.AddSingleton(inputs));
        var child = Workflow.Create<ChildData>("ContextRetryChild").Step<CheckInputs>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<ContextData, object>([], "ContextRetryInvocation")
            .Step<Previous>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.Sequence(nested => nested
                .Invoke<ChildData>(child.Name, 1, WorkflowDefinitionValidator.GetContract(child), setup => setup
                    .Input(data => data.Value).From(ctx => ctx.PreviousStep.Value + "/" + ctx.Item(item))
                    .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)
                    .OnError<InvalidOperationException>(error => error.ThenRetry(new RetryPolicy(1)))))).Build();

        var result = await Finish(host.Client, parent, new ContextData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("previous/7/default", Assert.IsType<ContextData>(result.WorkflowData).Result);
        Assert.Equal(2, inputs.Calls.Count);
        Assert.All(inputs.Calls.Values, count => Assert.Equal(2, count));
        Assert.Equal(new[] { "previous/3", "previous/7" }, inputs.Calls.Keys.Order().ToArray());
        await InvocationTraceAssertions.PrunedAsync(host.Store, result.InstanceId);

        var records = (await InvocationTraceAssertions.AttemptsAsync(host.Store, result.InstanceId, parent))
            .GroupBy(attempt => attempt.InvocationId).ToArray();

        Assert.Equal(2, records.Length);
        Assert.Equal(2, records.Select(r => r.Key).Distinct().Count());

        foreach (var record in records)
        {
            Assert.Single((await host.Store.GetTraceAsync(result.InstanceId)).Select(entry => entry.Entry.Payload).OfType<RetryTrace>(),
                retry => retry.Invocation?.InvocationId == record.Key);

            Assert.Equal(2, record.Count());

            foreach (var attempt in record)
            {
                var childRow = (await host.Store.GetWorkflowInstanceAsync(attempt.ChildInstanceId!))!;

                Assert.Equal(WorkflowDefinitionValidator.GetContract(child).Hash,
                    (await fixture.Catalog.GetWorkflowVersionAsync(childRow.WorkflowName, childRow.WorkflowVersion))!.ContractHash);
            }
        }

        Assert.Equal(4, records.SelectMany(r => r).Select(a => a.ChildInstanceId).Distinct().Count());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RecreatedParentInvocationUsesANewIncarnationAndNewChildren(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var host = await fixture.Open();
        var child = Workflow.Create<Data>("IncarnationRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "IncarnationRetryInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();

        var first = await Finish(host.Client, parent, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, first.Status);
        var saved = (await host.Store.GetWorkflowInstanceAsync(first.InstanceId))!;
        await InvocationTraceAssertions.PrunedAsync(host.Store, first.InstanceId);
        var original = await InvocationTraceAssertions.AttemptsAsync(host.Store, first.InstanceId, parent);
        await host.Store.DeleteWorkflowInstanceAsync(saved.InstanceId, TimeSpan.FromMilliseconds(100));

        if (fixture.Clock is FakeTimeProvider fake)
            fake.Advance(TimeSpan.FromSeconds(1));

        using (var patience = new CancellationTokenSource(InvocationRetryProfile.Patience(TimeSpan.FromMilliseconds(100))))
            do
            {
                await host.Store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromSeconds(1), 100);

                if (await host.Store.GetTombstoneAsync(saved.InstanceId) == null)
                    break;

                await Task.Delay(10, patience.Token);
            } while (true);

        var second = await Finish(host.Client, parent, new Data(), new() { InstanceId = saved.InstanceId });
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
        var replacement = (await host.Store.GetWorkflowInstanceAsync(first.InstanceId))!;
        Assert.NotEqual(saved.Incarnation, replacement.Incarnation);
        await InvocationTraceAssertions.PrunedAsync(host.Store, second.InstanceId);
        var current = await InvocationTraceAssertions.AttemptsAsync(host.Store, second.InstanceId, parent);

        Assert.NotEqual(Assert.Single(original.Select(attempt => attempt.InvocationId).Distinct()),
            Assert.Single(current.Select(attempt => attempt.InvocationId).Distinct()));

        Assert.DoesNotContain(Assert.Single(current).ChildInstanceId, original.Select(a => a.ChildInstanceId));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InvocationPolicyChangesAreRejectedAtRegistrationAndRecovery(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);

        WorkflowDefinition Parent(int retries) => new WorkflowBuilder<Data, object>([], "PolicyHashInvocation")
            .WaitFor<Signal>("go").Invoke<Data>("PolicyHashChild", 1,
                setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(retries)))).Build();

        var original = Parent(1);
        var changed = Parent(2);
        var originalHash = WorkflowDefinitionValidator.ForExecution(original).StructureHash;
        Assert.NotEqual(originalHash, WorkflowDefinitionValidator.ForExecution(changed).StructureHash);
        var first = await fixture.Open();
        var started = await first.Client.RunAsync(original, new Data());
        await Assert.ThrowsAsync<WorkflowEngineException>(() => fixture.Catalog.RegisterWorkflowAsync(changed));
        var before = (await first.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        await first.DisposeAsync();
        var changedCatalog = new WorkflowCatalog();
        await changedCatalog.RegisterWorkflowAsync(changed);
        var second = await fixture.Open(catalog: changedCatalog);
        await second.Store.AppendEventAsync(new(started.InstanceId, "go", WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: "go"));
        Assert.Equal(WorkflowExecutionStatus.Scheduled, (await second.Client.RecoverAsync(started.InstanceId)).Status);
        var after = (await second.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Empty(ExecutionCheckpoint.Read(after.ExecutionStateJson).InvocationRecords);
        Assert.Equal(EventOutcome.Pending, (await second.Store.GetEventAsync(started.InstanceId, "go"))!.Outcome);
        Assert.Empty((await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: started.InstanceId))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InvocationInARetriedSagaHasANewLogicalOccurrence(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var host = await fixture.Open();
        var child = Workflow.Create<Data>("SagaRetryChild").Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "SagaRetryInvocation")
            .Saga(saga => saga.Step<FailOnce>())
            .OnError<InvalidOperationException>(error => error.Compensate().ThenRetry(new RetryPolicy(1))).Build();

        // Saga's fluent activity builder has no Invoke; a definition can still contain it in its
        // forward sequence. Use the ordinary invocation builder for the step and its policies.
        var invocation = new WorkflowBuilder<Data, object>([], "SagaInvocationBody")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build().Steps[0];

        parent.Steps[0].SequenceSteps.Insert(0, invocation);
        var result = await Finish(host.Client, parent, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        await InvocationTraceAssertions.PrunedAsync(host.Store, result.InstanceId);

        var records = (await InvocationTraceAssertions.AttemptsAsync(host.Store, result.InstanceId, parent))
            .GroupBy(attempt => attempt.InvocationId).ToArray();

        Assert.Equal(2, records.Length);
        Assert.Equal(2, records.Select(r => r.Key).Distinct().Count());
        Assert.Equal(2, records.Select(r => Assert.Single(r).ChildInstanceId).Distinct().Count());
        Assert.Empty(await InvocationTraceAssertions.RetriesAsync(host.Store, result.InstanceId));
    }

    private static async Task<WorkflowExecutionResult> Finish<T>(IWorkflowClient client, WorkflowDefinition definition, T data,
        WorkflowOptions? options = null) where T : class
    {
        var id = await client.StartAsync(definition, data, options);

        return await InvocationRetryProfile.Finished(client, id);
    }

    public sealed class ContextData
    {
        public List<int> Items { get; set; } = [3, 7];
        public string? Result { get; set; }
    }

    public sealed class ChildData
    {
        [WorkflowInput, Required] public string Value { get; set; } = "";

        [WorkflowInput, DefaultValue("default")]
        public string Default { get; set; } = "initializer";

        [WorkflowOutput] public string? Result { get; set; }
    }

    public sealed class Inputs
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
    }

    public sealed class Previous : IAsyncActivity
    {
        public string Value { get; set; } = "previous";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class CheckInputs(Inputs inputs) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var data = (ChildData)context.WorkflowData;
            Assert.Equal("default", data.Default);
            var call = inputs.Calls.AddOrUpdate(data.Value, 1, (_, count) => count + 1);
            data.Result = data.Value + "/" + data.Default;
            data.Default = "mutated-in-failed-child";

            if (call == 1)
                throw new InvalidOperationException("retry with fresh defaults");

            return Task.CompletedTask;
        }
    }
}