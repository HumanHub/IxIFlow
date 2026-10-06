using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using IxIFlow.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class ExecutionLeaseContractTests
{
    [Fact]
    public Task MemoryLeaseIsExclusive() => VerifyExclusiveLease(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryExpiredLeaseCanBeReclaimed() =>
        VerifyExpiredLease(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryRenewalKeepsTheOwner() =>
        VerifyRenewal(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryFormerOwnerCannotCommitAfterTakeover() =>
        VerifyFencing(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryFormerOwnerCannotReplayReceiptAfterTakeover() =>
        VerifyReceiptFencing(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryOnlyTheOwnerCanReleaseItsLease() =>
        VerifyRelease(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryRepeatedStartUsesOneInstance() =>
        VerifyRepeatedStart(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases() =>
        VerifyRecoveryQuery(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlLeaseIsExclusive() => VerifyExclusiveLease(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlExpiredLeaseCanBeReclaimed() => VerifyExpiredLease(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRenewalKeepsTheOwner() => VerifyRenewal(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlFormerOwnerCannotCommitAfterTakeover() => VerifyFencing(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlFormerOwnerCannotReplayReceiptAfterTakeover() =>
        VerifyReceiptFencing(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlOnlyTheOwnerCanReleaseItsLease() => VerifyRelease(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRepeatedStartUsesOneInstance() => VerifyRepeatedStart(SqlRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases() =>
        VerifyRecoveryQuery(SqlRepository());

    [Fact]
    public async Task SuspendedInstanceCanResumeOnAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("CrossHostWait")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>(_ => { })
            .Build();

        var started = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var resumed = await secondHost.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(started.InstanceId, "reply", new LeaseEvent());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal(2, probe.Recorded);
    }

    [Fact]
    public async Task CancellingSuspendedWaitPersistsCancellationAndPreventsResume()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("CancelWait")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>(_ => { })
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await secondHost.GetRequiredService<IWorkflowEngine>()
            .CancelWorkflowAsync(started.InstanceId, new CancellationReason
            {
                ReasonCode = "withdrawn", Description = "Request withdrawn"
            });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, saved.Status);
        Assert.Equal("withdrawn", saved.CancellationReason?.ReasonCode);
        Assert.Equal(1, probe.Recorded);
        var resumed = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(started.InstanceId, "approval", new LeaseEvent());
        Assert.False(resumed.EventAccepted);
    }

    [Fact]
    public async Task OtherHostCanCancelRunningActivityThroughDurableRequest()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe, shortLease: true);
        using var secondHost = CreateHost(repository, registry, probe, shortLease: true);
        var definition = Workflow.Create<LeaseData>("CancelRunning")
            .Step<CancellableBlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();
        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var requested = await secondHost.GetRequiredService<IWorkflowEngine>()
            .CancelWorkflowAsync(saved.InstanceId, new CancellationReason
            {
                ReasonCode = "operator", Description = "Stopped by operator"
            });
        Assert.Equal(WorkflowExecutionStatus.Running, requested.Status);

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        var cancelled = (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, cancelled.Status);
        Assert.Equal("operator", cancelled.CancellationReason?.ReasonCode);
        Assert.Equal(0, probe.Recorded);
    }

    [Fact]
    public async Task CancellationReturnsCompletedWhenOwnerFinishesAfterRequest()
    {
        var inner = new InMemoryWorkflowStateRepository();
        var instanceId = Guid.NewGuid().ToString("N");
        await inner.SaveWorkflowInstanceAsync(new WorkflowInstance
        {
            InstanceId = instanceId,
            WorkflowName = "CompletionRace",
            Status = WorkflowStatus.Running
        });
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            CompleteOnCancellationRequest = true
        };
        using var host = CreateHost(repository, new WorkflowVersionRegistry(), new ActivityProbe());

        var result = await host.GetRequiredService<IWorkflowEngine>()
            .CancelWorkflowAsync(instanceId, new CancellationReason { ReasonCode = "late" });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await inner.GetWorkflowInstanceAsync(instanceId))!.Status);
    }

    [Fact]
    public async Task CancellationDuringSynchronousBranchStopsBeforeItsNextActivity()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowVersionRegistry(), probe);
        var definition = Workflow.Create<LeaseData>("CancelDuringBranch")
            .Step<RecordActivity>(_ => { })
            .If(_ => CancelAndTrue(cancellation),
                then => then.Step<RecordActivity>(_ => { }))
            .Build();

        var result = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData(),
                cancellationToken: cancellation.Token);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, probe.Recorded);
    }

    private static bool CancelAndTrue(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return true;
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlCancellationRequestReachesRunningOwnerOnAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, registry, probe);
        using var secondHost = CreateHost(secondRepository, registry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCancelRunning")
            .Step<CancellableBlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData(),
                new WorkflowOptions { InstanceId = instanceId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowStatus.Running,
            (await firstRepository.GetWorkflowInstanceAsync(instanceId))!.Status);
        try
        {
            var requested = await secondHost.GetRequiredService<IWorkflowEngine>()
                .CancelWorkflowAsync(instanceId, new CancellationReason
                {
                    ReasonCode = "operator", Description = "Stopped by operator"
                });
            Assert.Equal(WorkflowExecutionStatus.Running, requested.Status);
            Assert.Equal(WorkflowExecutionStatus.Cancelled,
                (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            var cancelled = (await secondRepository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(WorkflowStatus.Cancelled, cancelled.Status);
            Assert.Equal("operator", cancelled.CancellationReason?.ReasonCode);
            Assert.Equal(0, probe.Recorded);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlSuspendedWaitCanBeCancelledByAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, registry, probe);
        using var secondHost = CreateHost(secondRepository, registry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCancelWait")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>(_ => { })
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        try
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            var result = await secondHost.GetRequiredService<IWorkflowEngine>()
                .CancelWorkflowAsync(started.InstanceId, new CancellationReason
                {
                    ReasonCode = "withdrawn", Description = "Request withdrawn"
                });
            Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
            Assert.Equal(WorkflowStatus.Cancelled,
                (await firstRepository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
            Assert.Equal(1, probe.Recorded);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(started.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlScannerCompletesCancellationAfterRequestingHostStops()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var firstRegistry = new WorkflowVersionRegistry();
        var secondRegistry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        var definition = Workflow.Create<LeaseData>("SqlCancelAfterHostStop")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("approval")
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        WorkflowExecutionResult started;
        using (var firstHost = CreateHost(firstRepository, firstRegistry, probe))
        {
            started = await firstHost.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new LeaseData());
        }
        try
        {
            Assert.True(await firstRepository.RequestCancellationAsync(started.InstanceId,
                new CancellationReason { ReasonCode = "withdrawn" }));
            using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
            using var scanner = new WorkflowRecoveryService(
                secondHost.GetRequiredService<IServiceScopeFactory>(), secondRepository,
                NullLogger<WorkflowRecoveryService>.Instance);

            await scanner.RecoverOnceAsync(CancellationToken.None);

            Assert.Equal(WorkflowStatus.Cancelled,
                (await firstRepository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(started.InstanceId);
        }
    }

    [Fact]
    public async Task LiveOwnerCannotBeRecoveredByAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("LiveOwner")
            .Step<BlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();

        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        try
        {
            var attempted = await secondHost.GetRequiredService<IWorkflowEngine>()
                .RecoverWorkflowAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowExecutionStatus.Running, attempted.Status);
            Assert.Equal(WorkflowStatus.Running,
                (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        var completed = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, probe.Recorded);
    }

    [Fact]
    public async Task LongActivityRenewsItsLease()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe, shortLease: true);
        using var secondHost = CreateHost(repository, registry, probe, shortLease: true);
        var definition = Workflow.Create<LeaseData>("LongOwner")
            .Step<BlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();

        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        try
        {
            await Task.Delay(600);
            var attempted = await secondHost.GetRequiredService<IWorkflowEngine>()
                .RecoverWorkflowAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowExecutionStatus.Running, attempted.Status);
        }
        finally
        {
            probe.Release.TrySetResult();
        }
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(1, probe.Recorded);
    }

    [Fact]
    public async Task StaleResumeCannotApplyAnEventAfterAnotherHostFinishes()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var staleReader = new PausedReadRepository(repository);
        var registry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(staleReader, registry, probe);
        var definition = Workflow.Create<LeaseData>("StaleResume")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>(_ => { })
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var staleResume = secondHost.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(started.InstanceId, "reply", new LeaseEvent());
        await staleReader.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var winner = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(started.InstanceId, "reply", new LeaseEvent());
        Assert.Equal(WorkflowExecutionStatus.Success, winner.Status);
        staleReader.Continue.TrySetResult();

        var loser = await staleResume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Faulted, loser.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Equal(2, probe.Recorded);
    }

    [Fact]
    public async Task LeaseReleaseFailureDoesNotHideCommittedCompletion()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var releaseFault = new PausedReadRepository(repository) { FailRelease = true };
        var probe = new ActivityProbe();
        using var host = CreateHost(releaseFault, new WorkflowVersionRegistry(), probe);
        var definition = Workflow.Create<LeaseData>("ReleaseFault")
            .Step<RecordActivity>(_ => { })
            .Build();

        var completed = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(completed.InstanceId))!.Status);
    }

    [Fact]
    public async Task QueuedResumeUsesTheRequestedWaitKey()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowVersionRegistry(), probe);
        var definition = Workflow.Create<LeaseData>("QueuedWaitKey")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>(_ => { })
            .Build();
        var started = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var bus = new InMemoryMessageBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new WorkflowQueueService(host, NullLogger<WorkflowQueueService>.Instance,
            bus, coordinator, new WorkflowHostOptions { HostId = "test-host" });
        var command = new ResumeWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Key = "other",
            EventDataType = typeof(LeaseEvent).AssemblyQualifiedName!,
            EventDataJson = "{}"
        };
        await service.ProcessResumeCommandAsync(command);

        Assert.Equal(WorkflowStatus.Suspended,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Equal(1, probe.Recorded);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlSuspendedInstanceCanResumeOnAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var firstRegistry = new WorkflowVersionRegistry();
        var secondRegistry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCrossHostWait")
            .Step<RecordActivity>(_ => { })
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>(_ => { })
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var started = await firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData());
        try
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            var resumed = await secondHost.GetRequiredService<IWorkflowEngine>()
                .ResumeWorkflowAsync(started.InstanceId, "reply", new LeaseEvent());
            Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
            Assert.Equal(2, probe.Recorded);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(started.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlLiveOwnerCannotBeRecoveredByAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var firstRegistry = new WorkflowVersionRegistry();
        var secondRegistry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("SqlLiveOwner")
            .Step<BlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var correlationId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData(),
                new WorkflowOptions { CorrelationId = correlationId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await firstRepository.GetWorkflowInstancesByCorrelationIdAsync(correlationId));
        WorkflowExecutionResult attempted;
        try
        {
            attempted = await secondHost.GetRequiredService<IWorkflowEngine>()
                .RecoverWorkflowAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            probe.Release.TrySetResult();
        }
        WorkflowExecutionResult? completed = null;
        Exception? ownerError = null;
        try
        {
            completed = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error)
        {
            ownerError = error;
        }
        var finalStatus = (await firstRepository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status;
        await firstRepository.DeleteWorkflowInstanceAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Running, attempted.Status);
        Assert.Null(ownerError);
        Assert.Equal(WorkflowStatus.Completed, finalStatus);
        Assert.NotNull(completed);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, probe.Recorded);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlRecoveryScannerReclaimsAnExpiredWorker()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var firstRegistry = new WorkflowVersionRegistry();
        var secondRegistry = new WorkflowVersionRegistry();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("ExpiredWorker")
            .Step<BlockingActivity>(_ => { })
            .Step<RecordActivity>(_ => { })
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var correlationId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LeaseData(),
                new WorkflowOptions { CorrelationId = correlationId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await firstRepository.GetWorkflowInstancesByCorrelationIdAsync(correlationId));
        try
        {
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    UPDATE dbo.IxIFlowWorkflowLeases
                    SET ExpiresAtUtc = DATEADD(SECOND, -1, SYSUTCDATETIME())
                    WHERE InstanceId = @InstanceId
                    """, new { saved.InstanceId });
            }
            using var scanner = new WorkflowRecoveryService(
                secondHost.GetRequiredService<IServiceScopeFactory>(), secondRepository,
                NullLogger<WorkflowRecoveryService>.Instance);
            await scanner.RecoverOnceAsync(CancellationToken.None);

            Assert.Equal(WorkflowStatus.NeedsResolution,
                (await secondRepository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        }
        finally
        {
            probe.Release.TrySetResult();
            try
            {
                await executing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
                // The former owner is fenced after the scanner takes over.
            }
            await firstRepository.DeleteWorkflowInstanceAsync(saved.InstanceId);
        }
    }

    private static ServiceProvider CreateHost(IWorkflowStateRepository repository,
        IWorkflowVersionRegistry registry, ActivityProbe probe, bool shortLease = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton(repository));
        services.Replace(ServiceDescriptor.Singleton(registry));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        if (shortLease)
            services.AddSingleton(new ExecutionLeaseSettings(
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)));
        services.AddSingleton(probe);
        services.AddTransient<BlockingActivity>();
        services.AddTransient<CancellableBlockingActivity>();
        services.AddTransient<RecordActivity>();
        return services.BuildServiceProvider();
    }

    private static SqlWorkflowStateRepository SqlRepository() => new(
        Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!);

    private static async Task VerifyExclusiveLease(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
            Assert.False(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyExpiredLease(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromMilliseconds(100)));
            await Task.Delay(300);
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
            Assert.False(await repository.RenewExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyRenewal(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(1)));
            await Task.Delay(500);
            Assert.True(await repository.RenewExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(1)));
            await Task.Delay(750);
            Assert.False(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyFencing(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromMilliseconds(100)));
            var oldOwner = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "LeaseFencing",
                Status = WorkflowStatus.Running,
                WorkflowDataJson = "{}",
                ExecutionLeaseToken = "first"
            };
            var initial = await repository.CommitWorkflowInstanceAsync(oldOwner, 0, "initial");
            Assert.Equal(WorkflowCommitStatus.Applied, initial.Status);
            await Task.Delay(300);
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
            oldOwner.WorkflowDataJson = "\"stale\"";
            var stale = await repository.CommitWorkflowInstanceAsync(oldOwner, 1, "stale");
            Assert.Equal(WorkflowCommitStatus.Conflict, stale.Status);
            var newOwner = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            newOwner.ExecutionLeaseToken = "second";
            newOwner.WorkflowDataJson = "\"current\"";
            var current = await repository.CommitWorkflowInstanceAsync(newOwner, 1, "current");
            Assert.Equal(WorkflowCommitStatus.Applied, current.Status);
            Assert.Equal("\"current\"",
                (await repository.GetWorkflowInstanceAsync(instanceId))!.WorkflowDataJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyRelease(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
            Assert.False(await repository.ReleaseExecutionLeaseAsync(instanceId, "second"));
            Assert.False(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
            Assert.True(await repository.ReleaseExecutionLeaseAsync(instanceId, "first"));
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyReceiptFencing(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromMilliseconds(100)));
            var formerOwner = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "ReceiptFencing",
                Status = WorkflowStatus.Running,
                WorkflowDataJson = "{}",
                ExecutionLeaseToken = "first"
            };
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(formerOwner, 0, "start")).Status);
            await Task.Delay(300);
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));

            Assert.Equal(WorkflowCommitStatus.Conflict,
                (await repository.CommitWorkflowInstanceAsync(formerOwner, 0, "start")).Status);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyRepeatedStart(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowVersionRegistry(), probe);
        var definition = Workflow.Create<LeaseData>("IdempotentStart")
            .Step<RecordActivity>(_ => { })
            .Build();
        try
        {
            var first = await host.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new LeaseData(), new WorkflowOptions { InstanceId = instanceId });
            var replay = await host.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new LeaseData(), new WorkflowOptions { InstanceId = instanceId });

            Assert.Equal(instanceId, first.InstanceId);
            Assert.Equal(instanceId, replay.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Success, replay.Status);
            Assert.Equal(1, probe.Recorded);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyRecoveryQuery(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            await repository.SaveWorkflowInstanceAsync(new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "RecoveryQuery",
                Status = WorkflowStatus.Running
            });
            Assert.Contains(await repository.GetWorkflowsRequiringRecoveryAsync(),
                instance => instance.InstanceId == instanceId);
            Assert.True(await repository.TryAcquireExecutionLeaseAsync(instanceId, "worker",
                TimeSpan.FromMilliseconds(150)));
            Assert.DoesNotContain(await repository.GetWorkflowsRequiringRecoveryAsync(),
                instance => instance.InstanceId == instanceId);
            await Task.Delay(300);
            Assert.Contains(await repository.GetWorkflowsRequiringRecoveryAsync(),
                instance => instance.InstanceId == instanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    public sealed class LeaseData;
    public sealed class LeaseEvent;

    public sealed class ActivityProbe
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Recorded;
    }

    public sealed class BlockingActivity(ActivityProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult();
            await probe.Release.Task;
        }
    }

    public sealed class RecordActivity(ActivityProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Recorded);
            return Task.CompletedTask;
        }
    }

    public sealed class CancellableBlockingActivity(ActivityProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class PausedReadRepository(InMemoryWorkflowStateRepository inner) : IWorkflowStateRepository
    {
        public bool CompleteOnCancellationRequest { get; set; }
        public bool PauseFirstRead { get; set; } = true;
        public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
        {
            var accepted = await inner.RequestCancellationAsync(instanceId, reason);
            if (accepted && CompleteOnCancellationRequest)
            {
                var instance = (await inner.GetWorkflowInstanceAsync(instanceId))!;
                instance.Status = WorkflowStatus.Completed;
                await inner.SaveWorkflowInstanceAsync(instance);
            }
            return accepted;
        }
        public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            inner.GetCancellationRequestAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync() =>
            inner.GetWorkflowsRequiringRecoveryAsync();
        private int _reads;
        public bool FailRelease { get; set; }
        public TaskCompletionSource ReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
        {
            var snapshot = await inner.GetWorkflowInstanceAsync(instanceId);
            if (PauseFirstRead && Interlocked.Increment(ref _reads) == 1)
            {
                ReadCompleted.TrySetResult();
                await Continue.Task;
            }
            return snapshot;
        }

        public Task<bool> TryAcquireExecutionLeaseAsync(string id, string token, TimeSpan duration) =>
            inner.TryAcquireExecutionLeaseAsync(id, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string id, string token, TimeSpan duration) =>
            inner.RenewExecutionLeaseAsync(id, token, duration);
        public Task<bool> ReleaseExecutionLeaseAsync(string id, string token) =>
            FailRelease ? throw new IOException("Release acknowledgment unavailable") :
                inner.ReleaseExecutionLeaseAsync(id, token);
        public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
            WorkflowInstance instance, long revision, string commitId) =>
            inner.CommitWorkflowInstanceAsync(instance, revision, commitId);
        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) =>
            inner.SaveWorkflowInstanceAsync(instance);
        public Task<bool> TryClaimSuspendedWorkflowAsync(WorkflowInstance instance) =>
            inner.TryClaimSuspendedWorkflowAsync(instance);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string name) =>
            inner.GetWorkflowInstancesByNameAsync(name);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            inner.GetWorkflowInstancesByStatusAsync(status);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string id) =>
            inner.GetWorkflowInstancesByCorrelationIdAsync(id);
        public Task DeleteWorkflowInstanceAsync(string id) => inner.DeleteWorkflowInstanceAsync(id);
        public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync() =>
            inner.GetSuspendedWorkflowsReadyForResumptionAsync();
    }
}
