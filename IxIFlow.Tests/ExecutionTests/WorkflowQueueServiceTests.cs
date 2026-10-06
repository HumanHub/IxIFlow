using IxIFlow.Core;
using IxIFlow.Builders;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowQueueServiceTests
{
    [Fact]
    public async Task HostStopCommitsAnActivityThatReturnedBeforeLeavingItsClaim()
    {
        var probe = new ExecutionLeaseContractTests.ActivityProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(probe);
        services.AddTransient<ExecutionLeaseContractTests.BlockingActivity>();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("GracefulStop")
            .Step<ExecutionLeaseContractTests.BlockingActivity>(_ => { })
            .Step<NoopActivity>(_ => { })
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");
        using var stopping = new CancellationTokenSource();
        var processing = service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = "{}"
        }, stopping.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stopping.Cancel();
        probe.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processing.WaitAsync(TimeSpan.FromSeconds(5)));

        var saved = (await provider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(instanceId))!;
        Assert.Contains(saved.ExecutionHistory, entry =>
            entry.EntryType == TraceEntryType.ActivityCompleted &&
            entry.ActivityName.Contains("BlockingActivity"));
        var recovered = await provider.GetRequiredService<IWorkflowEngine>()
            .RecoverWorkflowAsync(instanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
    }

    [Fact]
    public async Task UnknownDefinitionDoesNotEmitAStartedEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
            {
                InstanceId = instanceId,
                TargetHostId = "test-host",
                WorkflowName = "Unknown",
                WorkflowVersion = 1,
                WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
                WorkflowDataJson = "{}"
            }));

        Assert.Empty(bus.Starts);
    }

    [Fact]
    public async Task ExecuteCommandDoesNotPublishCompletionWhenWorkflowWaits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedWait")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");

        await service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(new QueueData())
        });

        Assert.Equal(WorkflowStatus.Suspended,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(instanceId))!.Status);
        Assert.Empty(bus.Completions);
    }

    [Fact]
    public async Task ExecuteCommandPublishesCompletionAfterSuccess()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedSuccess")
            .Step<NoopActivity>()
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");

        await service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(new QueueData())
        });

        Assert.Equal(WorkflowExecutionStatus.Success, Assert.Single(bus.Completions).Status);
    }

    [Fact]
    public async Task RedeliveredResumeCommandDoesNotSatisfyTheNextWait()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("RepeatedApprovalWait")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .WaitFor<QueueEvent>("approval")
            .Build();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var first = new ResumeWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Key = "approval",
            EventDataType = typeof(QueueEvent).AssemblyQualifiedName!,
            EventDataJson = "{}"
        };
        await service.ProcessResumeCommandAsync(first);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var afterFirst = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, afterFirst.Status);

        await service.ProcessResumeCommandAsync(first);

        var afterRedelivery = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, afterRedelivery.Status);
        Assert.Equal(afterFirst.Revision, afterRedelivery.Revision);

        await service.ProcessResumeCommandAsync(new ResumeWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Key = "approval",
            EventDataType = typeof(QueueEvent).AssemblyQualifiedName!,
            EventDataJson = "{}"
        });
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Fact]
    public async Task DeliveryIdCannotBeReusedForDifferentEventData()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("ConflictingDelivery")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .WaitFor<QueueEvent>("approval")
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var first = new ResumeWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Key = "approval",
            EventDataType = typeof(QueueEvent).AssemblyQualifiedName!,
            EventDataJson = "{\"Value\":1}"
        };
        await service.ProcessResumeCommandAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessResumeCommandAsync(new ResumeWorkflowCommand
            {
                CommandId = first.CommandId,
                InstanceId = first.InstanceId,
                TargetHostId = first.TargetHostId,
                Key = first.Key,
                EventDataType = first.EventDataType,
                EventDataJson = "{\"Value\":2}"
            }));
    }

    [Fact]
    public async Task DuplicateExecuteCommandDoesNotReportRecoveryRejectionAsCompletion()
    {
        var engine = new TransitionEngine();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowEngine>(engine));
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("DuplicateExecute")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var instanceId = Guid.NewGuid().ToString("N");
        await provider.GetRequiredService<IWorkflowStateRepository>()
            .SaveWorkflowInstanceAsync(new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = definition.Name,
                Status = WorkflowStatus.Suspended
            });
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = "{}"
        });

        Assert.Equal(2, engine.ExecuteCalls);
        Assert.Equal(0, engine.RecoverCalls);
        Assert.Empty(bus.Completions);
        Assert.Empty(bus.Starts);
    }

    [Fact]
    public async Task CancelCommandChangesSavedWaitBeforePublishingCancelled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedCancel")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "withdrawn" }
        });

        Assert.Equal(WorkflowStatus.Cancelled,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled,
            Assert.Single(bus.Completions).Status);
    }

    [Fact]
    public async Task CancelCommandDoesNotClaimCompletedWorkflowWasCancelled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("AlreadyCompleted")
            .Step<NoopActivity>()
            .Build();
        var completed = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = completed.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "too-late" }
        });

        Assert.Equal(WorkflowStatus.Completed,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(completed.InstanceId))!.Status);
        Assert.DoesNotContain(bus.Completions,
            result => result.Status == WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task CancelCommandDoesNotPublishCompletionWhileFinallyWaits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedCancelCleanupWait")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<QueueEvent>("approval"))
            .Finally(body => body.WaitFor<QueueEvent>("cleanup"))
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "withdrawn" }
        });

        Assert.Equal(WorkflowStatus.Suspended,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Empty(bus.Completions);
    }

    [Fact]
    public async Task CancelCommandCanWaitForItsExecuteCommand()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await Assert.ThrowsAsync<WorkflowInstanceBusyException>(() =>
            service.ProcessCancelCommandAsync(new CancelWorkflowCommand
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                Reason = new CancellationReason { ReasonCode = "withdrawn" }
            }));
        Assert.Empty(bus.Completions);
    }

    [Fact]
    public async Task ExpiredUnmatchedResumeIsRejectedInsteadOfDeferredForever()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, new RecordingBus(), coordinator);
        var command = ResumeCommand(Guid.NewGuid().ToString("N"));
        command.QueuedAt = DateTime.UtcNow.AddHours(-2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessResumeCommandAsync(command));
    }

    [Fact]
    public async Task ExpiredCancelForMissingInstanceIsRejected()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, new RecordingBus(), coordinator);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ProcessCancelCommandAsync(new CancelWorkflowCommand
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                Reason = new CancellationReason { ReasonCode = "withdrawn" },
                QueuedAt = DateTime.UtcNow.AddHours(-2)
            }));
    }
    [Fact]
    public async Task ConsumerFailureStopsTheQueueServiceWithTheOriginalError()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var bus = new FailingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                service.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Execute consumer failed", error.Message);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    [Fact]
    public async Task LostMessageClaimDoesNotStopLaterDeliveries()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var bus = new LostClaimBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = service.RunAsync(stop.Token);
        await bus.SecondRejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, bus.RejectedCount);
    }

    [Fact]
    public async Task AcknowledgeStoreErrorLeavesCommandForRedeliveryAndKeepsConsumerRunning()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("AcknowledgeFailure")
            .Step<NoopActivity>().Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new AcknowledgeFailureBus(definition);
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = service.RunAsync(stop.Token);
        await bus.SecondAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, bus.RejectedCount);
        Assert.Equal(2, bus.AcknowledgeAttempts);
    }

    [Fact]
    public async Task BusyResumeCommandIsClassifiedForDeferral()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowEngine>(new TransitionEngine()));
        using var provider = services.BuildServiceProvider();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, new RecordingBus(), coordinator);

        await Assert.ThrowsAsync<WorkflowInstanceBusyException>(() =>
            service.ProcessResumeCommandAsync(new ResumeWorkflowCommand
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                Key = "approval",
                EventDataType = typeof(QueueEvent).AssemblyQualifiedName!,
                EventDataJson = "{}"
            }));
    }

    [Theory]
    [InlineData(WorkflowExecutionStatus.Faulted, null)]
    [InlineData(WorkflowExecutionStatus.Suspended, WorkflowStatus.Suspended)]
    public async Task UnacceptedResumeIsDeferredWhileTheInstanceMayStillWait(
        WorkflowExecutionStatus resultStatus, WorkflowStatus? storedStatus)
    {
        var engine = new TransitionEngine { ResumeStatus = resultStatus };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowEngine>(engine));
        using var provider = services.BuildServiceProvider();
        var instanceId = Guid.NewGuid().ToString("N");
        if (storedStatus != null)
            await provider.GetRequiredService<IWorkflowStateRepository>()
                .SaveWorkflowInstanceAsync(new WorkflowInstance
                {
                    InstanceId = instanceId,
                    WorkflowName = "PendingWait",
                    Status = storedStatus.Value
                });
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, new RecordingBus(), coordinator);

        await Assert.ThrowsAsync<WorkflowInstanceBusyException>(() =>
            service.ProcessResumeCommandAsync(ResumeCommand(instanceId)));
    }

    [Fact]
    public async Task UnacceptedResumeForTerminalInstanceIsRejected()
    {
        var engine = new TransitionEngine { ResumeStatus = WorkflowExecutionStatus.Faulted };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowEngine>(engine));
        using var provider = services.BuildServiceProvider();
        var instanceId = Guid.NewGuid().ToString("N");
        await provider.GetRequiredService<IWorkflowStateRepository>()
            .SaveWorkflowInstanceAsync(new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "Finished",
                Status = WorkflowStatus.Completed
            });
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, new RecordingBus(), coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessResumeCommandAsync(ResumeCommand(instanceId)));
    }

    private static ResumeWorkflowCommand ResumeCommand(string instanceId) => new()
    {
        InstanceId = instanceId,
        TargetHostId = "test-host",
        Key = "approval",
        EventDataType = typeof(QueueEvent).AssemblyQualifiedName!,
        EventDataJson = "{}"
    };

    private sealed class LostClaimBus : IAcknowledgingMessageBus
    {
        public TaskCompletionSource SecondRejected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RejectedCount;

        public Task PublishAsync<T>(T message) where T : class => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IMessageDelivery<T>> ConsumeDeliveriesAsync<T>(
            string? targetHostId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where T : class
        {
            if (typeof(T) == typeof(ResumeWorkflowCommand))
            {
                yield return (IMessageDelivery<T>)(object)new LostClaimDelivery(this, true);
                yield return (IMessageDelivery<T>)(object)new LostClaimDelivery(this, false);
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private sealed class LostClaimDelivery(LostClaimBus bus, bool loseClaim) :
            IMessageDelivery<ResumeWorkflowCommand>
        {
            public ResumeWorkflowCommand Message { get; } = new()
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                EventDataType = "missing type"
            };
            public Task AcknowledgeAsync() => Task.CompletedTask;
            public Task DeferAsync(TimeSpan delay) => Task.CompletedTask;
            public Task RejectAsync(Exception error)
            {
                var count = Interlocked.Increment(ref bus.RejectedCount);
                if (loseClaim)
                    throw new MessageClaimLostException("Another host owns this message");
                if (count == 2)
                    bus.SecondRejected.TrySetResult();
                return Task.CompletedTask;
            }
        }
    }

    private sealed class AcknowledgeFailureBus(WorkflowDefinition definition) : IAcknowledgingMessageBus
    {
        public TaskCompletionSource SecondAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcknowledgeAttempts;
        public int RejectedCount;

        public Task PublishAsync<T>(T message) where T : class => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<IMessageDelivery<T>> ConsumeDeliveriesAsync<T>(
            string? targetHostId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            where T : class
        {
            if (typeof(T) == typeof(ExecuteWorkflowCommand))
            {
                yield return (IMessageDelivery<T>)(object)new AcknowledgeFailureDelivery(this, definition);
                yield return (IMessageDelivery<T>)(object)new AcknowledgeFailureDelivery(this, definition);
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private sealed class AcknowledgeFailureDelivery(
            AcknowledgeFailureBus bus, WorkflowDefinition definition) :
            IMessageDelivery<ExecuteWorkflowCommand>
        {
            public ExecuteWorkflowCommand Message { get; } = new()
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                WorkflowName = definition.Name,
                WorkflowVersion = definition.Version,
                WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
                WorkflowDataJson = "{}"
            };

            public Task AcknowledgeAsync()
            {
                if (Interlocked.Increment(ref bus.AcknowledgeAttempts) == 1)
                    throw new IOException("SQL acknowledgement unavailable");
                bus.SecondAcknowledged.TrySetResult();
                return Task.CompletedTask;
            }

            public Task RejectAsync(Exception error)
            {
                Interlocked.Increment(ref bus.RejectedCount);
                throw new IOException("SQL rejection unavailable");
            }

            public Task DeferAsync(TimeSpan delay) => Task.CompletedTask;
        }
    }

    private sealed class TestQueueService(IServiceProvider services, IMessageBus bus,
        IWorkflowCoordinator coordinator) : WorkflowQueueService(
            services, NullLogger<WorkflowQueueService>.Instance, bus, coordinator,
            new WorkflowHostOptions { HostId = "test-host" })
    {
        public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class TransitionEngine : IWorkflowEngine
    {
        public int ExecuteCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public WorkflowExecutionStatus ResumeStatus { get; set; } = WorkflowExecutionStatus.Running;

        public Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
            WorkflowDefinition definition, TWorkflowData workflowData,
            WorkflowOptions? options = null, CancellationToken cancellationToken = default)
            where TWorkflowData : class => Task.FromResult(new WorkflowExecutionResult
            {
                InstanceId = options?.InstanceId ?? "",
                Status = ++ExecuteCalls == 1
                    ? WorkflowExecutionStatus.Running : WorkflowExecutionStatus.Suspended
            });

        public Task<WorkflowExecutionResult> RecoverWorkflowAsync(string instanceId,
            CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            return Task.FromResult(new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Faulted,
                ErrorMessage = "The instance is no longer running"
            });
        }

        public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(string instanceId,
            TEventData @event, CancellationToken cancellationToken = default)
            where TEventData : class => throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(string instanceId,
            string key, TEventData @event, CancellationToken cancellationToken = default)
            where TEventData : class => throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResumeWorkflowDeliveryAsync<TEventData>(string instanceId,
            string? key, TEventData @event, string deliveryId,
            CancellationToken cancellationToken = default)
            where TEventData : class => Task.FromResult(new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = ResumeStatus,
                EventAccepted = false
            });
        public Task<WorkflowExecutionResult> CancelWorkflowAsync(string instanceId,
            CancellationReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResolveActivityAsync(string instanceId,
            string invocationId, ActivityResolution resolution,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId) =>
            throw new NotSupportedException();
    }

    private sealed class FailingBus : IMessageBus
    {
        private readonly CancellationTokenSource _stop = new();

        public Task PublishAsync<T>(T message) where T : class => Task.CompletedTask;

        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            if (typeof(T) == typeof(ExecuteWorkflowCommand))
                throw new IOException("Execute consumer failed");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            yield break;
        }

        public Task StopAsync()
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }
    }

    public sealed class QueueData;
    public sealed class QueueEvent
    {
        public int Value { get; set; }
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<WorkflowExecutionCompletedEvent> Completions { get; } = [];
        public List<WorkflowExecutionStartedEvent> Starts { get; } = [];
        public Task PublishAsync<T>(T message) where T : class
        {
            if (message is WorkflowExecutionCompletedEvent completion)
                Completions.Add(completion);
            if (message is WorkflowExecutionStartedEvent started)
                Starts.Add(started);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task StopAsync() => Task.CompletedTask;
    }
}
