using IxIFlow.ActivitySdk;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-12")]
public sealed class PackageDescriptorCatalogScenarios
{
    public static TheoryData<string> RelationalProfiles => new(StoreProfiles.ProfileNames.Where(item => item != StoreProfiles.LocalMemory));

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityOrdinalKeysetsAgreeForBmpAndSupplementaryCharacters(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, activities) = await Fixture(store, "catalog.unicode");
        var keys = new[] { "z", "\u00E9", "\uE000", "\U00010000" };

        await store.RegisterPackageAsync(package,
            activities with { Activities = keys.Reverse().Select(key => activities.Activities[0] with { Key = key }).ToArray() }, store);

        var query = new CatalogActivityQuery(CatalogScope.Unrestricted, Limit: 1);
        var found = new List<string>();

        do
        {
            var page = await store.ListActivitiesAsync(query);
            found.Add(Assert.Single(page.Items).Descriptor.Key);
            query = query with { After = page.NextCursor };
        } while (query.After != null);

        Assert.Equal(keys, found);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FailedActivityInsertionRollsBackPackageClosureAndRevision(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, activities) = await Fixture(store, "catalog.rollback");
        var before = await store.GetCatalogRevisionAsync();

        if (database.Profile == StoreProfiles.LocalMemory)
            await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(package,
                activities with { Activities = [activities.Activities[0] with { Icon = "INVALID" }] }, store));
        else
        {
            await using var connection = database.NewConnection();

            var install = database.Profile switch
            {
                StoreProfiles.LocalSqlite =>
                    "CREATE TRIGGER reject_activity BEFORE INSERT ON ixiflow_package_activities BEGIN SELECT RAISE(ABORT,'fixture'); END;",
                StoreProfiles.DistributedSqlServer =>
                    "CREATE TRIGGER dbo.RejectActivity ON dbo.IxIFlowPackageActivities AFTER INSERT AS THROW 51000,'fixture',1;",
                _ =>
                    "CREATE FUNCTION reject_activity() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture'; END $$; CREATE TRIGGER reject_activity BEFORE INSERT ON ixiflow_package_activities FOR EACH ROW EXECUTE FUNCTION reject_activity();"
            };

            await connection.ExecuteAsync(install);
            await Assert.ThrowsAnyAsync<Exception>(() => store.RegisterPackageAsync(package, activities, store));

            await connection.ExecuteAsync(database.Profile switch
            {
                StoreProfiles.LocalSqlite => "DROP TRIGGER reject_activity",
                StoreProfiles.DistributedSqlServer => "DROP TRIGGER dbo.RejectActivity",
                _ => "DROP TRIGGER reject_activity ON ixiflow_package_activities; DROP FUNCTION reject_activity()"
            });
        }

        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        Assert.Empty((await store.ListActivitiesAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        var receipt = await store.RegisterPackageAsync(package, activities, store);
        Assert.True(receipt.Created);
        Assert.Single(receipt.Activities.Activities);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
    }

    [Theory]
    [MemberData(nameof(RelationalProfiles))]
    public async Task PackageActivityPrimaryKeyShapeIsRequiredOnReopen(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        await using (var connection = database.NewConnection())
        {
            if (database.Profile == StoreProfiles.LocalSqlite)
            {
                await ReplaceSqliteActivityTable(connection, correctKey: false);
            }
            else if (database.Profile == StoreProfiles.DistributedSqlServer)
            {
                var constraint = await connection.QuerySingleAsync<string>(
                    "SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.IxIFlowPackageActivities') AND type='PK'");

                await connection.ExecuteAsync("ALTER TABLE dbo.IxIFlowPackageActivities DROP CONSTRAINT [" + constraint.Replace("]", "]]") +
                    "]; ALTER TABLE dbo.IxIFlowPackageActivities ADD PRIMARY KEY (PackageId,PackageVersion,Version,[Key])");
            }
            else
            {
                var constraint = await connection.QuerySingleAsync<string>(
                    "SELECT conname FROM pg_constraint WHERE conrelid='ixiflow_package_activities'::regclass AND contype='p'");

                await connection.ExecuteAsync("ALTER TABLE ixiflow_package_activities DROP CONSTRAINT \"" +
                    constraint.Replace("\"", "\"\"") +
                    "\"; ALTER TABLE ixiflow_package_activities ADD PRIMARY KEY (package_id,package_version,version,key)");
            }
        }

        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("recreated", error.Message);
    }

    [Fact]
    public async Task SqliteReplacementActivityTableWithCorrectPrimaryKeyReopens()
    {
        await using var database = await StoreProfiles.CreateAsync(StoreProfiles.LocalSqlite);
        var store = database.NewRepository();
        var revision = await store.GetCatalogRevisionAsync();
        await ((IAsyncDisposable)store).DisposeAsync();

        await using (var connection = database.NewConnection())
            await ReplaceSqliteActivityTable(connection, correctKey: true);

        Assert.Equal(revision, await database.NewRepository().GetCatalogRevisionAsync());
    }

    private static async Task ReplaceSqliteActivityTable(System.Data.Common.DbConnection connection, bool correctKey)
    {
        var schema = await connection.QuerySingleAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='ixiflow_package_activities'");

        var indexes = (await connection.QueryAsync<(string Name, string Sql)>(
                "SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name='ixiflow_package_activities' AND sql IS NOT NULL"))
            .ToArray();

        await connection.ExecuteAsync("ALTER TABLE ixiflow_package_activities RENAME TO previous_activity_rows");

        await connection.ExecuteAsync(correctKey
            ? schema
            : System.Text.RegularExpressions.Regex.Replace(schema,
                @"PRIMARY KEY \([^)]+\)", "PRIMARY KEY (package_id, package_version, version, key)"));

        foreach (var index in indexes)
        {
            await connection.ExecuteAsync("DROP INDEX \"" + index.Name.Replace("\"", "\"\"") + "\"");
            await connection.ExecuteAsync(index.Sql);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PendingCompletionAndOriginUpgradeUpdateAnExistingPackage(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, indexed) = await Fixture(store, "catalog.update");

        var first = await store.RegisterPackageAsync(package, new([], PackageExtraction.Pending, "waiting", null, ["catalog.missing"]),
            store, "first");

        var before = await store.GetCatalogRevisionAsync();
        var completed = await store.CompletePackageExtractionAsync(package.Id, package.Version, package.Digest, indexed, store);
        Assert.False(completed.Created);
        Assert.Equal(PackageExtraction.Indexed, completed.Activities.Extraction);
        Assert.Single(completed.Activities.Activities);
        var upgraded = await store.RegisterPackageAsync(package with { Origin = WorkflowPackageOrigin.Uploaded }, indexed, store, "second");
        Assert.Equal(WorkflowPackageOrigin.Uploaded, upgraded.Stored.Origin);
        Assert.Equal(first.Stored.RegisteredAtUtc, upgraded.Stored.RegisteredAtUtc);
        Assert.Equal("first", upgraded.Stored.RegisteredBy);
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPendingPackageDependentsAsync("catalog.missing", null, 10));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityKeysRejectPaddedNamesAndCatalogLookupsRemainExact(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, activities) = await Fixture(store, "catalog.exact");
        var descriptor = activities.Activities[0] with { Key = "echo" };

        foreach (var key in new[] { " echo", "echo ", "\techo", "echo\u00a0" })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(package,
                activities with { Activities = [descriptor with { Key = key }] }, store));

            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        }

        await store.RegisterPackageAsync(package,
            activities with { Activities = [descriptor, descriptor with { Key = "Echo" }, descriptor with { Key = "echo.more" }] }, store);

        var scope = new CatalogScope(new([]), new(["echo"]), new([package.Id]), new([]));
        Assert.Equal("echo", Assert.Single((await store.ListActivitiesAsync(new(scope))).Items).Descriptor.Key);
        Assert.Equal(1, Assert.Single((await store.ListPackagesAsync(new(scope))).Items).ActivityCount);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ListActivityVersionsAsync("echo ", new(CatalogScope.Unrestricted)));

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetActivityAsync("echo ", descriptor.Version,
            new(package.Id, package.Version, package.Digest.Value), CatalogScope.Unrestricted));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageAndActivityProjectionsCannotDisagreeWithTheirPortableMetadata(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, activities) = await Fixture(store, "catalog.projection");
        await store.RegisterPackageAsync(package, activities, store);
        var returned = (await store.GetPackageDetailAsync(package.Id, package.Version, CatalogScope.Unrestricted))!;

        Assert.Throws<NotSupportedException>(() =>
            ((IList<ActivityDescriptor>)returned.Activities.Activities)[0] = activities.Activities[0] with { Name = "Changed" });

        if (database.Profile == StoreProfiles.LocalMemory)
        {
            ((IDictionary<string, ArtifactDigest>)package.Entries)["another.entry"] = package.Digest;
            Assert.Single((await store.GetPackageAsync(package.Id, package.Version))!.Entries);

            return;
        }

        await using var connection = database.NewConnection();

        await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowPackageActivities SET CSharpTypeName='corrupt' WHERE PackageId=@Id"
            : "UPDATE ixiflow_package_activities SET csharp_type_name='corrupt' WHERE package_id=@Id", package);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.GetPackageDetailAsync(package.Id, package.Version, CatalogScope.Unrestricted));

        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);

        await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowPackages SET Origin='Uploaded' WHERE Id=@Id"
            : "UPDATE ixiflow_packages SET origin='Uploaded' WHERE id=@Id", package);

        error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetPackageAsync(package.Id, package.Version));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageAcceptanceIsAtomicAuditedImmutableAndIdempotent(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var other = profile == StoreProfiles.LocalSqlite ? store : database.NewRepository();
        var (package, activities) = await Fixture(store, "catalog.atomic");
        var before = await store.GetCatalogRevisionAsync();

        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            (index % 2 == 0 ? store : other).RegisterPackageAsync(package, activities, store, "winner-" + index)));

        Assert.Single(receipts, item => item.Created);
        var winner = receipts.Single(item => item.Created);
        Assert.NotNull(winner.Stored.RegisteredBy);
        Assert.NotEqual(default, winner.Stored.RegisteredAtUtc);
        Assert.All(receipts, item => Assert.Equal(winner.Stored.RegisteredBy, item.Stored.RegisteredBy));
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        store = await Restart(database, store);
        var reopened = store;

        var detail = Assert.IsType<CatalogPackageDetail>(await reopened.GetPackageDetailAsync(package.Id, package.Version,
            CatalogScope.Unrestricted));

        Assert.Single(detail.Activities.Activities);
        Assert.Empty(detail.Activities.Closure!);
        Assert.Equal(PackageExtraction.Indexed, detail.Summary.Extraction);
        Assert.Equal(winner.Stored.LifecycleRevision, detail.Summary.LifecycleRevision);
        Assert.Null(detail.Activities.Activities[0].ActivityType);

        var upgraded =
            await reopened.RegisterPackageAsync(package with { Origin = WorkflowPackageOrigin.Uploaded }, activities, store, "uploader");

        Assert.False(upgraded.Created);
        Assert.Equal(WorkflowPackageOrigin.Uploaded, upgraded.Stored.Origin);
        Assert.Equal(winner.Stored.RegisteredBy, upgraded.Stored.RegisteredBy);
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
        var replay = await store.RegisterPackageAsync(package, activities, store);
        Assert.Equal(WorkflowPackageOrigin.Uploaded, replay.Stored.Origin);
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
        var changedBytes = await store.PutAsync(ArtifactKind.Package, new MemoryStream([2, 3]));

        var conflict = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.RegisterPackageAsync(package with { Digest = changedBytes }, activities, store));

        Assert.Equal(WorkflowErrorKind.InvalidState, conflict.Kind);

        conflict = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(package,
            activities with { Activities = [activities.Activities[0] with { Name = "Changed" }] }, store));

        Assert.Equal(WorkflowErrorKind.InvalidState, conflict.Kind);
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackagePendingCompletionPersistsClosureAndRowsTogetherAcrossRestart(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (package, indexed) = await Fixture(store, "catalog.pending");
        var pending = new PackageActivities([], PackageExtraction.Pending, "Missing dependency", null, ["catalog.dependency"]);
        await store.RegisterPackageAsync(package, pending, store);
        var revision = await store.GetCatalogRevisionAsync();
        Assert.Single(await store.GetPendingPackageDependentsAsync("catalog.dependency", null, 10));
        store = await Restart(database, store);
        var reopened = store;
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([3, 4]));
        var dependency = package with { Id = "catalog.dependency", Digest = digest };
        await store.RegisterPackageAsync(dependency, new([], PackageExtraction.Indexed, null, [], []), store);
        var complete = indexed with { Closure = [new(dependency.Id, dependency.Version, dependency.Digest)] };

        var failed = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            reopened.CompletePackageExtractionAsync(package.Id, package.Version, digest, complete, store));

        Assert.Equal(WorkflowErrorKind.InvalidState, failed.Kind);

        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            reopened.CompletePackageExtractionAsync(package.Id, package.Version, package.Digest, complete, store)));

        Assert.All(receipts, item => Assert.False(item.Created));
        Assert.Equal(revision + 2, await store.GetCatalogRevisionAsync());

        var detail = Assert.IsType<CatalogPackageDetail>(await reopened.GetPackageDetailAsync(package.Id, package.Version,
            CatalogScope.Unrestricted));

        Assert.Single(detail.Activities.Activities);
        Assert.Equal(dependency.Digest, Assert.Single(detail.Activities.Closure!).Digest);
        Assert.Empty(await store.GetPendingPackageDependentsAsync(dependency.Id, null, 10));
        Assert.Empty(await store.GetPackageExtractionPageAsync(PackageExtraction.Pending, null, 10));
        var unavailable = complete with { Closure = [new("missing.bytes", "1.0.0", ArtifactDigest.Of([99]))] };

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(package with { Id = "another.package" },
            unavailable with { Activities = [] }, store));

        Assert.Null(await store.GetPackageAsync("another.package", "1.0.0"));
        Assert.Equal(revision + 2, await store.GetCatalogRevisionAsync());
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageUploadsWorkDependentFirstOfflineAndRetryAfterRestart(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (activity, dependency) = ActivityPackageFixture.Create();
        var root = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(activity), "first");
        Assert.Equal(PackageExtraction.Pending, root.Activities.Extraction);
        Assert.Null(root.Activities.Closure);
        Assert.Equal(new[] { "fixture.dependency" }, root.Activities.MissingPackageIds);
        Assert.Empty(root.Activities.Activities);
        var restarted = await Restart(database, store);
        await new PackageIngestion(restarted, restarted).UploadPackageAsync(new MemoryStream(dependency), "dependency");

        var detail = Assert.IsType<CatalogPackageDetail>(await restarted.GetPackageDetailAsync("fixture.activities", "1.0.0",
            CatalogScope.Unrestricted));

        Assert.Equal(PackageExtraction.Indexed, detail.Summary.Extraction);
        Assert.Equal("fixture.echo", Assert.Single(detail.Activities.Activities).Key);
        Assert.Equal("fixture.dependency", Assert.Single(detail.Activities.Closure!).Id);
        Assert.Equal("first", detail.RegisteredBy);

        var a = ActivityPackageFixture.Package("offline.a", "1.0.0", [], "<dependency id=\"offline.b\" version=\"[1.0.0]\" />");
        var b = ActivityPackageFixture.Package("offline.b", "1.0.0", [], "<dependency id=\"offline.c\" version=\"[1.0.0]\" />");
        var c = ActivityPackageFixture.Package("offline.c", "1.0.0", []);
        await new PackageIngestion(restarted, restarted).UploadPackageAsync(new MemoryStream(a));
        await new PackageIngestion(restarted, restarted).UploadPackageAsync(new MemoryStream(b));

        // Simulate a crash after dependency commit, before any indexing service notification.
        var ingestion = new PackageIngestion(restarted, restarted);
        var candidate = await ingestion.ReadPackageAsync(new MemoryStream(c));
        await restarted.PutAsync(ArtifactKind.Package, new MemoryStream(c));
        await restarted.RegisterPackageAsync(candidate.Package, new([], PackageExtraction.Indexed, null, [], []), restarted);
        var afterCrash = await Restart(database, restarted);
        Assert.Equal(2, await new PackageIndexingService(afterCrash, afterCrash).SweepAsync());

        detail = Assert.IsType<CatalogPackageDetail>(
            await afterCrash.GetPackageDetailAsync("offline.a", "1.0.0", CatalogScope.Unrestricted));

        Assert.Equal(PackageExtraction.Indexed, detail.Summary.Extraction);
        Assert.Empty(detail.Activities.Activities);
        Assert.Equal(new[] { "offline.b", "offline.c" }, detail.Activities.Closure!.Select(item => item.Id));
        Assert.Equal(0, await new PackageIndexingService(afterCrash, afterCrash).SweepAsync());
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityCatalogScopesFiltersKeysetsAndCountsPrecedePaging(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var (fixture, contract) = await Fixture(store, "paging.base");

        for (var index = 0; index < 321; index++)
        {
            var id = "paging.p" + index.ToString("D3");

            var descriptor = contract.Activities[0] with
            {
                PackageId = id, Key = "key." + index.ToString("D3"), Category = index % 2 == 0 ? "Even" : "Odd",
                Name = index == 320 ? "literal_%[x]\\" : "Display"
            };

            await store.RegisterPackageAsync(
                fixture with { Id = id, Origin = index % 2 == 0 ? WorkflowPackageOrigin.Uploaded : WorkflowPackageOrigin.Resolved },
                contract with { Activities = [descriptor] }, store);
        }

        var scope = new CatalogScope(new([]), new(Prefixes: ["key.3"]), new(Prefixes: ["paging.p"]), new([]));
        var query = new CatalogActivityQuery(scope, Category: "Even", Origin: ActivityOrigin.Uploaded, Limit: 3);
        var seen = new List<string>();
        CatalogPage<CatalogActivity> page;

        do
        {
            page = await store.ListActivitiesAsync(query);
            seen.AddRange(page.Items.Select(item => item.Descriptor.Key));
            query = query with { After = page.NextCursor };
        } while (page.NextCursor != null);

        Assert.Equal(Enumerable.Range(300, 21).Where(index => index % 2 == 0).Select(index => "key." + index), seen);
        var first = await store.ListActivitiesAsync(new(scope, Limit: 2));
        Assert.NotNull(first.NextCursor);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ListActivitiesAsync(new(scope, Category: "Even", After: first.NextCursor, Limit: 2)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ListActivitiesAsync(new(CatalogScope.Unrestricted, After: first.NextCursor, Limit: 2)));

        Assert.Single((await store.ListActivitiesAsync(new(scope, Text: "_%[x]\\"))).Items);
        Assert.Empty((await store.ListActivitiesAsync(new(scope, PackageId: "paging.p001"))).Items);
        Assert.Empty((await store.ListActivitiesAsync(new(scope, ArtifactPresent: false))).Items);
        Assert.Single((await store.ListActivityVersionsAsync("key.320", new(scope))).Items);
        Assert.Single((await store.GetPackageActivitiesAsync("paging.p320", "1.0.0", new(scope))).Items);
        var detail = Assert.IsType<CatalogPackageDetail>(await store.GetPackageDetailAsync("paging.p001", "1.0.0", scope));
        Assert.Equal(0, detail.Summary.ActivityCount);
        Assert.Empty(detail.Activities.Activities);
        Assert.Null(await store.GetActivityAsync("key.001", "label", new("paging.p001", "1.0.0", fixture.Digest.Value), scope));
        var packages = await store.ListPackagesAsync(new(scope, Origin: WorkflowPackageOrigin.Uploaded, Limit: 5));
        Assert.Equal(5, packages.Items.Count);
        Assert.NotNull(packages.NextCursor);
        Assert.Single((await store.ListPackageVersionsAsync("paging.p320", new(scope))).Items);
        Assert.Equal(await store.GetCatalogRevisionAsync(), packages.CatalogRevision);
    }

    private static async Task<IWorkflowStateRepository> Restart(ProfileStore database, IWorkflowStateRepository store)
    {
        if (database.Profile == StoreProfiles.LocalSqlite && store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        return database.NewRepository();
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InstalledActivitiesHaveScopedLocalProvidersAndOnlyContentAdvancesRevision(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var subsetProvider = new ServiceCollection().AddIxIFlow().AddActivity<CatalogTestActivity>().Services.BuildServiceProvider();

        using var completeProvider = new ServiceCollection().AddIxIFlow().AddActivity<CatalogTestActivity>()
            .AddActivity<SecondCatalogActivity>().Services.BuildServiceProvider();

        var subset = Assert.Single(subsetProvider.GetRequiredService<IActivityRegistry>().Packages);
        var complete = Assert.Single(completeProvider.GetRequiredService<IActivityRegistry>().Packages);
        Assert.NotEqual(subset.Fingerprint, complete.Fingerprint);

        var member = new MemberRegistration(Guid.NewGuid(), MemberKind.Host, "catalog host", null, 1, "1.0.0", TimeSpan.FromSeconds(5),
            new Dictionary<string, string>()) { InstalledPackages = [subset] };

        var before = await store.GetCatalogRevisionAsync();
        await store.RegisterMemberAsync(member);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        await store.RegisterMemberAsync(member);
        Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities));
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        var scope = new CatalogScope(new([]), new(["catalog.fixture"]), new([subset.Id]), new([]));
        var page = await store.ListActivitiesAsync(new(scope, Origin: ActivityOrigin.Local));
        var local = Assert.Single(page.Items);
        Assert.False(local.ArtifactPresent);
        Assert.False(local.Withdrawn);
        Assert.Equal(subset.Fingerprint, local.Provider.Build);
        Assert.Null(local.Descriptor.ActivityType);

        Assert.NotNull(await store.GetActivityAsync(local.Descriptor.Key, local.Descriptor.Version,
            new(subset.Id.ToUpperInvariant(), subset.Version, subset.Fingerprint), scope));

        using var catalog = new WorkflowCatalog(store);
        Assert.Single(await catalog.GetPackageHostsAsync(subset.Id, subset.Version, subset.Fingerprint, scope));
        var original = subset.Activities[0].ToJson();
        var returned = Assert.Single(await store.GetInstalledPackagesAsync(subset.Id));

        Assert.Throws<NotSupportedException>(() =>
            ((IList<ActivityDescriptor>)returned.Activities)[0] = returned.Activities[0] with { Name = "Corrupt" });

        Assert.Equal(original, Assert.Single(await store.GetInstalledPackagesAsync(subset.Id)).Activities[0].ToJson());
        await store.RegisterMemberAsync(member with { InstalledPackages = [complete] });
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
        Assert.Equal(2, (await store.ListActivityVersionsAsync("catalog.fixture", new(scope))).Items.Count);
        Assert.Empty(await catalog.GetPackageHostsAsync(subset.Id, subset.Version, subset.Fingerprint, scope));
        Assert.Single(await catalog.GetPackageHostsAsync(complete.Id, complete.Version, complete.Fingerprint, scope));
        var denied = new CatalogScope(new([]), new([]), new([subset.Id]), new([]));
        Assert.Empty((await store.ListActivitiesAsync(new(denied))).Items);
        Assert.Empty(await catalog.GetPackageHostsAsync(subset.Id, subset.Version, null, denied));
        await store.UnregisterMemberAsync(member.Id);
        Assert.Empty(await catalog.GetPackageHostsAsync(subset.Id, subset.Version, null, scope));
        Assert.Equal(before + 2, await store.GetCatalogRevisionAsync());
        Assert.Equal(2, (await store.ListActivitiesAsync(new(scope, Origin: ActivityOrigin.Local))).Items.Count);
    }

    private static async Task<(WorkflowPackage Package, PackageActivities Activities)> Fixture(IWorkflowStateRepository store, string id)
    {
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2]));

        var package = new WorkflowPackage(id, "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = ArtifactDigest.Of([1]) }, []);

        var descriptor = ActivityDescriptor.OfType(typeof(CatalogTestActivity), id, package.Version);

        return (package, new([descriptor], PackageExtraction.Indexed, null, [], []));
    }

    [WorkflowActivity("catalog.fixture", Version = "label")]
    public sealed class CatalogTestActivity : IAsyncActivity
    {
        [ActivityInputOutput] public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [WorkflowActivity("catalog.second")]
    public sealed class SecondCatalogActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}