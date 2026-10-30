using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Exercises an accepted debugger command whose immediate execution claim is refused.</summary>
public static class DebugCommandWakeScenario
{
    public static async Task VerifyAsync(IWorkflowStateRepository requesterRepository,
        IWorkflowStateRepository ownerRepository, DebugCommandKind kind,
        Action<IServiceCollection>? configureHost = null,
        Func<IServiceProvider, Task>? startHostLinks = null,
        Func<IServiceProvider, Task>? stopHostLinks = null)
    {
        using var catalog = new WorkflowCatalog();

        // Pure control flow and result collection: no activity or child invocations can wake it.
        var definition = new WorkflowBuilder<Data, object>([], "DebugCommandWake")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Result(c => c.Item(item) + 1),
                setup => setup.Into(c => c.WorkflowData.Results)).Build();

        await catalog.RegisterWorkflowAsync(definition);
        await using var requester = Services(requesterRepository, "requester");
        await using var owner = Services(ownerRepository, "lease-holder");

        try
        {
            if (startHostLinks != null)
            {
                await startHostLinks(requester);
                await startHostLinks(owner);
            }

            var operations = requester.GetRequiredService<IWorkflowOperations>();
            var id = Guid.NewGuid().ToString("N");

            await operations.LaunchAsync(id, definition.Name, definition.Version,
                JsonSerializer.SerializeToElement(new Data()), stopOnEntry: true, actor: "launcher");

            var paused = (await requesterRepository.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(WorkflowStatus.Paused, paused.Status);
            Assert.Null(paused.RunnableAtUtc);
            var snapshot = await requester.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(id);
            var continuation = Assert.Single(snapshot!.Pointers).PointerId;

            const string ownerToken = "lease-holder";

            var claim = await StoreTestSupport.ClaimAsync(owner);

            // The launch can expose its paused commit before its execution scope releases the lease.
            var held = await TestEventually.UntilAsync(id, "the paused launch lease released and acquired by the competing owner",
                () => ownerRepository.TryAcquireExecutionLeaseAsync(id, ownerToken, TimeSpan.FromMinutes(1), claim,
                    ExecutionClaimKind.Operator), grant => grant != null);

            Assert.NotNull(held);

            string commandId;

            try
            {
                var result = kind == DebugCommandKind.Continue
                    ? await operations.ContinueAsync(id, "operator")
                    : await operations.StepAsync(id, continuation, DebugStepKind.Over, "operator");

                Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
                Assert.Equal(WorkflowErrorKind.InvalidState, result.ErrorKind);
                Assert.Equal("The workflow instance is executing on another host", result.ErrorMessage);

                var command = Assert.Single((await requesterRepository.GetDebugStatesAsync([id]))[id].Commands,
                    command => command.Kind == kind);

                commandId = command.Id;
                Assert.Equal("operator", command.Actor);
                Assert.Null(command.AcknowledgedRevision);
                Assert.Equal(WorkflowStatus.Paused, (await requesterRepository.GetWorkflowInstanceAsync(id))!.Status);

                if (kind == DebugCommandKind.Step)
                {
                    Assert.Equal(continuation, command.ContinuationId);
                    Assert.Equal(DebugStepKind.Over, command.StepKind);
                }
            }
            finally
            {
                Assert.True((await ownerRepository.ReleaseExecutionLeaseAsync(id, ownerToken)).Released);
            }

            // Read only: no resubmission, manual recovery scan, event, or AwaitAsync can supply the wake.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var saved = (await requesterRepository.GetWorkflowInstanceAsync(id))!;

            while (saved.Status != WorkflowStatus.Completed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                saved = (await requesterRepository.GetWorkflowInstanceAsync(id))!;
            }

            Assert.True(saved.Status == WorkflowStatus.Completed,
                $"The accepted {kind} command was stranded after lease release; status: {saved.Status}");

            var data = JsonSerializer.Deserialize<Data>(saved.WorkflowDataJson.InlineText)!;
            Assert.Equal(new ItemResult<int>(0, ItemStatus.Completed, 42), Assert.Single(data.Results));

            var applied = Assert.Single((await requesterRepository.GetDebugStatesAsync([id]))[id].Commands,
                command => command.Kind == kind);

            Assert.Equal(commandId, applied.Id);
            Assert.NotNull(applied.AcknowledgedRevision);
        }
        finally
        {
            foreach (var provider in new[] { requester, owner })
            {
                if (stopHostLinks != null)
                    await stopHostLinks(provider);

                await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            }
        }

        ServiceProvider Services(IWorkflowStateRepository repository, string name)
        {
            var services = new ServiceCollection().AddLogging();

            services.AddIxIFlow(options =>
            {
                options.Debugging = true;
                options.HostName = name;
            });

            services.AddSingleton(repository);
            services.AddSingleton<IWorkflowCatalog>(catalog);
            configureHost?.Invoke(services);

            // Recovery services are deliberately not started: only the command's wake can retry.
            return services.BuildServiceProvider();
        }
    }

    public sealed class Data
    {
        public List<int> Items { get; set; } = [41];
        public List<ItemResult<int>> Results { get; set; } = [];
    }
}