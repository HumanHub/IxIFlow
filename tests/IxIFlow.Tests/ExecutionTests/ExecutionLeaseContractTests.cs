using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using IxIFlow.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class ExecutionLeaseContractTests : IClassFixture<SqliteTestDatabase<ExecutionLeaseContractTests>>
{
    [Fact]
    public Task MemoryLeaseIsExclusive() => VerifyExclusiveLease(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryExpiredLeaseCanBeReclaimed()
    {
        var time = new FakeTimeProvider();
        return VerifyExpiredLease(new InMemoryWorkflowStateRepository(time), time);
    }

    [Fact]
    public Task MemoryRenewalKeepsTheOwner()
    {
        var time = new FakeTimeProvider();
        return VerifyRenewal(new InMemoryWorkflowStateRepository(time), TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750), time);
    }

    [Fact]
    public Task MemoryFormerOwnerCannotCommitAfterTakeover()
    {
        var time = new FakeTimeProvider();
        return VerifyFencing(new InMemoryWorkflowStateRepository(time), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(300), time);
    }

    [Fact]
    public Task MemoryFormerOwnerCannotReplayReceiptAfterTakeover()
    {
        var time = new FakeTimeProvider();
        return VerifyReceiptFencing(new InMemoryWorkflowStateRepository(time), time);
    }

    [Fact]
    public Task MemoryOnlyTheOwnerCanReleaseItsLease() =>
        VerifyRelease(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryRepeatedStartUsesOneInstance() =>
        VerifyRepeatedStart(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task MemoryRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases()
    {
        var time = new FakeTimeProvider();
        return VerifyRecoveryQuery(new InMemoryWorkflowStateRepository(time), time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-02")]
    public Task SqlLeaseIsExclusive() => VerifyExclusiveLease(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-02")]
    public Task PostgreSqlLeaseIsExclusive() => VerifyExclusiveLease(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteLeaseIsExclusive()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyExclusiveLease(repository);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-02")]
    public Task SqlConcurrentFirstLeaseHasOneOwner() => VerifyConcurrentFirstLeaseHasOneOwner(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-02")]
    public Task PostgreSqlConcurrentFirstLeaseHasOneOwner() => VerifyConcurrentFirstLeaseHasOneOwner(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlExpiredLeaseCanBeReclaimed() => VerifyExpiredLease(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlExpiredLeaseCanBeReclaimed() => VerifyExpiredLease(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteExpiredLeaseCanBeReclaimed()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString, time);
        await VerifyExpiredLease(repository, time);
    }

    // A second on each side of the renewal and of the check, so a slow SQL Server cannot cross either.
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRenewalKeepsTheOwner() => VerifyRenewal(SqlRepository(), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRenewalKeepsTheOwner() => VerifyRenewal(PostgreSqlRepository(), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteRenewalKeepsTheOwner()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString, time);
        await VerifyRenewal(repository, TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750), time);
    }

    // A lease long enough for the first commit on a cold store connection; the takeover waits it out.
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlFormerOwnerCannotCommitAfterTakeover() => VerifyFencing(SqlRepository(), TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1_200));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlFormerOwnerCannotCommitAfterTakeover() => VerifyFencing(PostgreSqlRepository(), TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1_200));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteFormerOwnerCannotCommitAfterTakeover()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString, time);
        await VerifyFencing(repository, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(300), time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlFormerOwnerCannotReplayReceiptAfterTakeover() =>
        VerifyReceiptFencing(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlFormerOwnerCannotReplayReceiptAfterTakeover() =>
        VerifyReceiptFencing(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteFormerOwnerCannotReplayReceiptAfterTakeover()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString, time);
        await VerifyReceiptFencing(repository, time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlOnlyTheOwnerCanReleaseItsLease() => VerifyRelease(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlOnlyTheOwnerCanReleaseItsLease() => VerifyRelease(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteOnlyTheOwnerCanReleaseItsLease()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyRelease(repository);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRepeatedStartUsesOneInstance() => VerifyRepeatedStart(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRepeatedStartUsesOneInstance() => VerifyRepeatedStart(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteRepeatedStartUsesOneInstance()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyRepeatedStart(repository);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases() =>
        VerifyRecoveryQuery(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases() =>
        VerifyRecoveryQuery(PostgreSqlRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteRecoveryQueryExcludesLiveOwnersAndFindsExpiredLeases()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString, time);
        await VerifyRecoveryQuery(repository, time);
    }

    [Fact]
    public async Task SuspendedInstanceCanResumeOnAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("CrossHostWait")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>()
            .Build();

        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var resumed = await secondHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "reply", new LeaseEvent());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal(2, probe.Recorded);
    }

    [Fact]
    public async Task CancellingSuspendedWaitPersistsCancellationAndPreventsResume()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("CancelWait")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>()
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await secondHost.GetRequiredService<IWorkflowClient>()
            .CancelAsync(started.InstanceId, new CancellationReason
            {
                ReasonCode = "withdrawn", Description = "Request withdrawn"
            });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, saved.Status);
        Assert.Equal("withdrawn", saved.CancellationReason?.ReasonCode);
        Assert.Equal(1, probe.Recorded);
        var resumed = await firstHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "approval", new LeaseEvent());
        EventAssert.Closed(resumed);
    }

    [Fact]
    public async Task OtherHostCanCancelRunningActivityThroughDurableRequest()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe, shortLease: true);
        using var secondHost = CreateHost(repository, registry, probe, shortLease: true);
        var definition = Workflow.Create<LeaseData>("CancelRunning")
            .Step<CancellableBlockingActivity>()
            .Step<RecordActivity>()
            .Build();
        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Running], Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));

        var requested = await secondHost.GetRequiredService<IWorkflowClient>()
            .CancelAsync(saved.InstanceId, new CancellationReason
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
        using (var setup = CreateHost(inner, new WorkflowCatalog(), new ActivityProbe()))
        {
            var started = await setup.GetRequiredService<IWorkflowClient>().RunAsync(
                Workflow.Create<LeaseData>("CompletionRace").Step<NoopActivity>()
                    .Suspend<LeaseEvent>("completion").Build(), new LeaseData(),
                new WorkflowOptions { InstanceId = instanceId });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        }
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            CompleteOnCancellationRequest = true
        };
        using var host = CreateHost(repository, new WorkflowCatalog(), new ActivityProbe());

        var result = await host.GetRequiredService<IWorkflowClient>()
            .CancelAsync(instanceId, new CancellationReason { ReasonCode = "late" });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await inner.GetWorkflowInstanceAsync(instanceId))!.Status);
    }

    [Fact]
    public async Task CallerCancellationLeavesWorkflowRecoverable()
    {
        using var cancellation = new CancellationTokenSource();
        CurrentCancellation.Value = cancellation;
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("CancelDuringBranch")
            .Step<RecordActivity>()
            .If(_ => CancelAndTrue(),
                then => then.Step<RecordActivity>())
            .Build();

        var engine = host.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.RunAsync(definition, new LeaseData(),
                    new WorkflowOptions { InstanceId = instanceId }, cancellation.Token));
        }
        finally
        {
            CurrentCancellation.Value = null;
        }

        Assert.Equal(WorkflowStatus.Running,
            (await repository.GetWorkflowInstanceAsync(instanceId))!.Status);
        Assert.Equal(1, probe.Recorded);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.RecoverAsync(instanceId)).Status);
        Assert.Equal(2, probe.Recorded);
    }

    private static readonly AsyncLocal<CancellationTokenSource?> CurrentCancellation = new();

    private static bool CancelAndTrue()
    {
        CurrentCancellation.Value?.Cancel();
        return true;
    }

    [Fact]
    public async Task CancellationRequestWinsOverAnEventArrivingAtAnIdleWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("CancelBeforeResume")
            .Step<NoopActivity>()
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>()
            .Build();
        var engine = host.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new LeaseData());
        Assert.True(await repository.RequestCancellationAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" }));

        var resumed = await engine.ResumeAsync(started.InstanceId, "approval", new LeaseEvent());

        Assert.Equal(WorkflowExecutionStatus.Cancelled, resumed.Status);
        Assert.Equal(0, probe.Recorded);
        Assert.Equal(WorkflowStatus.Cancelled,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Fact]
    public async Task MemoryCompletionWinsCancellationArrivingAfterWorkFinished()
    {
        await VerifyTerminalCommitWinsLateCancellation(
            new InMemoryWorkflowStateRepository(), WorkflowStatus.Completed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlCompletionWinsCancellationArrivingAfterWorkFinished() => VerifyTerminalCommitWinsLateCancellation(SqlRepository(), WorkflowStatus.Completed);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCompletionWinsCancellationArrivingAfterWorkFinished() => VerifyTerminalCommitWinsLateCancellation(PostgreSqlRepository(), WorkflowStatus.Completed);

    [Fact]
    [Trait("Spec", "C-11")]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteCompletionWinsCancellationArrivingAfterWorkFinished()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyTerminalCommitWinsLateCancellation(repository, WorkflowStatus.Completed);
    }

    [Fact]
    public async Task MemoryFailureWinsCancellationArrivingAfterWorkFinished() =>
        await VerifyTerminalCommitWinsLateCancellation(
            new InMemoryWorkflowStateRepository(), WorkflowStatus.Failed);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlFailureWinsCancellationArrivingAfterWorkFinished() =>
        await VerifyTerminalCommitWinsLateCancellation(SqlRepository(), WorkflowStatus.Failed);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlFailureWinsCancellationArrivingAfterWorkFinished() =>
        await VerifyTerminalCommitWinsLateCancellation(PostgreSqlRepository(), WorkflowStatus.Failed);

    [Fact]
    [Trait("Spec", "C-11")]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteFailureWinsCancellationArrivingAfterWorkFinished()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyTerminalCommitWinsLateCancellation(repository, WorkflowStatus.Failed);
    }

    private static async Task VerifyTerminalCommitWinsLateCancellation(
        IWorkflowStateRepository repository, WorkflowStatus terminalStatus)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            await repository.SeedAsync(new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "CancellationCommitFence",
                Status = WorkflowStatus.Running
            });
            var epoch = await repository.TryAcquireExecutionLeaseAsync(instanceId, owner,
                TimeSpan.FromSeconds(10));
            Assert.NotNull(epoch);
            var active = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(active, epoch.Epoch, active.Revision,
                    Guid.NewGuid().ToString("N")))).Status);
            Assert.True(await repository.RequestCancellationAsync(instanceId,
                new CancellationReason { ReasonCode = "operator" }));
            var terminal = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            terminal.Status = terminalStatus;

            var result = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(terminal, epoch.Epoch,
                terminal.Revision,
                Guid.NewGuid().ToString("N")));

            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
            Assert.Equal(terminalStatus,
                (await repository.GetWorkflowInstanceAsync(instanceId))!.Status);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    public async Task CancellationArrivingAtTerminalCommitDoesNotRelabelCompletedWork()
    {
        var inner = new InMemoryWorkflowStateRepository();
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            CancelBeforeTerminalCommit = 1
        };
        using var host = CreateHost(repository, new WorkflowCatalog(), new ActivityProbe());
        var definition = Workflow.Create<LeaseData>("CancelAtTerminalCommit")
            .Step<NoopActivity>()
            .Build();
        var engine = host.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");

        var completed = await engine.RunAsync(definition, new LeaseData(),
            new WorkflowOptions { InstanceId = instanceId });

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await inner.GetWorkflowInstanceAsync(instanceId))!.Status);
    }

    [Fact]
    public async Task CancellationAtFailureCommitDoesNotStrandLocalInstance()
    {
        var inner = new InMemoryWorkflowStateRepository();
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            CancelBeforeFailedCommit = 1
        };
        using var host = CreateHost(repository, new WorkflowCatalog(), new ActivityProbe());
        var definition = Workflow.Create<LeaseData>("CancelAtFailureCommit")
            .Step<FailingActivity>()
            .Build();

        var result = await host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(WorkflowStatus.Failed,
            (await inner.GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    public async Task CancellationSignalAfterRootCompletionDoesNotSpinTheRunner()
    {
        var inner = new InMemoryWorkflowStateRepository();
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            CancelWhenRootCompletedOnRead = 1
        };
        using var host = CreateHost(repository, new WorkflowCatalog(), new ActivityProbe());
        var definition = Workflow.Create<LeaseData>("CancelAfterRootFinished")
            .Step<NoopActivity>()
            .Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData(), cancellationToken: timeout.Token);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Fact]
    public async Task CancellationStoreReadFailureDoesNotInterruptLocalExecution()
    {
        var inner = new InMemoryWorkflowStateRepository();
        var repository = new PausedReadRepository(inner)
        {
            PauseFirstRead = false,
            FailAllCancellationReads = true
        };
        using var host = CreateHost(repository, new WorkflowCatalog(), new ActivityProbe());
        var definition = Workflow.Create<LeaseData>("CancellationReadFailure")
            .Step<NoopActivity>()
            .Build();
        var engine = host.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");

        var completed = await engine.RunAsync(definition, new LeaseData(),
            new WorkflowOptions { InstanceId = instanceId });

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await inner.GetWorkflowInstanceAsync(instanceId))!.Status);
    }

    [Fact]
    public async Task OneCancellationReadFailureDuringActivityDoesNotDiscardItsResult()
    {
        var repository = new PausedReadRepository(new InMemoryWorkflowStateRepository())
        {
            PauseFirstRead = false
        };
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("CancellationReadDuringActivity")
            .Step<BlockingActivity>()
            .Step<RecordActivity>()
            .Build();
        var executing = host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.FailAllCancellationReads = true;
        probe.Release.TrySetResult();

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, probe.Recorded);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlCancellationRequestReachesRunningOwnerOnAnotherHost() => VerifyCancellationRequestReachesRunningOwnerOnAnotherHost(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCancellationRequestReachesRunningOwnerOnAnotherHost() => VerifyCancellationRequestReachesRunningOwnerOnAnotherHost(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlSuspendedWaitCanBeCancelledByAnotherHost() => VerifySuspendedWaitCanBeCancelledByAnotherHost(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlSuspendedWaitCanBeCancelledByAnotherHost() => VerifySuspendedWaitCanBeCancelledByAnotherHost(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlScannerCompletesCancellationAfterRequestingHostStops() => VerifyScannerCompletesCancellationAfterRequestingHostStops(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlScannerCompletesCancellationAfterRequestingHostStops() => VerifyScannerCompletesCancellationAfterRequestingHostStops(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlScannerDoesNotRepeatCancellationWhileFinallyWaits() => VerifyScannerDoesNotRepeatCancellationWhileFinallyWaits(SqlRepository());

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlScannerDoesNotRepeatCancellationWhileFinallyWaits() => VerifyScannerDoesNotRepeatCancellationWhileFinallyWaits(PostgreSqlRepository());

    [Fact]
    public async Task LiveOwnerCannotBeRecoveredByAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(repository, registry, probe);
        var definition = Workflow.Create<LeaseData>("LiveOwner")
            .Step<BlockingActivity>()
            .Step<RecordActivity>()
            .Build();

        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Running], Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));

        try
        {
            var attempted = await secondHost.GetRequiredService<IWorkflowClient>()
                .RecoverAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
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
        var time = new TimerRecordingClock();
        var repository = new InMemoryWorkflowStateRepository(time);
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe, shortLease: true, time);
        using var secondHost = CreateHost(repository, registry, probe, shortLease: true, time);
        var definition = Workflow.Create<LeaseData>("LongOwner")
            .Step<BlockingActivity>()
            .Step<RecordActivity>()
            .Build();

        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Running], Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        try
        {
            // Three lease lengths: a lease that was not renewed would have expired.
            await AdvanceInStepsAsync(time, TimeSpan.FromMilliseconds(600));
            var attempted = await secondHost.GetRequiredService<IWorkflowClient>()
                .RecoverAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task OneCancellationReadErrorDoesNotForfeitAnActiveLease()
    {
        var time = new TimerRecordingClock();
        var repository = new PausedReadRepository(new InMemoryWorkflowStateRepository(time))
        {
            PauseFirstRead = false
        };
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe, shortLease: true, time);
        var definition = Workflow.Create<LeaseData>("TransientCancellationRead")
            .Step<BlockingActivity>()
            .Build();
        var executing = host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.FailNextCancellationReads = 1;
        await AdvanceUntilAsync(time, repository.CancellationReadFailed.Task);
        probe.Release.TrySetResult();

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task ObservedCancellationSurvivesLaterCancellationStoreReadsFailing()
    {
        var repository = new PausedReadRepository(new InMemoryWorkflowStateRepository())
        {
            PauseFirstRead = false,
            FailAfterObservedCancellation = true
        };
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe,
            shortLease: true);
        var definition = Workflow.Create<LeaseData>("ObservedCancellationWithStoreOutage")
            .Step<CancellableBlockingActivity>().Build();
        var engine = host.GetRequiredService<IWorkflowClient>();
        var executing = engine.RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var instance = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Running], Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        await engine.CancelAsync(instance.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        await repository.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var result = await executing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        }
        finally
        {
            repository.FailAfterObservedCancellation = false;
            await executing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OneRenewalErrorDoesNotForfeitAnActiveLease()
    {
        var time = new TimerRecordingClock();
        var repository = new PausedReadRepository(new InMemoryWorkflowStateRepository(time))
        {
            PauseFirstRead = false
        };
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe, shortLease: true, time);
        var definition = Workflow.Create<LeaseData>("TransientRenewal")
            .Step<BlockingActivity>()
            .Build();
        var executing = host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.FailNextRenewals = 1;
        await AdvanceUntilAsync(time, repository.RenewalFailed.Task);
        probe.Release.TrySetResult();

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task LostLeaseWaitsForAnInFlightActivityToStop()
    {
        var time = new TimerRecordingClock();
        var repository = new PausedReadRepository(new InMemoryWorkflowStateRepository(time))
        {
            PauseFirstRead = false
        };
        var probe = new ActivityProbe();
        using var host = CreateHost(repository, new WorkflowCatalog(), probe, shortLease: true, time);
        var definition = Workflow.Create<LeaseData>("LostLeaseActivity")
            .Step<BlockingActivity>()
            .Build();
        var executing = host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.LoseNextRenewal = 1;
        await AdvanceUntilAsync(time, repository.LeaseLost.Task);
        try
        {
            await Task.Delay(150);
            Assert.False(executing.IsCompleted);
        }
        finally
        {
            probe.Release.TrySetResult();
        }

        Assert.Equal(WorkflowExecutionStatus.Running,
            (await executing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task TheEventThatArrivedFirstIsTakenWhicheverHostRunsIt()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var staleReader = new PausedReadRepository(repository);
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(staleReader, registry, probe);
        var definition = Workflow.Create<LeaseData>("StaleResume")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>()
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var staleResume = secondHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "reply", new LeaseEvent());
        await staleReader.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The stale host stored its event first, so the run on the first host takes that one and
        // refuses its own, which arrived after the wait was answered (D-33).
        var later = await firstHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "reply", new LeaseEvent());
        Assert.Equal(WorkflowExecutionStatus.Success, later.Status);
        EventAssert.Rejected(later, EventRejection.NoOpenWait);
        staleReader.Continue.TrySetResult();

        var earlier = await staleResume.WaitAsync(TimeSpan.FromSeconds(5));
        EventAssert.Consumed(earlier);
        Assert.Equal(WorkflowExecutionStatus.Success, earlier.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Equal(2, probe.Recorded);
    }

    [Fact]
    public async Task ConcurrentResumesOfDifferentWaitsBothApply()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var staleReader = new PausedReadRepository(repository);
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(repository, registry, probe);
        using var secondHost = CreateHost(staleReader, registry, probe);
        var definition = Workflow.Create<LeaseData>("ConcurrentWaits")
            .Step<RecordActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<LeaseEvent>("first"))
                .Do(branch => branch.WaitFor<LeaseEvent>("second")))
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var secondResume = secondHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "second", new LeaseEvent());
        await staleReader.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The first host's run takes both events: the second was stored before it ran.
        var first = await firstHost.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(started.InstanceId, "first", new LeaseEvent());
        EventAssert.Consumed(first);
        staleReader.Continue.TrySetResult();

        var second = await secondResume.WaitAsync(TimeSpan.FromSeconds(5));
        EventAssert.Consumed(second);
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
    }

    [Fact]
    public async Task LeaseReleaseFailureDoesNotHideCommittedCompletion()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var releaseFault = new PausedReadRepository(repository) { FailRelease = true };
        var probe = new ActivityProbe();
        using var host = CreateHost(releaseFault, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("ReleaseFault")
            .Step<RecordActivity>()
            .Build();

        var completed = await host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(completed.InstanceId))!.Status);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlSuspendedInstanceCanResumeOnAnotherHost() => VerifySuspendedInstanceCanResumeOnAnotherHost(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlSuspendedInstanceCanResumeOnAnotherHost() => VerifySuspendedInstanceCanResumeOnAnotherHost(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlLiveOwnerCannotBeRecoveredByAnotherHost() => VerifyLiveOwnerCannotBeRecoveredByAnotherHost(SqlRepository);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLiveOwnerCannotBeRecoveredByAnotherHost() => VerifyLiveOwnerCannotBeRecoveredByAnotherHost(PostgreSqlRepository);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlRecoveryScannerReclaimsAnExpiredWorker() => VerifyRecoveryScannerReclaimsAnExpiredWorker(SqlRepository, () => new SqlConnection(SqlTestDatabase<ExecutionLeaseContractTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRecoveryScannerReclaimsAnExpiredWorker() => VerifyRecoveryScannerReclaimsAnExpiredWorker(PostgreSqlRepository, () => new NpgsqlConnection(PostgreSqlTestDatabase<ExecutionLeaseContractTests>.ConnectionString));

    private static ServiceProvider CreateHost(IWorkflowStateRepository repository,
        IWorkflowCatalog registry, ActivityProbe probe, bool shortLease = false, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton(repository));
        if (time != null)
            services.Replace(ServiceDescriptor.Singleton(time));
        services.Replace(ServiceDescriptor.Singleton(registry));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        if (shortLease)
            services.AddSingleton(new ExecutionLeaseSettings(
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)));
        services.AddSingleton(probe);
        services.AddTransient<BlockingActivity>();
        services.AddTransient<CancellableBlockingActivity>();
        services.AddTransient<RecordActivity>();
        services.AddTransient<NoopActivity>();
        return services.BuildServiceProvider();
    }

    private static SqlWorkflowStateRepository SqlRepository() => new(
        SqlTestDatabase<ExecutionLeaseContractTests>.ConnectionString);

    private static PostgreSqlWorkflowStateRepository PostgreSqlRepository() => new(
        PostgreSqlTestDatabase<ExecutionLeaseContractTests>.ConnectionString);

    // Lets time pass for a store: a fake clock is advanced; SQL Server's own clock needs real time.
    private static Task PassAsync(FakeTimeProvider? time, TimeSpan by)
    {
        if (time == null)
            return Task.Delay(by);
        time.Advance(by);
        return Task.CompletedTask;
    }

    // One renewal interval of the short lease per step. The lease's renewal and the host's cancellation watch
    // each keep a timer set at most one step ahead, and set it again once their store call is done; the clock
    // moves only when both are set, so each step's renewal and read are done before the next.
    private static readonly TimeSpan ShortLeaseStep = TimeSpan.FromMilliseconds(50);

    private static Task ShortLeaseTimersSetAsync(TimerRecordingClock time, CancellationToken cancellationToken = default) =>
        time.WaitUntilAsync(timers => timers.Count(timer => timer.Pending && timer.DueTime <= ShortLeaseStep) >= 2,
            "the short lease's renewal and cancellation watch timers", cancellationToken: cancellationToken);

    private static async Task AdvanceInStepsAsync(TimerRecordingClock time, TimeSpan by)
    {
        for (var moved = TimeSpan.Zero; moved < by; moved += ShortLeaseStep)
        {
            await ShortLeaseTimersSetAsync(time);
            time.Advance(ShortLeaseStep);
        }
        await ShortLeaseTimersSetAsync(time);
    }

    // Moves the clock in the same steps until the run has done what the test waits for, within five short leases.
    private static async Task AdvanceUntilAsync(TimerRecordingClock time, Task happened)
    {
        for (var moved = TimeSpan.Zero; ; moved += ShortLeaseStep)
        {
            using var stop = new CancellationTokenSource();
            var timersSet = ShortLeaseTimersSetAsync(time, stop.Token);
            await Task.WhenAny(happened, timersSet);
            if (happened.IsCompleted)
            {
                stop.Cancel();
                await timersSet.ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }
            await timersSet;
            Assert.True(moved < TimeSpan.FromSeconds(1), "It did not happen within five leases");
            time.Advance(ShortLeaseStep);
        }
    }

    private static async Task VerifyExclusiveLease(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
            Assert.Null(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyExpiredLease(IWorkflowStateRepository repository, FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromMilliseconds(100)));
            await PassAsync(time, TimeSpan.FromMilliseconds(300));
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
            Assert.False(await repository.RenewExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // The lease is renewed before it expires, and checked after the first lease would have expired but
    // before the renewed one does.
    private static async Task VerifyRenewal(IWorkflowStateRepository repository, TimeSpan lease,
        TimeSpan renewAfter, TimeSpan checkAfter, FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first", lease));
            await PassAsync(time, renewAfter);
            Assert.True(await repository.RenewExecutionLeaseAsync(instanceId, "first", lease));
            await PassAsync(time, checkAfter);
            Assert.Null(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second", lease));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // The first owner holds its lease for the given length; the second takes over once takeoverAfter has passed.
    private static async Task VerifyFencing(IWorkflowStateRepository repository, TimeSpan lease,
        TimeSpan takeoverAfter, FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var firstEpoch = await repository.TryAcquireExecutionLeaseAsync(instanceId, "first", lease);
            Assert.NotNull(firstEpoch);
            var oldOwner = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "LeaseFencing",
                Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.Inline("{}")
            };
            var initial = await repository.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(oldOwner, firstEpoch.Epoch, 0, "initial"));
            Assert.Equal(WorkflowCommitStatus.Applied, initial.Status);
            await PassAsync(time, takeoverAfter);
            var secondEpoch = await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5));
            Assert.NotNull(secondEpoch);
            oldOwner.WorkflowDataJson = StoredJson.Inline("\"stale\"");
            var stale = await repository.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(oldOwner, firstEpoch.Epoch, 1, "stale"));
            Assert.Equal(WorkflowCommitStatus.StaleEpoch, stale.Status);
            var newOwner = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            newOwner.WorkflowDataJson = StoredJson.Inline("\"current\"");
            var current = await repository.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(newOwner, secondEpoch.Epoch, 1, "current"));
            Assert.Equal(WorkflowCommitStatus.Applied, current.Status);
            Assert.Equal("\"current\"",
                (await repository.GetWorkflowInstanceAsync(instanceId))!.WorkflowDataJson.InlineText);
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
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(5)));
            Assert.False(await repository.ReleaseExecutionLeaseAsync(instanceId, "second"));
            Assert.Null(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
            Assert.True(await repository.ReleaseExecutionLeaseAsync(instanceId, "first"));
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyReceiptFencing(IWorkflowStateRepository repository,
        FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            // Long enough for the first commit on a cold store connection; the takeover waits it out.
            var formerEpoch = await repository.TryAcquireExecutionLeaseAsync(instanceId, "first",
                TimeSpan.FromSeconds(1));
            Assert.NotNull(formerEpoch);
            var formerOwner = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "ReceiptFencing",
                Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.Inline("{}")
            };
            var start = CheckpointCommit.Create(formerOwner, formerEpoch.Epoch, 0, "start");
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(start)).Status);
            await PassAsync(time, TimeSpan.FromMilliseconds(1_200));
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "second",
                TimeSpan.FromSeconds(5)));

            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await repository.CommitWorkflowInstanceAsync(start)).Status);
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
        using var host = CreateHost(repository, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("IdempotentStart")
            .Step<RecordActivity>()
            .Build();
        try
        {
            var first = await host.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new LeaseData(), new WorkflowOptions { InstanceId = instanceId });
            var replay = await host.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new LeaseData(), new WorkflowOptions { InstanceId = instanceId });

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

    private static async Task VerifyRecoveryQuery(IWorkflowStateRepository repository, FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            await repository.SeedAsync(new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "RecoveryQuery",
                Status = WorkflowStatus.Running
            });
            Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                instance => instance.InstanceId == instanceId);
            // Long enough for the next query on a slow store; the expiry is then polled for, and must come within
            // the lease's length, with three seconds more for a slow store.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2) + TimeSpan.FromSeconds(3);
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "worker",
                TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(100),
                instance => instance.InstanceId == instanceId);
            if (time != null)
            {
                // On a fake clock the lease has expired once the clock has moved its length.
                time.Advance(TimeSpan.FromSeconds(2));
                Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                    instance => instance.InstanceId == instanceId);
                return;
            }
            while (!(await repository.GetRunnableInstancesAsync(100)).Any(
                       instance => instance.InstanceId == instanceId))
            {
                Assert.True(DateTime.UtcNow < deadline, "The expired lease was not found on time");
                await Task.Delay(50);
            }
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    public sealed class LeaseData;
    public sealed class LeaseEvent;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class FailingActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated activity failure");
    }

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
        public IPayloadStore PayloadStore => inner.PayloadStore;
        public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) =>
            inner.GetDefinitionAsync(name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
            inner.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
            inner.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
            inner.GetWorkflowVersionAsync(name, version);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
            inner.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) =>
            inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null) =>
            inner.PublishDefinitionAsync(draftId, version, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
            inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.OpenAsync(digest, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(digest, cancellationToken);

        public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
            inner.RegisterMemberAsync(member, cancellationToken);
        public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
            CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
        public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
            inner.UnregisterMemberAsync(memberId, cancellationToken);
        public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
            inner.GetLiveMembersAsync(cancellationToken);

        public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
            CancellationToken cancellationToken = default) =>
            inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
        public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
            inner.GetTombstoneAsync(instanceId);
        public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
            inner.AdmitWorkflowInstanceAsync(commit, admission);
        public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
            inner.GetWorkflowOwnershipAsync(instanceId);
        public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
            inner.GetWorkflowAdmissionAsync(sender, trigger, key);

        public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
            inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
        public Task UnregisterPublisherAsync(string name, string actor = "host") => inner.UnregisterPublisherAsync(name, actor);
        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => inner.ListDeliveriesAsync(query);
        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => inner.RetryDeliveryAsync(eventId, publisherName, actor);
        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => inner.ListPublishersAsync();
        public Task<bool> EnablePublisherAsync(string publisherName, string actor) => inner.EnablePublisherAsync(publisherName, actor);
        public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
            inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
        public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
            inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
        public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
            inner.SettleDeliveryAsync(eventId, publisherName, token, result);
        public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
            inner.GetDeliveryAsync(eventId, publisherName);
        public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
            inner.GetPublisherAsync(publisherName);

        public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
            inner.AppendEventAsync(workflowEvent);
        public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
            inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
        public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
            inner.GetEventAsync(instanceId, eventId);
        public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
            inner.GetWaitAsync(instanceId, waitId);
        public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
            IReadOnlyCollection<string> instanceIds) =>
            inner.GetInboxSequencesAsync(instanceIds);
        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
            inner.GetOpenWaitsAsync(query);

        public int FailNextCancellationReads;
        public int FailNextRenewals;
        public int LoseNextRenewal;
        public bool FailAllCancellationReads;
        public bool FailAfterObservedCancellation;
        private int _cancellationObserved;
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelBeforeTerminalCommit;
        public int CancelBeforeFailedCommit;
        public int CancelWhenRootCompletedOnRead;
        public TaskCompletionSource CancellationReadFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RenewalFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LeaseLost { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompleteOnCancellationRequest { get; set; }
        public bool PauseFirstRead { get; set; } = true;
        public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null)
        {
            var accepted = await inner.RequestCancellationAsync(instanceId, reason, expectedIncarnation);
            if (accepted && CompleteOnCancellationRequest)
            {
                var instance = (await inner.GetWorkflowInstanceAsync(instanceId))!;
                instance.Status = WorkflowStatus.Completed;
                await inner.SeedAsync(instance);
            }
            return accepted;
        }
        public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
        {
            await BeforeCancellationReadAsync(instanceId);
            var reason = await inner.GetCancellationRequestAsync(instanceId);
            ObserveCancellation(reason);
            return reason;
        }

        private async Task BeforeCancellationReadAsync(string instanceId)
        {
            if (FailAfterObservedCancellation && Volatile.Read(ref _cancellationObserved) != 0)
                throw new IOException("Cancellation store unavailable after observation");
            if (FailAllCancellationReads)
                throw new IOException("Cancellation store unavailable");
            if (Interlocked.Exchange(ref FailNextCancellationReads, 0) > 0)
            {
                CancellationReadFailed.TrySetResult();
                throw new IOException("Cancellation read unavailable");
            }
            if (Volatile.Read(ref CancelWhenRootCompletedOnRead) > 0)
            {
                var saved = await inner.GetWorkflowInstanceAsync(instanceId);
                if (saved?.Status == WorkflowStatus.Running &&
                    ExecutionCheckpoint.IsStructured(saved.ExecutionStateJson) &&
                    ExecutionCheckpoint.Read(saved.ExecutionStateJson).Continuations[0].Status ==
                        ContinuationStatus.Completed &&
                    Interlocked.Exchange(ref CancelWhenRootCompletedOnRead, 0) > 0)
                    await inner.RequestCancellationAsync(instanceId,
                        new CancellationReason { ReasonCode = "late" });
            }
        }

        private void ObserveCancellation(CancellationReason? reason)
        {
            if (reason != null && FailAfterObservedCancellation)
            {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                CancellationObserved.TrySetResult();
            }
        }
        public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
            inner.GetRunnableInstancesAsync(limit);
        private int _reads;
        public bool FailRelease { get; set; }
        public TaskCompletionSource ReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
            inner.GetWorkflowInstanceSummaryAsync(instanceId);
        public async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds)
        {
            foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
                await BeforeCancellationReadAsync(instanceId);
            var controls = await inner.GetControlRequestsAsync(instanceIds);
            foreach (var reason in controls.Cancellations.Values)
                ObserveCancellation(reason);
            return controls;
        }
        public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
            inner.RequestDebugCommandAsync(instanceId, command);
        public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
            inner.GetDebugStatesAsync(instanceIds);

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

        public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string id, string token, TimeSpan duration) =>
            inner.TryAcquireExecutionLeaseAsync(id, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string id, string token, TimeSpan duration)
        {
            if (Interlocked.Exchange(ref LoseNextRenewal, 0) > 0)
            {
                LeaseLost.TrySetResult();
                return Task.FromResult(false);
            }
            if (Interlocked.Exchange(ref FailNextRenewals, 0) > 0)
            {
                RenewalFailed.TrySetResult();
                throw new IOException("Lease renewal unavailable");
            }
            return inner.RenewExecutionLeaseAsync(id, token, duration);
        }
        public Task<bool> ReleaseExecutionLeaseAsync(string id, string token) =>
            FailRelease ? throw new IOException("Release acknowledgment unavailable") :
                inner.ReleaseExecutionLeaseAsync(id, token);
        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadTrunk();
            if (instance.Status == WorkflowStatus.Completed &&
                Interlocked.Exchange(ref CancelBeforeTerminalCommit, 0) > 0)
                await inner.RequestCancellationAsync(instance.InstanceId,
                    new CancellationReason { ReasonCode = "operator" });
            if (instance.Status == WorkflowStatus.Failed &&
                Interlocked.Exchange(ref CancelBeforeFailedCommit, 0) > 0)
                await inner.RequestCancellationAsync(instance.InstanceId,
                    new CancellationReason { ReasonCode = "operator" });
            return await inner.CommitWorkflowInstanceAsync(commit);
        }

        public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
            inner.QueryInstancesAsync(query);

        public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
            inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation);
        public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsAsync(string instanceId) =>
            inner.GetDiagnosticsAsync(instanceId);
        public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation) =>
            inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
            int limit = 1000) =>
            inner.GetTraceAsync(instanceId, afterSequence, limit);
        public Task DeleteWorkflowInstanceAsync(string id, TimeSpan? tombstoneRetention = null) => inner.DeleteWorkflowInstanceAsync(id, tombstoneRetention);
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteConcurrentFirstLeaseHasOneOwner()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyConcurrentFirstLeaseHasOneOwner(() => repository);
    }

    private static async Task VerifyConcurrentFirstLeaseHasOneOwner(Func<IWorkflowStateRepository> newRepository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repositories = Enumerable.Range(0, 8).Select(_ => newRepository()).ToArray();
        try
        {
            var attempts = repositories.Select(async (repository, index) =>
            {
                await start.Task;
                return await repository.TryAcquireExecutionLeaseAsync(instanceId,
                    $"host-{index}", TimeSpan.FromSeconds(10));
            }).ToArray();
            start.SetResult();

            Assert.Single((await Task.WhenAll(attempts)).Where(epoch => epoch != null));
        }
        finally
        {
            await repositories[0].DeleteWorkflowInstanceAsync(instanceId);
        }
    }


    private static async Task VerifyCancellationRequestReachesRunningOwnerOnAnotherHost(Func<IWorkflowStateRepository> newRepository)
    {
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, registry, probe);
        using var secondHost = CreateHost(secondRepository, registry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCancelRunning")
            .Step<CancellableBlockingActivity>()
            .Step<RecordActivity>()
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData(),
                new WorkflowOptions { InstanceId = instanceId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowStatus.Running,
            (await firstRepository.GetWorkflowInstanceAsync(instanceId))!.Status);
        try
        {
            var requested = await secondHost.GetRequiredService<IWorkflowClient>()
                .CancelAsync(instanceId, new CancellationReason
                {
                    ReasonCode = "operator", Description = "Stopped by operator"
                });
            Assert.Equal(WorkflowExecutionStatus.Running, requested.Status);
            Assert.Equal(WorkflowExecutionStatus.Cancelled,
                (await executing.WaitAsync(TimeSpan.FromSeconds(30))).Status);
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


    private static async Task VerifySuspendedWaitCanBeCancelledByAnotherHost(Func<IWorkflowStateRepository> newRepository)
    {
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        var registry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, registry, probe);
        using var secondHost = CreateHost(secondRepository, registry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCancelWait")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("approval")
            .Step<RecordActivity>()
            .Build();
        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        try
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            var result = await secondHost.GetRequiredService<IWorkflowClient>()
                .CancelAsync(started.InstanceId, new CancellationReason
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


    private static async Task VerifyScannerCompletesCancellationAfterRequestingHostStops(Func<IWorkflowStateRepository> newRepository)
    {
        var firstRepository = newRepository();
        var firstRegistry = new WorkflowCatalog();
        var secondRegistry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        var definition = Workflow.Create<LeaseData>("SqlCancelAfterHostStop")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("approval")
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        WorkflowExecutionResult started;
        try
        {
            await using (var firstHost = CreateHost(firstRepository, firstRegistry, probe))
            {
                started = await firstHost.GetRequiredService<IWorkflowClient>()
                    .RunAsync(definition, new LeaseData());
            }
        }
        finally
        {
            if (firstRepository is IAsyncDisposable firstOwner)
                await firstOwner.DisposeAsync();
        }
        var secondRepository = newRepository();
        try
        {
            Assert.True(await secondRepository.RequestCancellationAsync(started.InstanceId,
                new CancellationReason { ReasonCode = "withdrawn" }));
            await using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
            using var scanner = new WorkflowRecoveryService(
                secondHost.GetRequiredService<IServiceScopeFactory>(), secondRepository,
                NullLogger<WorkflowRecoveryService>.Instance);

            await scanner.RecoverOnceAsync(CancellationToken.None);

            Assert.Equal(WorkflowStatus.Cancelled,
                (await secondRepository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        }
        finally
        {
            try
            {
                await secondRepository.DeleteWorkflowInstanceAsync(started.InstanceId);
            }
            finally
            {
                if (secondRepository is IAsyncDisposable secondOwner)
                    await secondOwner.DisposeAsync();
            }
        }
    }


    private static async Task VerifySuspendedInstanceCanResumeOnAnotherHost(Func<IWorkflowStateRepository> newRepository)
    {
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        var firstRegistry = new WorkflowCatalog();
        var secondRegistry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("SqlCrossHostWait")
            .Step<RecordActivity>()
            .WaitFor<LeaseEvent>("reply")
            .Step<RecordActivity>()
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var started = await firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        try
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            var resumed = await secondHost.GetRequiredService<IWorkflowClient>()
                .ResumeAsync(started.InstanceId, "reply", new LeaseEvent());
            Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
            Assert.Equal(2, probe.Recorded);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(started.InstanceId);
        }
    }


    private static async Task VerifyLiveOwnerCannotBeRecoveredByAnotherHost(Func<IWorkflowStateRepository> newRepository)
    {
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        var firstRegistry = new WorkflowCatalog();
        var secondRegistry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("SqlLiveOwner")
            .Step<BlockingActivity>()
            .Step<RecordActivity>()
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var correlationId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData(),
                new WorkflowOptions { CorrelationId = correlationId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await Task.WhenAll((await firstRepository.QueryInstancesAsync(new InstanceQuery(CorrelationId: correlationId, Limit: int.MaxValue))).Items.Select(async row => (await firstRepository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        WorkflowExecutionResult attempted;
        try
        {
            attempted = await secondHost.GetRequiredService<IWorkflowClient>()
                .RecoverAsync(saved.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
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


    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteScannerDoesNotRepeatCancellationWhileFinallyWaits()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString);
        await VerifyScannerDoesNotRepeatCancellationWhileFinallyWaits(repository);
    }

    private static async Task VerifyScannerDoesNotRepeatCancellationWhileFinallyWaits(IWorkflowStateRepository repository)
    {
        var probe = new ActivityProbe();
        await using var host = CreateHost(repository, new WorkflowCatalog(), probe);
        var definition = Workflow.Create<LeaseData>("SqlCancelFinallyWait")
            .Step<RecordActivity>()
            .Try(body => body.WaitFor<LeaseEvent>("approval"))
            .Finally(body => body.WaitFor<LeaseEvent>("cleanup"))
            .Build();
        var started = await host.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData());
        try
        {
            Assert.True(await repository.RequestCancellationAsync(started.InstanceId,
                new CancellationReason { ReasonCode = "withdrawn" }));
            using var scanner = new WorkflowRecoveryService(
                host.GetRequiredService<IServiceScopeFactory>(), repository,
                NullLogger<WorkflowRecoveryService>.Instance);
            await scanner.RecoverOnceAsync(CancellationToken.None);
            var waiting = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
            Assert.Equal(WorkflowStatus.Suspended, waiting.Status);
            Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(100),
                item => item.InstanceId == started.InstanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(started.InstanceId);
        }
    }


    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public Task SqliteScannerCompletesCancellationAfterRequestingHostStops() => VerifyScannerCompletesCancellationAfterRequestingHostStops(() => new SqliteWorkflowStateRepository(SqliteTestDatabase<ExecutionLeaseContractTests>.ConnectionString));


    private static async Task VerifyRecoveryScannerReclaimsAnExpiredWorker(Func<IWorkflowStateRepository> newRepository, Func<System.Data.Common.DbConnection> newConnection)
    {
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        var firstRegistry = new WorkflowCatalog();
        var secondRegistry = new WorkflowCatalog();
        var probe = new ActivityProbe();
        using var firstHost = CreateHost(firstRepository, firstRegistry, probe);
        using var secondHost = CreateHost(secondRepository, secondRegistry, probe);
        var definition = Workflow.Create<LeaseData>("ExpiredWorker")
            .Step<BlockingActivity>()
            .Step<RecordActivity>()
            .Build();
        await secondRegistry.RegisterWorkflowAsync(definition);
        var correlationId = Guid.NewGuid().ToString("N");
        var executing = firstHost.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new LeaseData(),
                new WorkflowOptions { CorrelationId = correlationId });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = Assert.Single(await Task.WhenAll((await firstRepository.QueryInstancesAsync(new InstanceQuery(CorrelationId: correlationId, Limit: int.MaxValue))).Items.Select(async row => (await firstRepository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        try
        {
            await using (var connection = newConnection())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(connection is SqlConnection
                    ? "UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE InstanceId = @InstanceId"
                    : "UPDATE ixiflow_workflow_leases SET expires_at_utc = clock_timestamp() - INTERVAL '1 second' WHERE instance_id = @InstanceId",
                    new { saved.InstanceId });
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


}
