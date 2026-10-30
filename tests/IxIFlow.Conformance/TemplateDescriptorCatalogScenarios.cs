using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "F-14")]
[Trait("Spec", "H-03")]
public sealed class TemplateDescriptorCatalogScenarios
{
    private static readonly CatalogScope All = CatalogScope.Unrestricted;

    private static CatalogScope Scope(CatalogNames templates, CatalogNames? packages = null) =>
        new(new(), new(), packages ?? new(), templates);

    private static TemplateVersion Content(string key, int version = 1, string? display = null,
        IReadOnlyList<WorkflowPackageReference>? packages = null) => new(key, version, "liquid", TemplateFormat.Html,
        TemplateVersion.ModelTypeName(typeof(TemplateCatalogScenarios.Model)),
        WorkflowTypeIdentity.SavedName(typeof(TemplateCatalogScenarios.Model)),
        packages ?? [], display, "Description 😀", [new("de-AT", "Hallo {{ Name }}"), new("", "Hello {{ Name }}"), new("de", "Guten Tag")]);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ProjectionsAndDetailMatchImmutableContentAndMaskRelatedPackages(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var visible = await TemplateCatalogScenarios.Package(store, "model.visible", []);
        var hidden = await TemplateCatalogScenarios.Package(store, "model.hidden", []);

        var content = Content("receipt", 7, "Receipt 😀",
            [new(hidden.Id, hidden.Version, hidden.Digest), new(visible.Id, visible.Version, visible.Digest)]);

        var draft = await store.SaveDefinitionAsync(new(content.Key, content.Version, WorkflowDraftKinds.Template, "source"), null);
        var entry = await store.PublishTemplateDefinitionAsync(draft.Id, content, store, "publisher");

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        store = database.NewRepository();
        using var catalog = new WorkflowCatalog(GuardedStateRepository.Wrap(store));
        var scope = Scope(new(["receipt"]), new([visible.Id]));
        var page = await catalog.ListTemplatesAsync(new(scope));
        var summary = Assert.Single(page.Items);

        var expected = new CatalogTemplate(content.Key, content.Version, content.Engine, content.Format.ToString(), content.ModelType,
            content.ModelTypeId,
            [new(visible.Id, visible.Version, visible.Digest)], content.Variants.Select(item => item.Culture).ToArray(),
            content.DisplayName, content.Description);

        Assert.Equal(Json(expected), Json(summary));
        Assert.Equal(Json(summary), Json(Assert.Single((await catalog.ListTemplateVersionsAsync("receipt", new(scope))).Items)));
        var detail = (await catalog.GetTemplateDetailAsync("receipt", 7, scope))!;
        Assert.Equal(Json(summary), Json(detail.Summary));
        Assert.Equal(draft.Id, detail.PublishedDraft);
        Assert.Equal(entry.PublishedAtUtc, detail.PublishedAtUtc);
        Assert.Equal(DateTimeKind.Utc, detail.PublishedAtUtc.Kind);
        Assert.Equal("publisher", detail.PublishedBy);
        Assert.Null(page.NextCursor);
        Assert.Equal(await store.GetCatalogRevisionAsync(), page.CatalogRevision);
        Assert.Equal(2, (await store.GetTemplateVersionAsync("receipt", 7))!.Version.ModelPackages.Count);
        Assert.Empty((await catalog.GetTemplateDetailAsync("receipt", 7, Scope(new(["receipt"]), new([]))))!.Summary.ModelPackages);
        Assert.Null(await catalog.GetTemplateDetailAsync("receipt", 7, Scope(new([]))));
        Assert.Null(await catalog.GetTemplateDetailAsync("missing", 1, All));
        Assert.Null(await catalog.GetTemplateDetailAsync("receipt", 8, All));
        var before = await store.GetCatalogRevisionAsync();
        var packages = Assert.IsAssignableFrom<IList<WorkflowPackageReference>>(summary.ModelPackages);
        Assert.Throws<NotSupportedException>(() => packages.Clear());
        var cultures = Assert.IsAssignableFrom<IList<string>>(summary.Cultures);
        Assert.Throws<NotSupportedException>(() => cultures.Clear());
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopeAndAllFiltersApplyBeforeGlobalPagingAndCounts(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        for (var index = 0; index < 321; index++)
            await store.RegisterTemplateVersionAsync(Content($"item.{index:000}", display: index % 2 == 0 ? "Even" : "Odd"), store);

        await store.RegisterTemplateVersionAsync(Content("item.319", 2, "Even"), store);
        var scope = Scope(new(["item.319"], ["item.32"]));
        var rows = new List<CatalogTemplate>();
        var query = new CatalogTemplateQuery(scope, Text: "even", Engine: " LIQUID ", Limit: 1);

        do
        {
            var page = await store.ListTemplatesAsync(query);
            Assert.Single(page.Items);
            rows.AddRange(page.Items);
            Assert.Equal(await store.GetCatalogRevisionAsync(), page.CatalogRevision);
            query = query with { After = page.NextCursor };
        } while (query.After != null);

        Assert.Equal(new[] { ("item.319", 2), ("item.320", 1) }, rows.Select(row => (row.Key, row.Version)));
        Assert.Equal(3, (await store.ListTemplatesAsync(new(scope))).Items.Count);
        Assert.Equal(322, (await store.ListTemplatesAsync(new(All, Limit: 1000))).Items.Count);
        Assert.Empty((await store.ListTemplatesAsync(new(scope, Text: "Odd", Engine: "other"))).Items);
        Assert.Empty((await store.ListTemplatesAsync(new(Scope(new([]))))).Items);
        Assert.Empty((await store.ListTemplateVersionsAsync("item.000", new(scope, Limit: 1))).Items);
        Assert.Null(await store.GetTemplateDetailAsync("item.000", 1, scope));
        Assert.Equal(new[] { 1, 2 }, (await store.ListTemplateVersionsAsync("item.319", new(scope))).Items.Select(row => row.Version));

        // Every allowed name/prefix can be supplied together without approaching SQL Server's parameter ceiling.
        var maximumScope = Scope(new(Enumerable.Range(0, 100).Select(i => $"item.{i:000}").ToArray(),
            Enumerable.Range(0, 100).Select(i => $"absent.{i:000}").ToArray()));

        Assert.Equal(100, (await store.ListTemplatesAsync(new(maximumScope, Limit: 1000))).Items.Count);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NumericVersionKeysetsHaveNoDuplicatesAndTolerateLaterWrites(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        foreach (var version in new[] { 10, 2, 1, 100, int.MaxValue })
            await store.RegisterTemplateVersionAsync(Content("a", version), store);

        await store.RegisterTemplateVersionAsync(Content("b"), store);
        var global = await store.ListTemplatesAsync(new(All, Limit: 2));
        Assert.Equal(new[] { 1, 2 }, global.Items.Select(row => row.Version));
        Assert.NotNull(global.NextCursor);
        var revision = global.CatalogRevision;
        await store.RegisterTemplateVersionAsync(Content("a", 3), store);
        var next = await store.ListTemplatesAsync(new(All, After: global.NextCursor, Limit: 3));
        Assert.Equal(new[] { 3, 10, 100 }, next.Items.Select(row => row.Version));
        Assert.True(next.CatalogRevision > revision);
        var final = await store.ListTemplatesAsync(new(All, After: next.NextCursor, Limit: 2));
        Assert.Equal(new[] { ("a", int.MaxValue), ("b", 1) }, final.Items.Select(row => (row.Key, row.Version)));
        Assert.Null(final.NextCursor);
        var versions = new List<int>();
        string? cursor = null;

        do
        {
            var page = await store.ListTemplateVersionsAsync("a", new(All, cursor, 2));
            versions.AddRange(page.Items.Select(row => row.Version));
            cursor = page.NextCursor;
        } while (cursor != null);

        Assert.Equal(new[] { 1, 2, 3, 10, 100, int.MaxValue }, versions);
        var empty = await store.ListTemplatesAsync(new(All, After: next.NextCursor, Limit: 1000));
        Assert.Equal(2, empty.Items.Count);
        Assert.Null(empty.NextCursor);
        Assert.Empty((await store.ListTemplateVersionsAsync("missing", new(All))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task VersionPagesTraverseHundredsOfRowsAndCursorKeysAreValidated(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        for (var version = 321; version > 0; version--)
            await store.RegisterTemplateVersionAsync(Content("many", version), store);

        var scope = Scope(new(["many"]));
        var versions = new List<int>();
        string? cursor = null;

        do
        {
            var page = await store.ListTemplateVersionsAsync("many", new(scope, cursor, 19));
            versions.AddRange(page.Items.Select(row => row.Version));
            cursor = page.NextCursor;
        } while (cursor != null);

        Assert.Equal(Enumerable.Range(1, 321), versions);
        var first = await store.ListTemplateVersionsAsync("many", new(scope, Limit: 1));
        var json = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(Convert.FromBase64String(first.NextCursor!))!;

        foreach (var invalid in new[] { "0", "-1", "2147483648", "x" })
        {
            json["Key"] = new System.Text.Json.Nodes.JsonArray(invalid);
            var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplateVersionsAsync("many", new(scope, forged)));
        }

        var global = await store.ListTemplatesAsync(new(scope, Limit: 1));
        json = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(Convert.FromBase64String(global.NextCursor!))!;
        json["Key"] = new System.Text.Json.Nodes.JsonArray("bad key", "1");

        await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplatesAsync(new(scope,
            After: Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())))));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CursorsBindNormalizedFiltersScopeEndpointAndTemplateKey(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterTemplateVersionAsync(Content("a", 1, "Name"), store);
        await store.RegisterTemplateVersionAsync(Content("a", 2, "Name"), store);
        await store.RegisterTemplateVersionAsync(Content("b", 1, "Name"), store);
        var first = await store.ListTemplatesAsync(new(All, Text: " NAME ", Engine: " LIQUID ", Limit: 1));
        Assert.Single((await store.ListTemplatesAsync(new(All, Text: "name", Engine: "liquid", After: first.NextCursor, Limit: 1))).Items);

        foreach (var query in new[]
                 {
                     new CatalogTemplateQuery(All, After: first.NextCursor),
                     new(All, Text: "name", Engine: "other", After: first.NextCursor),
                     new(Scope(new(["a"])), Text: "name", Engine: "liquid", After: first.NextCursor), new(All, After: "invalid")
                 })
            await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplatesAsync(query));

        var version = await store.ListTemplateVersionsAsync("a", new(All, Limit: 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplateVersionsAsync("b", new(All, version.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplatesAsync(new(All, After: version.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplateVersionsAsync("a", new(All, first.NextCursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplateVersionsAsync("a", new(Scope(new([])), version.NextCursor)));

        foreach (var limit in new[] { 0, 1001 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListTemplatesAsync(new(All, Limit: limit)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListTemplateVersionsAsync("a", new(All, Limit: limit)));
        }

        foreach (var value in new[] { "bad\0text", "bad\ud800" })
        {
            Assert.Throws<ArgumentException>(() => new CatalogTemplateQuery(All, Text: value));
            Assert.Throws<ArgumentException>(() => new CatalogTemplateQuery(All) with { Engine = value });
            await Assert.ThrowsAsync<ArgumentException>(() => store.ListTemplateVersionsAsync(value, new(All)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetTemplateDetailAsync(value, 1, All));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListTemplatesAsync(new(All, Engine: new string('a', 51))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListTemplatesAsync(new(All, Text: new string('a', 401))));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LiteralSearchPreservesUnicodeAndDoesNotSearchContent(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterTemplateVersionAsync(Content("wild", display: "Literal _%[x]\\ 😀"), store);
        await store.RegisterTemplateVersionAsync(Content("plain", display: "Literal anything x"), store);
        Assert.Equal("wild", Assert.Single((await store.ListTemplatesAsync(new(All, Text: "_%[X]\\"))).Items).Key);
        Assert.Equal("wild", Assert.Single((await store.ListTemplatesAsync(new(All, Text: "😀"))).Items).Key);
        Assert.Empty((await store.ListTemplatesAsync(new(All, Text: "Hello"))).Items);
        Assert.Empty((await store.ListTemplatesAsync(new(All, Text: "Description"))).Items);
        var prefix = Scope(new(Prefixes: ["w%"]));
        Assert.Empty((await store.ListTemplatesAsync(new(prefix))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BuiltInDescriptorReadsAreIndependentOfScopeAndReturnDetachedDependentMembers(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var catalog = new WorkflowCatalog(GuardedStateRepository.Wrap(store));
        var before = await store.GetCatalogRevisionAsync();
        var denied = new CatalogScope(new([]), new([]), new([]), new([]));
        var descriptor = Assert.Single(await catalog.ListStepDescriptorsAsync(denied), item => item.Kind == "renderTemplate");
        Assert.Equal(Json(TemplateStepDescriptors.RenderTemplate()), Json(descriptor));
        Assert.Equal("renderTemplate", descriptor.Kind);
        Assert.Equal("Render template", descriptor.DisplayName);
        Assert.Equal("Templates", descriptor.Category);
        Assert.Equal("file-text", descriptor.Icon);
        Assert.Equal(new[] { "Template", "Model", "Culture" }, descriptor.Inputs.Select(member => member.Name));
        var model = descriptor.Inputs[1];
        Assert.True(model.Required);
        Assert.Null(model.Type);
        Assert.Null(model.TypeName);
        Assert.Equal("template.ModelType", model.TypeFrom);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(TemplateReference)), descriptor.Inputs[0].Type);
        Assert.Equal("string?", descriptor.Inputs[2].TypeName);
        Assert.False(descriptor.Inputs[2].Required);
        var output = Assert.Single(descriptor.Outputs);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(RenderedTemplate)), output.Type);
        Assert.Equal("IxIFlow.Templates.RenderedTemplate?", output.TypeName);
        var bound = TemplateStepDescriptors.Bind(Content("choice"));
        Assert.Equal(Content("choice").ModelTypeId, bound.Inputs[1].Type);
        Assert.Null(bound.Inputs[1].TypeFrom);

        if (descriptor.Inputs is IList<StepMemberDescriptor> mutable && !mutable.IsReadOnly)
            mutable[1] = model with { TypeFrom = "tampered" };

        Assert.Equal("template.ModelType",
            Assert.Single(await catalog.ListStepDescriptorsAsync(All), item => item.Kind == "renderTemplate").Inputs[1].TypeFrom);

        Assert.Empty((await catalog.ListTemplatesAsync(new(denied))).Items);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NewReaderForwardsPreserveTheStoreFailureChannel(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);

        // The proxy fails before touching a provider; the same catalog/guard contract applies to every store.
        var guarded = GuardedStateRepository.Wrap(System.Reflection.DispatchProxy
            .Create<IWorkflowStateRepository, TemplateCatalogScenarios.ThrowingStore>());

        using var catalog = new WorkflowCatalog(guarded);

        foreach (var call in new Func<Task>[]
                 {
                     () => catalog.ListTemplatesAsync(new(All)),
                     () => catalog.ListTemplateVersionsAsync("receipt", new(All)), () => catalog.GetTemplateDetailAsync("receipt", 1, All),
                     () => catalog.ListStepDescriptorsAsync(All)
                 })
            Assert.Equal(WorkflowErrorKind.Infrastructure, (await Assert.ThrowsAsync<WorkflowEngineException>(call)).Kind);
    }

    public static TheoryData<string> Relational => new(StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory));

    [Theory, MemberData(nameof(Relational))]
    public async Task PagesNeverReadContentJsonAndDetailVerifiesItAfterScope(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterTemplateVersionAsync(Content("receipt"), store);
        await using var connection = database.NewConnection();
        var (table, column) = Sql(database, "metadata_json");
        await connection.ExecuteAsync($"UPDATE {table} SET {column}='invalid-json'");
        Assert.Single((await store.ListTemplatesAsync(new(All))).Items);
        Assert.Single((await store.ListTemplateVersionsAsync("receipt", new(All))).Items);
        Assert.Null(await store.GetTemplateDetailAsync("receipt", 1, Scope(new([]))));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetTemplateDetailAsync("receipt", 1, All));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    public static TheoryData<string, string> Projections
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var profile in StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory))
            foreach (var part in new[] { "model_packages_json", "cultures_json", "model_type_id", "search_text", "format", "engine" })
                data.Add(profile, part);

            return data;
        }
    }

    [Theory, MemberData(nameof(Projections))]
    public async Task PagesRefuseMalformedOrNoncanonicalProjections(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterTemplateVersionAsync(Content("receipt"), store);
        await using var connection = database.NewConnection();
        var (table, column) = Sql(database, part);

        await connection.ExecuteAsync($"UPDATE {table} SET {column}=@Value",
            new { Value = part == "cultures_json" ? "[\"de\",\"\"]" : "corrupt" });

        Assert.Equal(WorkflowErrorKind.ArtifactMismatch,
            (await Assert.ThrowsAsync<WorkflowEngineException>(() => store.ListTemplatesAsync(new(All)))).Kind);

        Assert.Equal(WorkflowErrorKind.ArtifactMismatch,
            (await Assert.ThrowsAsync<WorkflowEngineException>(() => store.ListTemplateVersionsAsync("receipt", new(All)))).Kind);
    }

    private static (string Table, string Column) Sql(ProfileStore database, string column) =>
        database.Profile == StoreProfiles.DistributedSqlServer
            ? ("dbo.IxIFlowTemplateVersions",
                "[" + string.Concat(column.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) + "]")
            : ("ixiflow_template_versions", "\"" + column + "\"");
}