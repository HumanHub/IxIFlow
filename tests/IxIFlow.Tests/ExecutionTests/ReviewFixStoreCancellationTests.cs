using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Relational;
using IxIFlow.Templates;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class ReviewFixStoreCancellationTests
{
    [Theory]
    [InlineData("claim")]
    [InlineData("renew")]
    [InlineData("settle")]
    [InlineData("acquire")]
    [InlineData("release")]
    public async Task GuardForwardsCancellationAfterItsDelegateHasStarted(string operation)
    {
        var target = DispatchProxy.Create<IWorkflowStateRepository, HeldStore>();
        var probe = (HeldStore)target;
        var guarded = GuardedStateRepository.Wrap(target);
        using var cancel = new CancellationTokenSource();

        var pending = operation switch
        {
            "claim" => (Task)guarded.ClaimDeliveriesAsync("publisher", "owner", TimeSpan.FromMinutes(1), 1, cancel.Token),
            "renew" => guarded.RenewDeliveryLeaseAsync("event", "publisher", "owner", TimeSpan.FromMinutes(1), cancel.Token),
            "settle" => guarded.SettleDeliveryAsync("event", "publisher", "owner", OutboxPublishResult.Delivered(), cancel.Token),
            "acquire" => guarded.TryAcquireExecutionLeaseAsync(
                "instance", "owner", TimeSpan.FromMinutes(1),
                new(Guid.Empty, new Dictionary<string, string>()), cancellationToken: cancel.Token),
            _ => guarded.ReleaseExecutionLeaseAsync("instance", "owner", cancel.Token)
        };

        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancel.Token, probe.Token);
        }
        finally
        {
            probe.Release.TrySetResult();
            await ObserveAsync(pending);
        }
    }

    [Theory]
    [InlineData("workflow-register")]
    [InlineData("workflow-publish")]
    [InlineData("package-register")]
    [InlineData("package-publish")]
    [InlineData("template-register")]
    [InlineData("template-publish")]
    [InlineData("workflow-state")]
    [InlineData("package-withdraw")]
    [InlineData("commit")]
    public async Task MemoryCatalogWaitCancelsBeforeItCanMutate(string operation)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store);
        var definition = new WorkflowBuilder<Data, object>([], "QueuedRegistration").WaitFor<string>("go").Build();
        await catalog.RegisterWorkflowAsync(definition);
        var version = (await store.GetWorkflowVersionAsync(definition.Name, 1))!;
        var draft = await store.SaveDefinitionAsync(new("QueuedPublication", 1, "workflow", "source"), null);
        var activity = await store.SaveDefinitionAsync(new("review.package", 1, "activity", "source"), null);
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2, 3]));

        var package = new WorkflowPackage("review.package", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["review.nuspec"] = digest }, []);

        var activities = new PackageActivities([], PackageExtraction.Pending, null, null, []);
        var retainedPackage = (await store.RegisterPackageAsync(package with { Id = "retained.package" }, activities, store)).Stored;
        var templateDraft = await store.SaveDefinitionAsync(new("review-template", 1, "template", "source"), null);

        var template = new TemplateVersion(templateDraft.Name, 1, "liquid", TemplateFormat.Text,
            TemplateVersion.ModelTypeName(typeof(Data)), WorkflowTypeIdentity.SavedName(typeof(Data)), [], null, null, [new("", "hello")]);

        var instance = Instance("queued-commit");
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);

        var gate = (SemaphoreSlim)typeof(InMemoryWorkflowStateRepository)
            .GetField("_catalogLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

        await gate.WaitAsync();
        using var cancel = new CancellationTokenSource();

        var pending = operation switch
        {
            "workflow-register" => (Task)store.RegisterWorkflowVersionAsync(
                version with { Name = "NewRegistration" },
                cancellationToken: cancel.Token),
            "workflow-publish" => store.PublishDefinitionAsync(
                draft.Id,
                version with { Name = draft.Name, PublishedDraft = draft.Id }, store, cancellationToken: cancel.Token),
            "package-register" => store.RegisterPackageAsync(package, activities, store, cancellationToken: cancel.Token),
            "package-publish" => store.PublishPackageDefinitionAsync(
                activity.Id,
                package with { Origin = WorkflowPackageOrigin.Published, PublishedDraft = activity.Id }, activities, store,
                cancellationToken: cancel.Token),
            "template-register" => store.RegisterTemplateVersionAsync(template, store, cancellationToken: cancel.Token),
            "template-publish" => store.PublishTemplateDefinitionAsync(templateDraft.Id, template, store, cancellationToken: cancel.Token),
            "workflow-state" => store.SetWorkflowVersionStateAsync(version.Name, version.Version, WorkflowVersionState.Retired,
                new(version.LifecycleRevision), cancellationToken: cancel.Token),
            "package-withdraw" => store.SetPackageWithdrawnAsync(retainedPackage.Id, retainedPackage.Version, true,
                new(retainedPackage.LifecycleRevision), cancellationToken: cancel.Token),
            _ => store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0), cancel.Token)
        };

        try
        {
            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotNull(await store.GetDefinitionAsync(draft.Kind, draft.Name, 1));
            Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
            Assert.Null(await store.GetTemplateVersionAsync(template.Key, template.Version));
            Assert.False((await store.GetPackageAsync(retainedPackage.Id, retainedPackage.Version))!.Withdrawn);
            Assert.Equal(WorkflowVersionState.Published, (await store.GetWorkflowVersionAsync(version.Name, version.Version))!.State);
        }
        finally
        {
            gate.Release();
            await ObserveAsync(pending);
        }
    }

    [Theory]
    [InlineData("publishers", false)]
    [InlineData("renew", false)]
    [InlineData("settle", false)]
    [InlineData("renew", true)]
    [InlineData("settle", true)]
    [InlineData("installed", true)]
    public async Task RelationalOperationsCancelTheirSchemaOrWriterWait(string operation, bool initialized)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.ReviewCancellation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "store.db"));

            if (initialized)
                await store.GetCatalogRevisionAsync();

            var gate = (SemaphoreSlim)(initialized ? typeof(SqliteWorkflowStateRepository) : typeof(RelationalWorkflowStateRepository))
                .GetField(initialized ? "_writer" : "_schemaLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

            await gate.WaitAsync();
            using var cancel = new CancellationTokenSource();

            var pending = operation switch
            {
                "publishers" => (Task)store.ListPublishersAsync(cancel.Token),
                "renew" => store.RenewDeliveryLeaseAsync("event", "publisher", "owner", TimeSpan.FromMinutes(1), cancel.Token),
                "settle" => store.SettleDeliveryAsync("event", "publisher", "owner", OutboxPublishResult.Delivered(), cancel.Token),
                _ => store.GetInstalledPackagesAsync(cancellationToken: cancel.Token)
            };

            try
            {
                Assert.False(pending.IsCompleted);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                gate.Release();
                await ObserveAsync(pending);
            }
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    private static WorkflowInstance Instance(string id) => new()
    {
        InstanceId = id, WorkflowName = "Review", Status = WorkflowStatus.Running,
        ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
    };

    private static async Task ObserveAsync(Task pending)
    {
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Cleanup also runs against the deliberately broken product in the red regression run.
        }
    }

    public class HeldStore : DispatchProxy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Token = (CancellationToken)args![^1]!;
            Entered.TrySetResult();

            return typeof(HeldStore).GetMethod(nameof(HoldAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(method!.ReturnType.GenericTypeArguments[0]).Invoke(this, null);
        }

        private async Task<T> HoldAsync<T>()
        {
            await Release.Task.WaitAsync(Token);

            return default!;
        }
    }

    public sealed class Data;
}