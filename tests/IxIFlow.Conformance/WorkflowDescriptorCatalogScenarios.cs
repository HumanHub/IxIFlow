using System.Text.Json;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-18")]
public sealed class WorkflowDescriptorCatalogScenarios
{
    private static WorkflowDefinition Definition(string name, string display = "Original", int version = 1) =>
        Workflow.Create<Data>(name, version).DisplayName(display).Description("Workflow help").Icon("folder").Category("Examples").Tags("tag", "Case")
            .Delay(TimeSpan.FromHours(1)).Named(display).WithId("wait").Build();
    private static WorkflowVersion Row(string name, int version = 1, WorkflowVersionState state = WorkflowVersionState.Published,
        string display = "Original", string? category = "Examples", string[]? tags = null)
    {
        var definition = Definition(name, display, version);
        definition.Category = category; definition.Tags = tags ?? ["tag", "Case"];
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        return new(name, version, WorkflowDefinitionValidator.Inspect(definition), contract.ToJson(), contract.Hash, WorkflowDescriptor.Of(definition), [], state);
    }
    private static CatalogScope Scope(params string[] names) => new(new(names), new(), new([], []), new());

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BoundedSearchProjectionPreservesCompleteUnicodeScalars(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var name = new string('n', 200);
        var display = new string('d', 198) + "\U00010000";
        await store.RegisterWorkflowVersionAsync(Row(name, display: display));
        var row = Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Text: new string('d', 198)))).Items);
        Assert.Equal(display, row.DisplayName);
        Assert.Equal(display, Assert.Single((await store.ListWorkflowVersionsAsync(name, new(CatalogScope.Unrestricted))).Items).Descriptor.DisplayName);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task VersionKeysetsUseIntegerOrderAcrossMoreThanThreeHundredVersions(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        foreach (var version in Enumerable.Range(1, 321).Reverse()) await store.RegisterWorkflowVersionAsync(Row("Versions", version));
        var found = new List<int>(); string? cursor = null;
        do
        {
            var page = await store.ListWorkflowVersionsAsync("Versions", new(CatalogScope.Unrestricted, cursor, 37));
            found.AddRange(page.Items.Select(item => item.Version)); cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(Enumerable.Range(1, 321), found);
        var summary = Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.Equal(321, summary.VersionCount); Assert.Equal(321, summary.LatestPublishedVersion);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DescriptorIsRequiredPortableAndStoredAsADeepSnapshot(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var row = Row("Snapshot");
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RegisterWorkflowVersionAsync(row with { Descriptor = null! }));
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(row with { Descriptor = row.Descriptor with { DataTypeName = "Bad" } }));
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
        var tags = new[] { "original" };
        await store.RegisterWorkflowVersionAsync(row with { Descriptor = row.Descriptor with { Tags = tags } });
        tags[0] = "mutated";
        Assert.Equal("original", Assert.Single((await store.GetWorkflowVersionAsync(row.Name, 1))!.Descriptor.Tags));
        var contract = WorkflowContract.Parse(row.ContractJson);
        var changed = new WorkflowContract(contract.Inputs.Select(member => member with { DisplayName = "Changed label" }).ToArray(), contract.Outputs, contract.Declared);
        Assert.NotEqual(row.ContractHash, changed.Hash);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(row with { ContractJson = changed.ToJson(), ContractHash = changed.Hash }));
        Assert.Equal(1, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DebuggerStepTableAndInspectionUsePinnedDefinitionAfterRefresh(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var catalog = new WorkflowCatalog(store);
        var original = Definition("Inspected");
        await catalog.RegisterWorkflowAsync(original);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.Debugging = true);
        services.AddSingleton(store); services.AddSingleton<IWorkflowCatalog>(catalog);
        await using var provider = services.BuildServiceProvider();
        var run = await provider.GetRequiredService<IWorkflowClient>().RunAsync(original, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
        await catalog.RegisterWorkflowAsync(Definition("Inspected", "Refreshed"));
        var revision = await store.GetCatalogRevisionAsync();
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        Assert.Equal("Original", Assert.Single(await operations.GetStepTableAsync(run.InstanceId)).Name);
        Assert.NotNull(await operations.InspectAsync(run.InstanceId));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Equal("Refreshed", (await store.GetWorkflowVersionAsync("Inspected", 1))!.Descriptor.DisplayName);
        Assert.Same(original, await catalog.GetWorkflowDefinitionAsync("Inspected", 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LatestStartupRegistrationRefreshesUnderConcurrencyAndKeepsExecutablePinned(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var firstHost = new WorkflowCatalog(store);
        using var secondHost = new WorkflowCatalog(profile == StoreProfiles.LocalSqlite ? store : database.NewRepository());
        var original = Definition("Refresh");
        await firstHost.RegisterWorkflowAsync(original);
        var before = (await store.GetWorkflowVersionAsync("Refresh", 1))!;
        var revision = await store.GetCatalogRevisionAsync();
        await secondHost.RegisterWorkflowAsync(Definition("Refresh", "Second host"));
        Assert.Equal("Second host", (await store.GetWorkflowVersionAsync("Refresh", 1))!.Descriptor.DisplayName);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
        // Exercise the facade's early already-attached path as well as another host's first attachment.
        await firstHost.RegisterWorkflowAsync(Definition("Refresh", "First host again"));
        Assert.Equal("First host again", (await store.GetWorkflowVersionAsync("Refresh", 1))!.Descriptor.DisplayName);
        var start = await store.GetCatalogRevisionAsync();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => (index % 2 == 0 ? firstHost : secondHost)
            .RegisterWorkflowAsync(Definition("Refresh", "Concurrent " + index))));
        Assert.Equal(start + 16, await store.GetCatalogRevisionAsync());
        await secondHost.RegisterWorkflowAsync(Definition("Refresh", "Final winner"));
        var after = (await store.GetWorkflowVersionAsync("Refresh", 1))!;
        Assert.Equal("Final winner", after.Descriptor.DisplayName);
        Assert.Equal(before.StructureHash, after.StructureHash);
        Assert.Equal(before.ContractJson, after.ContractJson);
        Assert.Equal(before.ContractHash, after.ContractHash);
        Assert.Equal(before.LifecycleRevision, after.LifecycleRevision);
        var changes = await store.GetWorkflowVersionChangesAsync(revision);
        Assert.Equal(after.Descriptor, Assert.Single(changes.Versions).Descriptor);
        using var inspectionLease = await firstHost.AcquireWorkflowDefinitionAsync("Refresh", 1, null);
        Assert.Same(original, inspectionLease.Definition);
        Assert.Equal("Original", inspectionLease.Definition.DisplayName);
        Assert.Equal("Original", inspectionLease.Definition.StepTable.Single().Name);
        Assert.Equal("Final winner", Assert.Single((await store.ListWorkflowVersionsAsync("Refresh", new(CatalogScope.Unrestricted))).Items).Descriptor.DisplayName);
        var unchanged = await store.GetCatalogRevisionAsync();
        await secondHost.RegisterWorkflowAsync(Definition("Refresh", "Final winner"));
        Assert.Equal(unchanged, await store.GetCatalogRevisionAsync());
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(after with { ContractHash = "wrong" }));
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(after with { StructureHash = "different" }));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WorkflowFiltersApplyToOneVersionAndSummariesUseFilteredPublishedPrecedence(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.SaveDefinitionAsync(new("DraftOnly", 1, WorkflowDraftKinds.Workflow, "draft"), null);
        await store.RegisterWorkflowVersionAsync(Row("Summary", 1, display: "Published one", category: "First", tags: ["one"]));
        await store.RegisterWorkflowVersionAsync(Row("Summary", 2, WorkflowVersionState.Deprecated, "Deprecated two", "Second", ["two"]));
        await store.RegisterWorkflowVersionAsync(Row("Summary", 3, WorkflowVersionState.Draft, "Draft three", "Third", ["three"]));
        var summary = Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.Equal("Published one", summary.DisplayName); Assert.Equal(1, summary.LatestPublishedVersion); Assert.Equal(3, summary.VersionCount);
        Assert.Empty((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Tag: "one", Category: "Second"))).Items);
        Assert.Empty((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Text: "published", State: WorkflowVersionState.Deprecated))).Items);
        Assert.Empty((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Tag: "ONE"))).Items);
        var draft = Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, State: WorkflowVersionState.Draft))).Items);
        Assert.Equal("Draft three", draft.DisplayName); Assert.Null(draft.LatestPublishedVersion); Assert.Equal(1, draft.VersionCount);
        var other = Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Category: "Second"))).Items);
        Assert.Equal("Deprecated two", other.DisplayName); Assert.Null(other.LatestPublishedVersion); Assert.Equal(1, other.VersionCount);
        var versions = (await store.ListWorkflowVersionsAsync("Summary", new(CatalogScope.Unrestricted, Limit: 2)));
        Assert.Equal(new[] { 1, 2 }, versions.Items.Select(row => row.Version)); Assert.NotNull(versions.NextCursor);
        var last = await store.ListWorkflowVersionsAsync("Summary", new(CatalogScope.Unrestricted, After: versions.NextCursor, Limit: 2));
        Assert.Equal(3, Assert.Single(last.Items).Version); Assert.Null(last.NextCursor);
        Assert.Empty((await store.ListWorkflowVersionsAsync("Summary", new(Scope("Other")))).Items);
        Assert.Null(await store.GetWorkflowVersionDetailAsync("Summary", 1, Scope("Other")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListWorkflowVersionsAsync("Other", new(CatalogScope.Unrestricted, versions.NextCursor)));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WorkflowKeysetsScopesLiteralSearchAndDeepSnapshotsAreStableAcrossManyRows(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        foreach (var index in Enumerable.Range(0, 321).Reverse())
            await store.RegisterWorkflowVersionAsync(Row("Page" + index.ToString("D3"), display: index == 42 ? "Literal %_[]\\" : "Visible " + index));
        foreach (var name in new[] { "z", "\u00e9", "\uE000", "\U00010000" }) await store.RegisterWorkflowVersionAsync(Row(name));
        var names = new List<string>(); string? cursor = null;
        do
        {
            var page = await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, After: cursor, Limit: 17));
            Assert.Equal(await store.GetCatalogRevisionAsync(), page.CatalogRevision);
            names.AddRange(page.Items.Select(item => item.Name)); cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(325, names.Count); Assert.Equal(325, names.Distinct().Count());
        Assert.Equal(new[] { "z", "\u00e9", "\uE000", "\U00010000" }, names.TakeLast(4));
        Assert.Equal("Page042", Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, Text: "%_[]\\"))).Items).Name);
        var allowed = new CatalogScope(new(["Page100"], ["Page00"]), new(), new(), new());
        var scoped = await store.ListWorkflowsAsync(new(allowed, Limit: 7));
        Assert.Equal(7, scoped.Items.Count); Assert.All(scoped.Items, item => Assert.True(allowed.Allows(WorkflowDraftKinds.Workflow, item.Name)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListWorkflowsAsync(new(CatalogScope.Unrestricted, After: scoped.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListWorkflowsAsync(new(allowed, Tag: "tag", After: scoped.NextCursor)));
        Assert.Empty((await store.ListWorkflowsAsync(new(Scope()))).Items);
        var descriptor = (await store.GetWorkflowVersionAsync("Page001", 1))!.Descriptor;
        Assert.False(descriptor.Tags is string[]);
        var versionPage = await store.ListWorkflowVersionsAsync("Page001", new(CatalogScope.Unrestricted));
        Assert.Equal(descriptor, Assert.Single(versionPage.Items).Descriptor);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetWorkflowVersionAsync("Page001 ", 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task HostReadsRequireLiveExactCapabilityAndHashAndParentScope(string profile)
    {
        var time = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var row = Row("Hosts"); await store.RegisterWorkflowVersionAsync(row);
        var good = Guid.NewGuid(); var wrong = Guid.NewGuid(); var bundleOnly = Guid.NewGuid();
        async Task Register(Guid id, Dictionary<string, string> capabilities) => await store.RegisterMemberAsync(new(id, MemberKind.Host, "Host", null, 1, "engine", TimeSpan.FromSeconds(5), capabilities));
        await Register(good, new() { ["workflow:Hosts@1"] = row.StructureHash });
        if (profile == StoreProfiles.LocalSqlite) await store.UnregisterMemberAsync(good);
        await Register(wrong, new() { ["workflow:Hosts@1"] = "different" });
        if (profile == StoreProfiles.LocalSqlite) await store.UnregisterMemberAsync(wrong);
        await Register(bundleOnly, new() { ["bundle-host"] = "true" });
        using var catalog = new WorkflowCatalog(store);
        if (profile == StoreProfiles.LocalSqlite)
        {
            Assert.Empty(await catalog.GetWorkflowVersionHostsAsync("Hosts", 1, CatalogScope.Unrestricted));
            await store.UnregisterMemberAsync(bundleOnly);
            await Register(good, new() { ["workflow:Hosts@1"] = row.StructureHash });
        }
        Assert.Equal(good, Assert.Single(await catalog.GetWorkflowVersionHostsAsync("Hosts", 1, CatalogScope.Unrestricted)).Id);
        Assert.Empty(await catalog.GetWorkflowVersionHostsAsync("Hosts", 1, Scope("Other")));
        Assert.Empty(await catalog.GetWorkflowVersionHostsAsync("Missing", 1, CatalogScope.Unrestricted));
        if (profile.StartsWith("distributed-", StringComparison.Ordinal))
        {
            await using var connection = database.NewConnection();
            await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
                ? "UPDATE dbo.IxIFlowHosts SET HeartbeatAtUtc=DATEADD(MINUTE,-10,SYSUTCDATETIME())"
                : "UPDATE ixiflow_hosts SET heartbeat_at_utc=clock_timestamp()-INTERVAL '10 minutes'");
        }
        else time.Advance(TimeSpan.FromSeconds(16));
        Assert.Empty(await catalog.GetWorkflowVersionHostsAsync("Hosts", 1, CatalogScope.Unrestricted));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RetirementBlockersAndDetailMaskRelatedNames(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var child = Row("Child"); await store.RegisterWorkflowVersionAsync(child);
        var caller = Row("VisibleCaller") with { Dependencies = [new("Child", 1), new("HiddenChild", 1)] };
        var packageDigest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2, 3]));
        var manifest = new WorkflowBundleManifest(4, caller.Name, 1, caller.StructureHash, caller.Descriptor.DataType, "Entry", "1.0", WorkflowDefinitionValidator.CheckpointContractVersion,
            "", caller.ContractJson, caller.ContractHash, WorkflowCompilation.Settings, new Dictionary<string, string>(), new Dictionary<string, ArtifactDigest>())
        {
            ActivityPackages = new[] { "visible.package", "hidden.package" }.Select(id => new WorkflowPackagePin(id, "1.0.0", packageDigest,
                new Dictionary<string, ArtifactDigest>(), [], new Dictionary<string, WorkflowPackageAssets>(), false)).ToArray()
        };
        caller = caller with { ManifestJson = JsonSerializer.Serialize(manifest) };
        await store.RegisterWorkflowVersionAsync(caller);
        await store.RegisterWorkflowVersionAsync(Row("HiddenCaller") with { Dependencies = [new("Child", 1)] });
        var scope = Scope("Child", "VisibleCaller");
        var blockers = await store.GetRetirementBlockersAsync("Child", 1, scope);
        Assert.Equal("VisibleCaller", Assert.Single(blockers.Callers).Name); Assert.True(blockers.OutsideScope);
        var hidden = await store.GetRetirementBlockersAsync("Child", 1, Scope("Other"));
        Assert.Empty(hidden.Callers); Assert.Null(hidden.UnfinishedCaller); Assert.True(hidden.OutsideScope);
        var detail = (await store.GetWorkflowVersionDetailAsync("VisibleCaller", 1, scope))!;
        Assert.Equal("Child", Assert.Single(detail.Dependencies).Name);
        Assert.Equal(caller.ContractJson, detail.Contract.ToJson()); Assert.False(detail.ArtifactPresent);
        Assert.Empty(detail.ActivityPackages);
        var packageScope = new CatalogScope(scope.Workflows, new(), new(["visible.package"]), new());
        Assert.Equal("visible.package", Assert.Single((await store.GetWorkflowVersionDetailAsync("VisibleCaller", 1, packageScope))!.ActivityPackages).Id);
        var unfinished = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "HiddenCaller", WorkflowVersion = 1,
            Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
            WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(Data))
        };
        await store.SeedAsync(unfinished);
        var hiddenCaller = (await store.GetWorkflowVersionAsync("HiddenCaller", 1))!;
        await store.SetWorkflowVersionStateAsync(hiddenCaller.Name, 1, WorkflowVersionState.Retired, new(hiddenCaller.LifecycleRevision));
        var masked = await store.GetRetirementBlockersAsync("Child", 1, scope);
        Assert.True(masked.OutsideScope); Assert.Null(masked.UnfinishedCaller);
        var complete = await store.GetRetirementBlockersAsync("Child", 1, CatalogScope.Unrestricted);
        Assert.Equal("HiddenCaller", complete.UnfinishedCaller!.Name);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedBundlePresentationIsPersistedAndImmutable(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var yaml = """
            workflow:
              name: PublishedPresentation
              version: 1
              dataType: Data
              displayName: Published display
              description: Published help
              category: Published
              icon: file-text
              tags: [published]
            definitions:
              - kind: delay
                duration: 00:01:00
            """;
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + typeof(Data).FullName!.Replace('+', '.');
        using var catalog = new WorkflowCatalog(store);
        var draft = await store.SaveDefinitionAsync(new("PublishedPresentation", 1, WorkflowDraftKinds.Workflow, yaml), null);
        var published = await new WorkflowPublisher(catalog, store, options).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var row = published.Workflow!;
        Assert.Equal("Published display", row.Descriptor.DisplayName);
        var revision = await store.GetCatalogRevisionAsync();
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(row with { Descriptor = row.Descriptor with { DisplayName = "Changed" } }));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        var detail = (await store.GetWorkflowVersionDetailAsync(row.Name, row.Version, CatalogScope.Unrestricted))!;
        Assert.True(detail.ArtifactPresent); Assert.Equal(row.ContractJson, detail.Contract.ToJson());
        Assert.Equal(draft.Id, detail.PublishedDraft); Assert.Equal(row.Descriptor, detail.Summary.Descriptor);
        var retry = await new WorkflowPublisher(catalog, store, options).PublishAsync(draft.Id);
        Assert.True(retry.Success); Assert.Equal(row.Descriptor, retry.Workflow!.Descriptor);
    }

    public static TheoryData<string> Relational => new(StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory));
    [Theory, MemberData(nameof(Relational))]
    public async Task PagesReadOnlyProjectionsAndRefuseProjectionCorruption(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository(); await store.RegisterWorkflowVersionAsync(Row("Projection"));
        await using var connection = database.NewConnection();
        var table = database.Profile == StoreProfiles.DistributedSqlServer ? "dbo.IxIFlowWorkflowVersions" : "ixiflow_workflow_versions";
        var metadata = database.Profile == StoreProfiles.DistributedSqlServer ? "MetadataJson" : "metadata_json";
        await connection.ExecuteAsync($"UPDATE {table} SET {metadata}='invalid-json'");
        Assert.Single((await store.ListWorkflowsAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.Single((await store.ListWorkflowVersionsAsync("Projection", new(CatalogScope.Unrestricted))).Items);
        await Assert.ThrowsAsync<JsonException>(() => store.GetWorkflowVersionDetailAsync("Projection", 1, CatalogScope.Unrestricted));
        var display = database.Profile == StoreProfiles.DistributedSqlServer ? "DisplayName" : "display_name";
        await connection.ExecuteAsync($"UPDATE {table} SET {display}='corrupt'");
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.ListWorkflowVersionsAsync("Projection", new(CatalogScope.Unrestricted)));
    }

    public sealed class Data { [IxIFlow.ActivitySdk.WorkflowInput] public int Value { get; set; } }
}
