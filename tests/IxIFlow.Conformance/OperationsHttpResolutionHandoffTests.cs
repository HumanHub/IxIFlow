using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class OperationsHttpResolutionHandoffTests
{
    [Fact]
    public async Task SlowAcceptanceDoesNotConsumeTheCompletionWait()
    {
        var store = new InMemoryWorkflowStateRepository();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var factory = new OperationsHttpFactory(store)
        {
            Wait = TimeSpan.FromSeconds(1),
            ConfigureServices = registrations =>
            {
                registrations.Replace(ServiceDescriptor.Scoped<IActivityExecutor>(services =>
                    new HeldExecutor(ActivatorUtilities.CreateInstance<ActivityExecutor>(services), entered, release)));

                registrations.Replace(ServiceDescriptor.Singleton<IWorkflowOperations>(services =>
                {
                    var proxy = DispatchProxy.Create<IWorkflowOperations, DelayedAcceptance>();
                    var delay = (DelayedAcceptance)proxy;
                    delay.Inner = ActivatorUtilities.CreateInstance<HostedWorkflowOperations>(services);
                    delay.Accepted = accepted;

                    return proxy;
                }));
            }
        };

        using var http = factory.Operator();
        var invocationId = await SeedAsync(factory, store, block: false);

        try
        {
            var response = http.PostAsJsonAsync("/ops/instances/resolution-handoff/resolutions",
                new { invocationId, kind = "Completed", outputs = new { Number = 42 }, note = "observed" });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The submit decorator returns only after acceptance has already exceeded the
            // configured budget. Keep execution blocked while checking the completion wait.
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(response, await Task.WhenAny(response, Task.Delay(100)));
            release.TrySetResult();
            using var completed = await response.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

            Assert.Equal(42, JsonSerializer.Deserialize<Data>(
                (await store.GetWorkflowInstanceAsync("resolution-handoff"))!.WorkflowDataJson.InlineText)!.Number);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task AcceptedResolutionRetainsConvertedOutputsUntilItsExecutorCanApplyThem()
    {
        var store = new InMemoryWorkflowStateRepository();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var factory = new OperationsHttpFactory(store)
        {
            Wait = TimeSpan.Zero,
            ConfigureServices = registrations => registrations.Replace(ServiceDescriptor.Scoped<IActivityExecutor>(services =>
                new HeldExecutor(ActivatorUtilities.CreateInstance<ActivityExecutor>(services), entered, release)))
        };

        using var http = factory.Operator();
        var invocationId = await SeedAsync(factory, store, block: true);

        try
        {
            using var response = await http.PostAsJsonAsync("/ops/instances/resolution-handoff/resolutions",
                new { invocationId, kind = "Completed", outputs = new { Number = 42 }, note = "observed" });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var accepted = (await store.GetWorkflowInstanceAsync("resolution-handoff"))!;
            Assert.Equal(0, JsonSerializer.Deserialize<Data>(accepted.WorkflowDataJson.InlineText)!.Number);
            var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(accepted));
            var decision = Assert.Single(saved.Continuations).PendingActivity!.Resolution!;
            Assert.Equal(42, Assert.IsType<int>(decision.OutputProperties[nameof(Uncertain.Number)]!.Read(factory.Services)));
            release.TrySetResult();
            await factory.ActivityProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var applied = (await store.GetWorkflowInstanceAsync("resolution-handoff"))!;
            Assert.Equal(42, JsonSerializer.Deserialize<Data>(applied.WorkflowDataJson.InlineText)!.Number);
            factory.ActivityProbe.Exit.TrySetResult();

            Assert.Equal(WorkflowExecutionStatus.Success,
                (await factory.Engine.AwaitAsync("resolution-handoff", WorkflowAwaitCondition.Finished)
                    .WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally
        {
            release.TrySetResult();
            factory.ActivityProbe.Exit.TrySetResult();
        }
    }

    private static async Task<string> SeedAsync(OperationsHttpFactory factory,
        InMemoryWorkflowStateRepository store, bool block)
    {
        foreach (var scanner in factory.Services.GetServices<IHostedService>().OfType<WorkflowRecoveryService>())
            await scanner.StopAsync(default);

        var builder = Workflow.Create<Data>("HttpResolutionHandoff")
            .Step<Uncertain>(step => step.Output(activity => activity.Number).To(context => context.WorkflowData.Number));

        var definition = block ? builder.Step<Block>().Build() : builder.WaitFor<Approval>("approval").Build();
        await factory.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);

        var pending = new ActivityInvocationState
        {
            StepId = scopes.Id(definition.Steps[0]), StartedAtUtc = DateTime.UtcNow,
            Attempts = [new ActivityAttemptState { StartedAtUtc = DateTime.UtcNow }], ResolutionReason = "uncertain"
        };

        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = scopes.StructureHash,
            Continuations =
            [
                new ContinuationState
                {
                    Status = ContinuationStatus.WaitingResolution, PendingActivity = pending,
                    Stack = [new ScopePosition { ScopeId = "root" }]
                }
            ]
        };

        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = "resolution-handoff", WorkflowName = definition.Name, WorkflowVersion = definition.Version,
            CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Status = WorkflowStatus.NeedsResolution,
            WorkflowDataJson = StoredJson.Inline(JsonSerializer.Serialize(new Data())),
            WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(Data)), ExecutionStateJson = JsonSerializer.Serialize(checkpoint)
        });

        return pending.Id;
    }

    private sealed class HeldExecutor(
        IActivityExecutor inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IActivityExecutor
    {
        public async Task<PreparedActivity> PrepareTypedActivityAsync<TData, TPrevious>(
            TypedActivityExecutionRequest<TData, TPrevious> request, bool applyInputMappings,
            string invocationId, string attemptId, IReadOnlyCollection<string>? codeInputs = null)
        {
            if (request.ActivityType == typeof(Uncertain))
            {
                entered.TrySetResult();
                await release.Task;
            }

            return await inner.PrepareTypedActivityAsync(request, applyInputMappings, invocationId, attemptId, codeInputs);
        }
    }

    public class DelayedAcceptance : DispatchProxy
    {
        public IWorkflowOperations Inner = null!;
        public TaskCompletionSource Accepted = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            try
            {
                var result = method!.Invoke(Inner, args);

                return method.Name == nameof(IWorkflowOperations.SubmitResolutionAsync)
                    ? DelayAsync((Task<WorkflowOperation>)result!)
                    : result;
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }

        private async Task<WorkflowOperation> DelayAsync(Task<WorkflowOperation> submit)
        {
            var operation = await submit;
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            Accepted.TrySetResult();

            return operation;
        }
    }
}