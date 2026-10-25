using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
public sealed class CatalogTextNulScenarios
{
    private const string Nul = "Visible\0hidden";
    private static CatalogScope Scope => CatalogScope.Unrestricted;

    public static TheoryData<string, string> MetadataFields
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (var profile in StoreProfiles.ProfileNames)
                foreach (var property in new[] { "DisplayName", "Description" }) rows.Add(profile, property);
            return rows;
        }
    }

    private static TemplateVersion Template(string? display = "Receipt", string? description = "Help", string content = "Hello {{ Name }}") =>
        new("receipt", 1, "liquid", TemplateFormat.Text, TemplateVersion.ModelTypeName(typeof(Model)),
            WorkflowTypeIdentity.SavedName(typeof(Model)), [], display, description, [new("", content)]);

    private static TemplateVersion InvalidMetadata(string field) =>
        Template(field == "DisplayName" ? Nul : "Receipt", field == "Description" ? Nul : "Help");

    [Theory, MemberData(nameof(MetadataFields))]
    public async Task CodePublicationRefusesNulMetadataBeforeRegistration(string profile, string field)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var before = await store.GetCatalogRevisionAsync();
        var publisher = new TemplatePublisher(store, store, new([new LiquidTemplateEngine()]));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync<Model>(InvalidMetadata(field)));
        Assert.StartsWith("Catalog text cannot contain U+0000", error.Message);
        Assert.Equal("value", error.ParamName);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterTemplateVersionAsync(InvalidMetadata(field), store));
        Assert.Null(await store.GetTemplateVersionAsync("receipt", 1));
        Assert.Empty(await store.GetTemplateVersionsAsync());
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(MetadataFields))]
    public async Task DraftPublicationRefusesNulMetadataAndKeepsTheExactDraft(string profile, string field)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        // Authoring source encodes NUL; the decoded projected value must still be refused.
        var source = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = Nul });
        var first = await store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, source), null, "author");
        var draft = await store.SaveDefinitionAsync(first with { Source = source + " " }, first.Id, "editor");
        var before = await store.GetCatalogRevisionAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                store.PublishTemplateDefinitionAsync(draft.Id, InvalidMetadata(field), store, "publisher"));
            Assert.StartsWith("Catalog text cannot contain U+0000", error.Message);
            Assert.Equal("value", error.ParamName);
            Assert.Null(await store.GetTemplateVersionAsync("receipt", 1));
            Assert.Empty(await store.GetTemplateVersionsAsync());
            Assert.Equal(before, await store.GetCatalogRevisionAsync());
            Assert.Equal(draft, await store.GetDefinitionAsync(WorkflowDraftKinds.Template, "receipt", 1));
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task JsonEscapedVariantNulSurvivesCodeAndDraftPublication(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new TemplatePublisher(store, store, new([new LiquidTemplateEngine()]));
        var content = Template(content: "Hello\0{{ Name }}");
        var json = TemplateJson.Serialize(content);
        Assert.DoesNotContain((byte)0, json);
        Assert.Contains("\\u0000", Encoding.UTF8.GetString(json));
        await publisher.PublishAsync<Model>(content);
        Assert.True(content.IsSameAs((await store.GetTemplateVersionAsync("receipt", 1))!.Version));
        var draft = await store.SaveDefinitionAsync(new("draft", 1, WorkflowDraftKinds.Template, "escaped source"), null);
        var draftContent = new TemplateVersion("draft", 1, content.Engine, content.Format, content.ModelType, content.ModelTypeId,
            [], content.DisplayName, content.Description, content.Variants);
        var before = await store.GetCatalogRevisionAsync();
        await store.PublishTemplateDefinitionAsync(draft.Id, draftContent, store);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        Assert.Null(await store.GetDefinitionAsync(WorkflowDraftKinds.Template, "draft", 1));
        if (profile == StoreProfiles.LocalSqlite && store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var reopened = database.NewRepository();
        Assert.True(content.IsSameAs((await reopened.GetTemplateVersionAsync("receipt", 1))!.Version));
        Assert.True(draftContent.IsSameAs((await reopened.GetTemplateVersionAsync("draft", 1))!.Version));
    }

    private static WorkflowVersion WorkflowRow()
    {
        var contract = WorkflowContract.OfType(typeof(Model));
        return new("Workflow", 1, "structure", contract.ToJson(), contract.Hash,
            new("Workflow", "Help", [], "folder", "Examples", WorkflowTypeIdentity.SavedName(typeof(Model)),
                WorkflowTypeIdentity.CSharpName(typeof(Model))), []);
    }

    private static string SavedType(string assembly) => $"ixi-type:1:N{assembly.Length}:{assembly}6:Sample0:";

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WorkflowDescriptorAndIdentityTextIsRefusedWithoutMutation(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var row = WorkflowRow();
        var descriptor = row.Descriptor;
        var descriptors = new[]
        {
            descriptor with { DisplayName = Nul }, descriptor with { Description = Nul },
            descriptor with { Category = Nul }, descriptor with { Icon = Nul }, descriptor with { Tags = [Nul] },
            descriptor with { DataType = SavedType(Nul), DataTypeName = "global::Sample" },
            descriptor with { DataTypeName = Nul }
        };
        var before = await store.GetCatalogRevisionAsync();
        foreach (var invalid in descriptors)
        {
            AssertRefusal(Record.Exception(() => WorkflowDescriptor.Parse(JsonSerializer.Serialize(invalid))));
            AssertRefusal(await Record.ExceptionAsync(() => store.RegisterWorkflowVersionAsync(row with { Descriptor = invalid })));
        }
        foreach (var invalid in new[] { row with { Name = Nul }, row with { StructureHash = Nul },
                     row with { PublishedBy = Nul }, row with { StateChangedBy = Nul }, row with { ManifestJson = Nul } })
            await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterWorkflowVersionAsync(invalid));
        Assert.Null(await store.GetWorkflowVersionAsync(row.Name, 1));
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityDescriptorFieldsRefuseNulBeforePackageOrInstalledRegistration(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2]));
        var package = new WorkflowPackage("nul.package", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = ArtifactDigest.Of([1]) }, []);
        var descriptor = ActivityDescriptor.OfType(typeof(PackageDescriptorCatalogScenarios.CatalogTestActivity), package.Id, package.Version);
        var before = await store.GetCatalogRevisionAsync();
        foreach (var invalid in new[]
        {
            descriptor with { Key = Nul }, descriptor with { Version = Nul }, descriptor with { Name = Nul },
            descriptor with { Category = Nul }, descriptor with { Description = Nul }, descriptor with { Icon = Nul },
            descriptor with { Designer = Nul }, descriptor with { MetadataTypeName = Nul }, descriptor with { CSharpTypeName = Nul }
        })
        {
            var json = JsonSerializer.Serialize(invalid);
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, Assert.Throws<WorkflowEngineException>(() => ActivityDescriptor.Parse(json)).Kind);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(package,
                new([invalid], PackageExtraction.Indexed, null, [], []), store));
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            // The installed snapshot validates descriptors before comparing the supplied fingerprint.
            var member = new MemberRegistration(Guid.NewGuid(), MemberKind.Host, "host", null, 1, "1.0.0",
                TimeSpan.FromSeconds(5), new Dictionary<string, string>())
            { InstalledPackages = [new(package.Id, package.Version, new string('0', 64), [invalid])] };
            Assert.Equal(WorkflowErrorKind.InvalidDefinition,
                (await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterMemberAsync(member))).Kind);
            Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
            Assert.Empty(await store.GetInstalledPackagesAsync());
            Assert.Empty((await store.ListActivitiesAsync(new(Scope))).Items);
            Assert.Equal(before, await store.GetCatalogRevisionAsync());
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ExtractionDetailDraftSourceAndAuditTextRefuseNulBeforeMutation(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1]));
        var package = new WorkflowPackage("nul.package", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = digest }, []);
        var draft = await store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, "source"), null, "author");
        var before = await store.GetCatalogRevisionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterPackageAsync(package,
            new([], PackageExtraction.Pending, Nul, null, []), store));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterPackageAsync(package,
            new([], PackageExtraction.Pending, null, null, []), store, Nul));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterPackageAsync(package with { RegisteredBy = Nul },
            new([], PackageExtraction.Pending, null, null, []), store));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(draft with { Source = Nul }, draft.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(draft, draft.Id, Nul));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterTemplateVersionAsync(Template(), store, Nul));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishTemplateDefinitionAsync(draft.Id, Template(), store, Nul));
        var member = new MemberRegistration(Guid.NewGuid(), MemberKind.Host, "host", null, 1, "1.0.0",
            TimeSpan.FromSeconds(5), new Dictionary<string, string>());
        foreach (var invalid in new[] { member with { Name = Nul }, member with { EngineVersion = Nul }, member with { Endpoint = Nul } })
            await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterMemberAsync(invalid));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), current => current.Id == member.Id);
        Assert.Equal(draft, await store.GetDefinitionAsync(WorkflowDraftKinds.Template, "receipt", 1));
        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        Assert.Empty(await store.GetTemplateVersionsAsync());
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CodeWorkflowDescriptorsRefuseNulBeforeRegistration(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var catalog = new WorkflowCatalog(store);
        var before = await store.GetCatalogRevisionAsync();
        foreach (var set in new Action<WorkflowDefinition>[]
        {
            definition => definition.DisplayName = Nul, definition => definition.Description = Nul,
            definition => definition.Category = Nul, definition => definition.Icon = Nul, definition => definition.Tags = [Nul]
        })
        {
            var definition = Workflow.Create<Model>("Code").Delay(TimeSpan.FromMinutes(1)).Build();
            set(definition);
            AssertRefusal(Record.Exception(() => WorkflowDescriptor.Of(definition)));
            AssertRefusal(await Record.ExceptionAsync(() => catalog.RegisterWorkflowAsync(definition)));
        }
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.Empty(await catalog.GetWorkflowDefinitionsAsync());
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task QueryTextNamesKeysPrefixesAndProviderSelectorsRefuseNul(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var before = await store.GetCatalogRevisionAsync();
        foreach (var call in new Func<Task>[]
        {
            () => store.ListWorkflowsAsync(new(Scope, Text: Nul)),
            () => store.ListWorkflowsAsync(new(Scope, Category: Nul)),
            () => store.ListWorkflowsAsync(new(Scope, Tag: Nul)),
            () => store.ListActivitiesAsync(new(Scope, Text: Nul)),
            () => store.ListActivitiesAsync(new(Scope, Category: Nul)),
            () => store.ListActivitiesAsync(new(Scope, PackageId: Nul)),
            () => store.ListPackagesAsync(new(Scope, Text: Nul)),
            () => store.GetDefinitionsAsync(new(Scope, NamePrefix: Nul)),
            () => store.GetWorkflowVersionAsync(Nul, 1),
            () => store.ListWorkflowVersionsAsync(Nul, new(Scope)),
            () => store.ListActivityVersionsAsync(Nul, new(Scope)),
            () => store.GetActivityAsync(Nul, "1", new("package", "1.0.0", "build"), Scope),
            () => store.GetActivityAsync("activity", Nul, new("package", "1.0.0", "build"), Scope),
            () => store.GetActivityAsync("activity", "1", new("package", Nul, "build"), Scope),
            () => store.GetActivityAsync("activity", "1", new("package", "1.0.0", Nul), Scope),
            () => store.GetPackageAsync(Nul, "1.0.0"), () => store.GetPackageAsync("package", Nul),
            () => store.GetPackagesAsync(Nul), () => store.GetPackagePageAsync("package", Nul, 10),
            () => store.GetPackageDetailAsync("package", Nul, Scope),
            () => store.GetPackageActivitiesAsync("package", Nul, new(Scope)),
            () => store.GetPackageExtractionPageAsync(PackageExtraction.Pending, Nul, 10),
            () => store.GetPendingPackageDependentsAsync("package", Nul, 10)
        })
            await Assert.ThrowsAsync<ArgumentException>(call);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Fact]
    public void ScopeQueryCopiesReadersAndTypeIdentitiesUseTheSameTextRule()
    {
        foreach (var invalid in new[] { Nul, "bad\ud800", "bad\udc00" })
        {
            for (var family = 0; family < 4; family++)
                foreach (var prefix in new[] { false, true })
                {
                    var names = Enumerable.Range(0, 4).Select(index => index == family
                        ? prefix ? new CatalogNames(Prefixes: [invalid]) : new CatalogNames([invalid]) : new CatalogNames()).ToArray();
                    Assert.Throws<ArgumentException>(() => new CatalogScope(names[0], names[1], names[2], names[3]));
                }
            Assert.Throws<ArgumentException>(() => new CatalogWorkflowQuery(Scope) with { Text = invalid });
            Assert.Throws<ArgumentException>(() => new CatalogActivityQuery(Scope) with { Category = invalid });
            Assert.Throws<ArgumentException>(() => new CatalogPackageQuery(Scope) with { Text = invalid });
            Assert.Throws<ArgumentException>(() => new WorkflowDraftQuery(Scope) with { NamePrefix = invalid });
            Assert.Throws<ArgumentException>(() => new WorkflowPublicationQuery(Scope, "template", "receipt", 1) with { Name = invalid });
            Assert.Throws<ArgumentException>(() => new ActivityProviderSelector("package", "1.0.0", "build") with { Build = invalid });
            Assert.Throws<ArgumentException>(() => Template(display: invalid));
            Assert.Throws<ArgumentException>(() => Template(description: invalid));
            Assert.Throws<ArgumentException>(() => new TemplateVersion("receipt", 1, "liquid", TemplateFormat.Text,
                "global::Sample", SavedType(invalid), [], null, null, [new("", "content")]));
        }
        var content = Template();
        var json = Encoding.UTF8.GetString(TemplateJson.Serialize(content)).Replace("Receipt", "Receipt\\u0000hidden", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => TemplateJson.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<ArgumentException>(() => new TemplateVariant("", "bad\ud800"));
        Assert.Equal("safe\0\U0001f600", new TemplateVariant("", "safe\0\U0001f600").Content);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ForgedCursorKeysAreValidatedAfterDecoding(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterWorkflowVersionAsync(WorkflowRow());
        await store.RegisterWorkflowVersionAsync(WorkflowRow() with { Name = "Second" });
        var page = await store.ListWorkflowsAsync(new(Scope, Limit: 1));
        using var cursor = JsonDocument.Parse(Convert.FromBase64String(page.NextCursor!));
        foreach (var invalid in new[] { Nul, "bad\ud800" })
        {
            // Write the lone surrogate as a JSON escape so serialization cannot replace it.
            var key = invalid == Nul ? "Visible\\u0000hidden" : "bad\\ud800";
            var json = "{\"Version\":1,\"Filter\":\"" + cursor.RootElement.GetProperty("Filter").GetString() + "\",\"Key\":[\"" + key + "\"]}";
            var after = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ListWorkflowsAsync(new(Scope, After: after, Limit: 1)));
        }
    }

    private static void AssertRefusal(Exception? error) => Assert.True(
        error is ArgumentException or WorkflowEngineException { Kind: WorkflowErrorKind.InvalidDefinition }, error?.ToString() ?? "Expected refusal");

    public sealed class Model { public string Name { get; set; } = "Ana"; }

}
