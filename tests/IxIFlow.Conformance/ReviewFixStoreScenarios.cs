using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
public sealed class ReviewFixStoreScenarios
{
    private static readonly string[] ReadNames =
    [
        "event", "wait", "wait-key", "wait-type", "wait-cursor", "delivery-event", "delivery-publisher", "publisher",
        "delivery-filter", "delivery-cursor-event", "delivery-cursor-publisher", "instance-name", "instance-correlation",
        "admission-sender", "admission-trigger", "admission-key", "package-id", "package-version", "packages-id", "package-cursor"
    ];

    public static TheoryData<string, string> Reads
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var profile in StoreProfiles.ProfileNames)
            foreach (var read in ReadNames)
                data.Add(profile, read);

            return data;
        }
    }

    [Theory, MemberData(nameof(Reads))]
    public async Task IdentifierReadsRefusePaddedAndNulAliasesOfSeededRecords(string profile, string read)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var instance = Instance("read-identifiers");
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);

        Assert.Equal(
            WorkflowCommitStatus.Applied,
            (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(
                    instance, grant.Epoch, 0, openedWaits: [new("wait", WaitKind.Event, "key", "type", null)]))).Status);

        Assert.Equal(
            EventAppendStatus.Stored, (await store.AppendEventAsync(new(instance.InstanceId, "event", "type", "{}", "key"))).Status);

        Assert.NotNull(await store.GetEventAsync(instance.InstanceId, "event"));
        Assert.NotNull(await store.GetWaitAsync(instance.InstanceId, "wait"));
        await store.RegisterPublisherAsync("publisher", [OutboxEvent.WorkflowCompleted]);
        var completed = Instance("completed");
        completed.Status = WorkflowStatus.Completed;
        await store.SeedAsync(completed);
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync("publisher", "owner", TimeSpan.FromMinutes(1), 1));
        var admitted = Instance("admitted");

        Assert.Equal(
            WorkflowCommitStatus.Applied, (await store.AdmitWorkflowInstanceAsync(
                CheckpointCommit.CreateAdmission(admitted),
                new("sender", "trigger", "request", new string('a', 64), "owner"))).Status);

        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2, 3]));

        var package = new WorkflowPackage("review.package", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["review.nuspec"] = digest }, []);

        await store.RegisterPackageAsync(package, new([], PackageExtraction.Pending, null, null, []), store);
        Assert.NotNull(await store.GetPackageAsync(package.Id, package.Version));
        var reference = WaitReference.Of(instance.InstanceId, "wait");

        var valid = read switch
        {
            "event" => "event", "wait" => "wait", "wait-key" => "key", "wait-type" => "type", "wait-cursor" => reference,
            "delivery-event" or "delivery-cursor-event" => delivery.Event.Id,
            "delivery-publisher" or "publisher" or "delivery-filter" or "delivery-cursor-publisher" => "publisher",
            "instance-name" => "Review", "instance-correlation" => "correlation", "admission-sender" => "sender",
            "admission-trigger" => "trigger", "admission-key" => "request", "package-version" or "package-cursor" => "1.0.0",
            _ => "review.package"
        };

        await Lookup(valid);

        foreach (var invalid in new[] { " " + valid, valid + " ", valid + "\0suffix" })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Lookup(invalid));

        Assert.NotNull(await store.GetEventAsync(instance.InstanceId, "event"));
        Assert.NotNull(await store.GetWaitAsync(instance.InstanceId, "wait"));
        Assert.Equal(delivery.Event.Id, (await store.GetDeliveryAsync(delivery.Event.Id, "publisher"))!.Event.Id);

        Task Lookup(string value) => read switch
        {
            "event" => store.GetEventAsync(instance.InstanceId, value),
            "wait" => store.GetWaitAsync(instance.InstanceId, value),
            "wait-key" => store.GetOpenWaitsAsync(new(InstanceId: instance.InstanceId, Key: value)),
            "wait-type" => store.GetOpenWaitsAsync(new(InstanceId: instance.InstanceId, EventType: value)),
            "wait-cursor" => store.GetOpenWaitsAsync(new(AfterWaitRef: value)),
            "delivery-event" => store.GetDeliveryAsync(value, "publisher"),
            "delivery-publisher" => store.GetDeliveryAsync(delivery.Event.Id, value),
            "publisher" => store.GetPublisherAsync(value),
            "delivery-filter" => store.ListDeliveriesAsync(new(PublisherName: value)),
            "delivery-cursor-event" => store.ListDeliveriesAsync(new(After: Cursor(value, "publisher"))),
            "delivery-cursor-publisher" => store.ListDeliveriesAsync(new(After: Cursor(delivery.Event.Id, value))),
            "instance-name" => store.QueryInstancesAsync(new(WorkflowName: value)),
            "instance-correlation" => store.QueryInstancesAsync(new(CorrelationId: value)),
            "admission-sender" => store.GetWorkflowAdmissionAsync(value, "trigger", "request"),
            "admission-trigger" => store.GetWorkflowAdmissionAsync("sender", value, "request"),
            "admission-key" => store.GetWorkflowAdmissionAsync("sender", "trigger", value),
            "package-id" => store.GetPackageAsync(value, "1.0.0"),
            "package-version" => store.GetPackageAsync("review.package", value),
            "packages-id" => store.GetPackagesAsync(value),
            _ => store.GetPackagePageAsync("review.package", value, 10)
        };
    }

    public static TheoryData<string, int> Surrogates
    {
        get
        {
            var data = new TheoryData<string, int>();

            foreach (var profile in StoreProfiles.ProfileNames)
            foreach (var form in new[] { 0, 1 })
                data.Add(profile, form);

            return data;
        }
    }

    [Theory, MemberData(nameof(Surrogates))]
    public async Task MalformedSurrogatesAreRefusedAtEveryRuntimeIdentifierBoundary(string profile, int form)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.SeedAsync(Instance("unicode-boundary"));
        var invalid = "invalid" + (form == 0 ? "\uD800" : "\uDC00");
        var valid = new WorkflowEvent("unicode-boundary", "event", "type", "{}", "key", "wait", "subject", Source: "source");

        foreach (var item in new[]
                 {
                     valid with { InstanceId = invalid }, valid with { EventId = invalid }, valid with { EventType = invalid },
                     valid with { Key = invalid }, valid with { TargetWaitId = invalid }, valid with { Subject = invalid },
                     valid with { Source = invalid }
                 })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendEventAsync(item));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetEventAsync(valid.InstanceId, invalid));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetWaitAsync(valid.InstanceId, invalid));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetOpenWaitsAsync(new(Key: invalid)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetOpenWaitsAsync(new(EventType: invalid)));
        var instance = Instance("commit-unicode");
        Assert.ThrowsAny<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0, commitId: invalid));
        instance.CorrelationId = invalid;
        Assert.ThrowsAny<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0));
        instance.CorrelationId = "";

        foreach (var wait in new[]
                 {
                     new OpenedWait(invalid, WaitKind.Event, "key", "type", null),
                     new OpenedWait("wait", WaitKind.Event, invalid, "type", null),
                     new OpenedWait("wait", WaitKind.Event, "key", invalid, null)
                 })
            Assert.ThrowsAny<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0, openedWaits: [wait]));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SupplementaryCharactersRoundTripInRuntimeIdentifiers(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        const string suffix = "\U0001F680";
        var instance = Instance("instance" + suffix);
        instance.CorrelationId = "correlation" + suffix;
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var wait = new OpenedWait("wait" + suffix, WaitKind.Event, "key" + suffix, "type" + suffix, null);

        Assert.Equal(
            WorkflowCommitStatus.Applied,
            (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(instance, grant.Epoch, 0, openedWaits: [wait], commitId: "commit" + suffix))).Status);

        var item = new WorkflowEvent(
            instance.InstanceId, "event" + suffix, wait.EventType, "{}", wait.Key, wait.WaitId,
            "subject" + suffix, Source: "source" + suffix);

        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(item)).Status);
        var saved = (await store.GetEventAsync(instance.InstanceId, item.EventId))!;
        Assert.Equal(item.EventId, saved.EventId);
        Assert.Equal(item.EventType, saved.EventType);
        Assert.Equal(item.Key, saved.Key);
        Assert.Equal(item.TargetWaitId, saved.TargetWaitId);
        Assert.Equal(wait.WaitId, (await store.GetWaitAsync(instance.InstanceId, wait.WaitId))!.WaitId);
        Assert.Single(await store.GetOpenWaitsAsync(new(InstanceId: instance.InstanceId, Key: wait.Key, EventType: wait.EventType)));
        Assert.Equal(instance.CorrelationId, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.CorrelationId);
    }

    public static TheoryData<string, string> ArtifactRegistrations
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var profile in StoreProfiles.ProfileNames)
            foreach (var kind in new[] { "workflow", "package", "template" })
                data.Add(profile, kind);

            return data;
        }
    }

    [Theory, MemberData(nameof(ArtifactRegistrations))]
    public async Task ArtifactRegistrationCancelsAfterItsExistenceCheckStarts(string profile, string kind)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var local = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(local);
        var definition = new WorkflowBuilder<Data, object>([], "ArtifactCancellation").WaitFor<string>("go").Build();
        await catalog.RegisterWorkflowAsync(definition);
        var version = (await local.GetWorkflowVersionAsync(definition.Name, 1))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken checkedToken = default;
        var artifacts = new HeldArtifacts(store, CheckAsync);

        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2, 3]));

        var package = new WorkflowPackage("artifact.package", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["review.nuspec"] = digest }, []);

        var template = new TemplateVersion("artifact-cancellation", 1, "liquid", TemplateFormat.Text,
            TemplateVersion.ModelTypeName(typeof(string)), WorkflowTypeIdentity.SavedName(typeof(string)),
            [new(package.Id, package.Version, digest)], null, null, [new("", "hello")]);

        if (kind == "template")
            await store.RegisterPackageAsync(package, new([], PackageExtraction.Indexed, null, [], []), store);

        using var cancel = new CancellationTokenSource();

        var pending = kind switch
        {
            "workflow" => (Task)store.RegisterWorkflowVersionAsync(version with { ArtifactReferences = [digest] }, artifacts, cancel.Token),
            "package" => store.RegisterPackageAsync(package, new([], PackageExtraction.Pending, null, null, []), artifacts,
                cancellationToken: cancel.Token),
            _ => store.RegisterTemplateVersionAsync(template, artifacts, cancellationToken: cancel.Token)
        };

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancel.Token, checkedToken);
            Assert.Null(await store.GetWorkflowVersionAsync(definition.Name, 1));
            Assert.Null(await store.GetTemplateVersionAsync(template.Key, template.Version));

            if (kind == "package")
                Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        }
        finally
        {
            release.TrySetResult();

            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Task<bool> CheckAsync(CancellationToken token)
        {
            checkedToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);

            return true;
        }
    }

    private static string Cursor(string eventId, string publisher) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { eventId, publisher })));

    private static WorkflowInstance Instance(string id) => new()
    {
        InstanceId = id, WorkflowName = "Review", CorrelationId = "correlation", Status = WorkflowStatus.Running,
        ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
    };

    private sealed class HeldArtifacts(IArtifactStore inner, Func<CancellationToken, Task<bool>> check) : IArtifactStore
    {
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
            inner.PutAsync(kind, content, cancellationToken);

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.OpenAsync(digest, cancellationToken);

        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            new(check(cancellationToken));

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(digest, cancellationToken);

        public ValueTask<int> PurgeUnreferencedAsync(
            IReadOnlySet<ArtifactDigest> references,
            DateTime olderThanUtc,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }

    public sealed class Data;
}