using System.Security.Cryptography;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-10")]
[Trait("Feature", "durability")]
public sealed class IncomingWebhookAdmissionTests
{
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static WorkflowInstance Instance(string? id = null) => new()
    {
        InstanceId = id ?? Guid.NewGuid().ToString("N"), WorkflowName = "Admission", WorkflowVersion = 7,
        Status = WorkflowStatus.Running, WorkflowDataType = typeof(object).AssemblyQualifiedName!,
        WorkflowDataJson = "{}", ExecutionStateJson = StoreTestSupport.CheckpointJson
    };

    private static WorkflowAdmission Request(TimeSpan? retention = null) =>
        new("sender", "stable-trigger", "request-key", Fingerprint, "owner", "tenant", retention);

    [Fact]
    public async Task DuplicateSignedDeliveryDoesNotMakeTheInMemoryInstanceRunnableAgain()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = Instance();
        await repository.SeedAsync(instance);
        var grant = await StoreTestSupport.LeaseAsync(repository, instance.InstanceId);
        var opened = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, instance.Revision,
            parks: true, wakeAfter: TimeSpan.FromHours(1), openedWaits: [new("wait", WaitKind.Event, "approval", "Approved", null)]));
        Assert.Equal(WorkflowCommitStatus.Applied, opened.Status);
        await StoreTestSupport.ReleaseAsync(repository, instance.InstanceId);
        var workflowEvent = new WorkflowEvent(instance.InstanceId, "event", "Approved", "{}", "approval",
            DeliveryIdentity: new string('B', 64), Source: "urn:vendor");
        var first = await repository.AppendEventAsync(workflowEvent);
        Assert.Equal(EventAppendStatus.Stored, first.Status);
        Assert.True(first.MadeRunnable);
        var duplicate = await repository.AppendEventAsync(workflowEvent);
        Assert.Equal(EventAppendStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.Sequence, duplicate.Sequence);
        Assert.False(duplicate.MadeRunnable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDuplicatesCommitExactlyOneRunnableInstanceAndReturnWinner(bool sqlite)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IWorkflowStateRepository repository = sqlite
            ? new SqliteWorkflowStateRepository($"Data Source={Path.Combine(folder, "state.db")}")
            : new InMemoryWorkflowStateRepository();
        try
        {
            var store = (IWorkflowAdmissionStore)repository;
            var commits = Enumerable.Range(0, 16).Select(_ => CheckpointCommit.CreateAdmission(Instance())).ToArray();
            using var barrier = new Barrier(2);
            var first = Task.Run(async () => { barrier.SignalAndWait(); return await store.AdmitWorkflowInstanceAsync(commits[0], Request()); });
            var second = Task.Run(async () => { barrier.SignalAndWait(); return await store.AdmitWorkflowInstanceAsync(commits[1], Request()); });
            var race = await Task.WhenAll(first, second);
            var winner = Assert.Single(race, r => r.Status == WorkflowCommitStatus.Applied).Admission!;
            Assert.All(race, r => Assert.Equal(winner, r.Admission));
            foreach (var commit in commits.Skip(2))
                Assert.Equal(winner, (await store.AdmitWorkflowInstanceAsync(commit, Request())).Admission);
            Assert.Equal(winner.InstanceId, Assert.Single(await repository.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100))).InstanceId);
            var saved = await repository.GetWorkflowInstanceAsync(winner.InstanceId);
            Assert.Equal(1, saved!.Revision);
            Assert.Equal(7, saved.WorkflowVersion);
            Assert.All(commits.Where(c => c.InstanceId != winner.InstanceId), c =>
                Assert.Null(repository.GetWorkflowInstanceAsync(c.InstanceId).GetAwaiter().GetResult()));
            Assert.Equal(new WorkflowOwnership(winner.InstanceId, "sender", "stable-trigger", "owner", "tenant", saved.Incarnation),
                await store.GetWorkflowOwnershipAsync(winner.InstanceId));
        }
        finally
        {
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementAndConflictReturnOriginalRecordWhileExpiryCreatesFreshId(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IWorkflowStateRepository repository = sqlite
            ? new SqliteWorkflowStateRepository($"Data Source={Path.Combine(folder, "state.db")}", time)
            : new InMemoryWorkflowStateRepository(time);
        try
        {
            var store = (IWorkflowAdmissionStore)repository;
            var request = Request(TimeSpan.FromHours(1));
            var firstCommit = CheckpointCommit.CreateAdmission(Instance());
            await store.AdmitWorkflowInstanceAsync(firstCommit, request); // Response deliberately lost; no host has run it.
            var retry = await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance()), request);
            Assert.Equal(firstCommit.InstanceId, retry.Admission!.InstanceId);
            Assert.Equal(time.GetUtcNow().UtcDateTime.AddHours(1), retry.Admission.ExpiresAtUtc);
            var conflict = await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance()),
                request with { Fingerprint = new string('B', 64) });
            Assert.Equal(retry.Admission, conflict.Admission); // Caller compares the winner's fingerprint.
            time.Advance(TimeSpan.FromHours(1));
            await repository.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100);
            var fresh = await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance()),
                request with { Fingerprint = new string('B', 64) });
            Assert.Equal(WorkflowCommitStatus.Applied, fresh.Status);
            Assert.NotEqual(retry.Admission.InstanceId, fresh.Admission!.InstanceId);
            Assert.NotNull(await repository.GetWorkflowInstanceAsync(retry.Admission.InstanceId));
            Assert.NotNull(await store.GetWorkflowOwnershipAsync(retry.Admission.InstanceId));
            await repository.DeleteWorkflowInstanceAsync(retry.Admission.InstanceId, TimeSpan.FromHours(2));
            Assert.NotNull(await store.GetWorkflowOwnershipAsync(retry.Admission.InstanceId));
            time.Advance(TimeSpan.FromHours(2));
            await repository.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100);
            Assert.Null(await store.GetWorkflowOwnershipAsync(retry.Admission.InstanceId));
        }
        finally
        {
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Fact]
    public async Task AdmissionValidationCannotLeaveAKeyReservationOrBypassAnExistingInstance()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var existing = Instance("existing");
        await repository.SeedAsync(existing);
        var conflict = await repository.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance("existing")), Request());
        Assert.Equal(WorkflowCommitStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Admission);
        var accepted = await repository.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance()), Request());
        Assert.Equal(WorkflowCommitStatus.Applied, accepted.Status);
        Assert.Throws<ArgumentException>(() => (Request() with { Fingerprint = "invalid" }).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredTombstonesDoNotAuthorizeTheOldOwnerForAReusedInstanceId(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IWorkflowStateRepository repository = sqlite
            ? new SqliteWorkflowStateRepository($"Data Source={Path.Combine(folder, "state.db")}", time)
            : new InMemoryWorkflowStateRepository(time);
        try
        {
            var store = (IWorkflowAdmissionStore)repository;
            var result = await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(Instance("reused")), Request());
            await repository.DeleteWorkflowInstanceAsync("reused", TimeSpan.FromHours(1));
            time.Advance(TimeSpan.FromHours(1));
            // Deliberately leave expired rows in place, as between retention scans.
            await repository.SeedAsync(Instance("reused"));
            Assert.Null(await store.GetWorkflowOwnershipAsync("reused"));
            Assert.Equal(1, (await repository.GetWorkflowInstanceAsync("reused"))!.Revision);
        }
        finally
        {
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Fact]
    public void SignatureUsesTheOriginalBytesAndChecksBothTimeDirectionsAndEveryRotatedKey()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var body = Encoding.UTF8.GetBytes(" {\"value\":\"é\"} \n");
        var old = Enumerable.Repeat((byte)1, 32).ToArray();
        var current = Enumerable.Repeat((byte)2, 32).ToArray();
        var timestamp = now.ToUnixTimeSeconds().ToString();
        // Independent protocol vector, rather than verifying only with the production signer.
        var signed = Encoding.UTF8.GetBytes("delivery." + timestamp + ".").Concat(body).ToArray();
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(old, signed));
        Assert.Equal(expected, StandardWebhookSignature.Sign(old, "delivery", timestamp, body));
        Assert.True(StandardWebhookSignature.Verify([current, old], "delivery", timestamp, "v2,unsupported " + expected, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([current], "delivery", timestamp, expected, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([old], "delivery", timestamp, expected, Encoding.UTF8.GetBytes("{}"), now, TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([old], "delivery", timestamp, expected, body, now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([old], "delivery", timestamp, expected, body, now.AddMinutes(-6), TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([old], "delivery", "not-a-time", expected, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(StandardWebhookSignature.Verify([old], "delivery", timestamp, "v1,invalid-base64!", body, now, TimeSpan.FromMinutes(5)));
    }
}
