using System.Globalization;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "F-14")]
[Trait("Spec", "H-03")]
public sealed class TemplatePublisherScenarios
{
    private static readonly Lazy<Dictionary<string, byte[]>> Packages = new(() =>
    {
        var leaf = PublishingFeed.Emit("Template.Leaf",
            "namespace TemplateLeaf; public class Base { public string Name {get;set;} = \"\"; }");

        var argument = PublishingFeed.Emit("Template.Argument", "namespace TemplateArguments; public class Order : TemplateLeaf.Base {}",
            leaf);

        var models = PublishingFeed.Emit("Template.Models", """
                                                            using System.Text.Json.Serialization;
                                                            namespace TemplateModels;
                                                            public class Envelope<T> { public T Value {get;set;} }
                                                            [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
                                                            [JsonDerivedType(typeof(Derived), "derived")]
                                                            public class Surface { public string Name {get;set;} = ""; }
                                                            public class Derived : Surface { public string Secret {get;set;} = "hidden"; }
                                                            public struct ValueModel { public string Name {get;set;} }
                                                            public record struct RecordModel(string Name);
                                                            public class Outer { public class Nested { public string Name {get;set;} = ""; } }
                                                            public interface IReadSurface { string Name {get;} }
                                                            public abstract class AbstractSurface { public string Name {get;set;} = ""; }
                                                            internal class Hidden { public string Name {get;set;} }
                                                            public class Throwing { public string Name {get;set;} public Throwing() { throw new System.InvalidOperationException("private constructor detail"); } }
                                                            public class Slow { public Slow() { System.Threading.Thread.Sleep(700); } public string Name {get;set;} }
                                                            """);

        return new()
        {
            ["template.leaf"] = PublishingFeed.Package("template.leaf", new() { ["lib/net10.0/Template.Leaf.dll"] = leaf }),
            ["template.argument"] = PublishingFeed.Package("template.argument", new() { ["lib/net10.0/Template.Argument.dll"] = argument },
                "<dependency id=\"template.leaf\" version=\"[1.0.0]\"/>"),
            ["template.models"] = PublishingFeed.Package("template.models", new() { ["lib/net10.0/Template.Models.dll"] = models })
        };
    });

    private static PublishingFeed Feed()
    {
        var feed = new PublishingFeed();

        foreach (var (id, bytes) in Packages.Value)
            File.WriteAllBytes(Path.Combine(feed.Folder, id + ".1.0.0.nupkg"), bytes);

        return feed;
    }

    private static string Yaml(string key = "notice", string model = "TemplateModels.Surface", string content = "Hello {{ Name }}") =>
        $"template: {{key: {key}, version: 1, engine: liquid, format: html, model: '{model}'}}\nimports: {{catalogs: [template.models@1.0.0, template.argument@1.0.0]}}\ncontent: |\n  {content}\n";

    private static TemplatePreviewRequest Request(string json = "{\"Name\":\"Ana\"}", string? culture = null) =>
        new(culture, JsonSerializer.Deserialize<JsonElement>(json));

    private static WorkflowPublisher Publisher(IWorkflowCatalogStore catalog, IArtifactStore artifacts, PublishingFeed feed,
        ITemplateEngine? engine = null, WorkflowPublisherOptions? options = null) => new(catalog, artifacts,
        options ?? new() { NuGetConfigPath = feed.Config }, templateEngines: new([engine ?? new LiquidTemplateEngine()]));

    private static void Valid(WorkflowValidationReport report) => Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
    private static void Success(WorkflowPublishResult result) => Assert.True(result.Success, string.Join("; ", result.Diagnostics));

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FeedBackedStoredAndUnsavedDryRunAndPreviewNeverWriteOrRefreshArtifacts(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", Yaml()), null);
        var revision = await store.GetCatalogRevisionAsync();
        var catalog = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var artifacts = new ReadOnlyArtifacts(store);
        var publisher = Publisher(catalog, artifacts, feed);
        Valid(await publisher.ValidateAsync(draft));
        Valid(await publisher.ValidateAsync(draft with { Generation = Guid.Empty, Revision = 0 }));
        var preview = await publisher.PreviewTemplateAsync(draft, Request());
        Assert.Empty(preview.Diagnostics);
        Assert.Equal("Hello Ana\n", preview.Rendered?.Content);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, artifacts.Writes);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Empty(await store.GetTemplateVersionsAsync());
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "template", "notice", 1))).Items);
        Assert.Equal(draft, await store.GetDefinitionAsync("template", "notice", 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task GenericProbePinsModelArgumentAndTransitiveClosureAndReturnsAuditedWholeReceipt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();

        var yaml = Yaml(model: "TemplateModels.Envelope<TemplateArguments.Order>", content: "{{ Value.Name }}") +
            "cultures:\n  de: Bestellung {{ Value.Name }}\n";

        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", yaml), null, "author");
        var publisher = Publisher(store, store, feed);
        Valid(await publisher.ValidateAsync(draft));
        var preview = await publisher.PreviewTemplateAsync(draft, Request("{\"Value\":{\"Name\":\"Ana\"}}", "de-AT"));
        Assert.Empty(preview.Diagnostics);
        Assert.Equal("de", preview.Rendered?.Culture);
        Assert.Equal("Bestellung Ana", preview.Rendered?.Content);
        var first = await publisher.PublishAsync(draft.Id, "first");
        Success(first);
        var entry = Assert.IsType<TemplateVersionEntry>(first.Template);
        Assert.Equal(draft.Id, entry.PublishedDraft);
        Assert.Equal("first", entry.PublishedBy);
        Assert.Equal(DateTimeKind.Utc, entry.PublishedAtUtc.Kind);

        Assert.Equal(new[] { "template.argument", "template.leaf", "template.models" },
            entry.Version.ModelPackages.Select(item => item.Id));

        Assert.Contains("Envelope<global::TemplateArguments.Order>", entry.Version.ModelType);
        Assert.Contains("Template.Argument", entry.Version.ModelTypeId);

        foreach (var pin in entry.Version.ModelPackages)
            Assert.True(await store.ExistsAsync(pin.Digest));

        Assert.Null(await store.GetDefinitionAsync("template", "notice", 1));
        var revision = await store.GetCatalogRevisionAsync();
        var replay = await new WorkflowPublisher(store, new ReadOnlyArtifacts(store)).PublishAsync(draft.Id, "later");
        Success(replay);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        var otherGeneration = await publisher.PublishAsync(draft.Id with { Generation = Guid.NewGuid() });
        Assert.Equal("PUB001", Assert.Single(otherGeneration.Diagnostics).Code);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LiquidCultureDiagnosticsUseLiteralContentLinesAndFoldedScalarStart(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var yaml = Yaml() + "cultures:\n  de: |\n    Guten Tag\n    {{ Missing }}\n";
        var report = await Publisher(store, new ReadOnlyArtifacts(store), feed).ValidateAsync(new("notice", 1, "template", yaml));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("TPL006", diagnostic.Code);
        Assert.Equal(8, diagnostic.Line);
        Assert.Equal("template.yaml", diagnostic.Path);

        var folded = await Publisher(store, new ReadOnlyArtifacts(store), feed)
            .ValidateAsync(new("notice", 1, "template", yaml.Replace("de: |", "de: >")));

        Assert.Equal(6, Assert.Single(folded.Diagnostics).Line);
        Assert.Empty(await store.GetPackagesAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PreviewUsesDeclaredPolymorphicSurfaceAndHtmlEncoding(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var publisher = Publisher(store, new ReadOnlyArtifacts(store), feed);
        var draft = new WorkflowDraft("notice", 1, "template", Yaml());

        var preview = await publisher.PreviewTemplateAsync(draft,
            Request("{\"$type\":\"derived\",\"Name\":\"<Ana>\",\"Secret\":\"private\"}"));

        Assert.Empty(preview.Diagnostics);
        Assert.Equal("Hello &lt;Ana&gt;\n", preview.Rendered?.Content);

        var invisible = await publisher.PreviewTemplateAsync(draft with { Source = Yaml(content: "{{ Secret }}") },
            Request("{\"$type\":\"derived\",\"Name\":\"Ana\"}"));

        Assert.Equal("TPL006", Assert.Single(invisible.Diagnostics).Code);
        Assert.Null(invisible.Rendered);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PreviewJsonConstructionCultureAndUtf8BoundsHavePositionedSafeDiagnostics(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var catalog = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var artifacts = new ReadOnlyArtifacts(store);

        foreach (var (model, json) in new[]
                 {
                     ("TemplateModels.Surface", "null"), ("TemplateModels.Surface", "[]"),
                     ("TemplateModels.Throwing", "{}"), ("TemplateModels.IReadSurface", "{\"Name\":\"Ana\"}"),
                     ("TemplateModels.AbstractSurface", "{}")
                 })
        {
            var result = await Publisher(catalog, artifacts, feed)
                .PreviewTemplateAsync(new("notice", 1, "template", Yaml(model: model)), Request(json));

            var error = Assert.Single(result.Diagnostics);
            Assert.Equal("TPL021", error.Code);
            Assert.Equal(1, error.Line);
            Assert.DoesNotContain("private constructor detail", error.Message);
        }

        var draft = new WorkflowDraft("notice", 1, "template", Yaml());

        Assert.Equal("TPL021",
            Assert.Single((await Publisher(catalog, artifacts, feed).PreviewTemplateAsync(draft, new(null, default))).Diagnostics).Code);

        Assert.Equal("TPL003",
            Assert.Single((await Publisher(catalog, artifacts, feed).PreviewTemplateAsync(draft, Request(culture: "not-a-culture")))
                .Diagnostics).Code);

        var modelBound = Publisher(catalog, artifacts, feed, options: new() { NuGetConfigPath = feed.Config, MaxPreviewModelBytes = 10 });
        var modelError = Assert.Single((await modelBound.PreviewTemplateAsync(draft, Request())).Diagnostics);
        Assert.Equal("TPL018", modelError.Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Limit, modelError.Class);
        var outputBound = Publisher(catalog, artifacts, feed, options: new() { NuGetConfigPath = feed.Config, MaxPreviewOutputBytes = 10 });
        var outputError = Assert.Single((await outputBound.PreviewTemplateAsync(draft, Request("{\"Name\":\"éééé\"}"))).Diagnostics);
        Assert.Equal("TPL018", outputError.Code);
        Assert.Equal(3, outputError.Line);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, artifacts.Writes);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FullGenerationRetrySaveAndDeleteRecreateDuringValidationCannotPublishAnOldDraft(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();

        foreach (var recreate in new[] { false, true })
        {
            var key = recreate ? "recreated" : "edited";
            var draft = await store.SaveDefinitionAsync(new(key, 1, "template", Yaml(key)), null);
            var gate = new GateEngine();
            var work = Publisher(store, store, feed, gate).PublishAsync(draft.Id);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            WorkflowDraft current;

            try
            {
                if (recreate)
                {
                    Assert.True(await store.DeleteDefinitionAsync(draft.Id));
                    current = await store.SaveDefinitionAsync(new(key, 1, "template", Yaml(key)), null);
                }
                else
                    current = await store.SaveDefinitionAsync(draft with { Source = Yaml(key, content: "changed {{ Name }}") }, draft.Id);
            }
            finally
            {
                gate.Release.TrySetResult();
            }

            var refused = await work;
            Assert.False(refused.Success);
            Assert.Equal("PUB040", Assert.Single(refused.Diagnostics).Code);
            Assert.Null(await store.GetTemplateVersionAsync(key, 1));
            Assert.Equal(current.Id, (await store.GetDefinitionAsync("template", key, 1))!.Id);
            var result = await Publisher(store, store, feed).PublishAsync(current.Id);
            Success(result);
            Assert.Equal(current.Id, result.Template!.PublishedDraft);
            Assert.Equal("PUB001", Assert.Single((await Publisher(store, store, feed).PublishAsync(draft.Id)).Diagnostics).Code);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CompetingPublishersReturnFirstWinningContentAuditAndReceipt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", Yaml()), null);
        var gate = new GateEngine();
        var losing = Publisher(store, store, feed, gate).PublishAsync(draft.Id, "losing");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        WorkflowPublishResult winner;

        try
        {
            winner = await Publisher(store, store, feed).PublishAsync(draft.Id, "winning");
            Success(winner);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var revision = await store.GetCatalogRevisionAsync();
        var replay = await losing;
        Success(replay);
        Assert.Equal(winner.Receipt, replay.Receipt);
        Assert.Equal("winning", replay.Template!.PublishedBy);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Single(await store.GetTemplateVersionsAsync());

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.SaveDefinitionAsync(new(draft.Name, draft.Version, draft.Kind, draft.Source), null));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CodeRegistrationCollisionIsTpl007BeforeAcquisitionAndAtFinalCommit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", Yaml()), null);
        var gate = new GateEngine();
        var work = Publisher(store, store, feed, gate).PublishAsync(draft.Id);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            await store.RegisterTemplateVersionAsync(
                TemplateVersion.Create<WorkflowDemand>("notice", 1, "liquid", TemplateFormat.Text, new TemplateVariant("", "Code")), store);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var result = await work;
        Assert.Equal("TPL007", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Conflict, result.FailureClass);
        var catalog = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var early = await new WorkflowPublisher(catalog, new ReadOnlyArtifacts(store)).PublishAsync(draft.Id);
        Assert.Equal("TPL007", Assert.Single(early.Diagnostics).Code);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync("template", "notice", 1))!.Id);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawalAfterPreparationIsRecheckedInTheFinalTemplateTransaction(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", Yaml()), null);
        var gate = new GateEngine();
        var work = Publisher(store, store, feed, gate).PublishAsync(draft.Id);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            var package = (await store.GetPackageAsync("template.models", "1.0.0"))!;
            await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision), "withdrawer");
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.False((await work).Success);
        Assert.Null(await store.GetTemplateVersionAsync("notice", 1));
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync("template", "notice", 1))!.Id);
        var again = await Publisher(store, store, feed).ValidateAsync(draft);
        Assert.Equal("PUB025", Assert.Single(again.Diagnostics).Code);
    }

    [Fact]
    public async Task ModelShapesBindAsFullExpressionsAndUnsupportedRootsAreTpl005()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = Feed();
        var publisher = Publisher(store, new ReadOnlyArtifacts(store), feed);

        foreach (var model in new[]
                 {
                     "TemplateModels.ValueModel", "TemplateModels.RecordModel", "TemplateModels.Outer.Nested",
                     "TemplateModels.IReadSurface", "TemplateModels.AbstractSurface"
                 })
            Valid(await publisher.ValidateAsync(new("notice", 1, "template", Yaml(model: model))));

        foreach (var model in new[]
                 {
                     "object", "dynamic", "string", "int", "System.DayOfWeek", "TemplateModels.Surface[]",
                     "System.Collections.Generic.List<TemplateModels.Surface>",
                     "System.Collections.Generic.Dictionary<string, TemplateModels.Surface>", "TemplateModels.Envelope<>",
                     "TemplateModels.Hidden", "Missing.Model"
                 })
        {
            var report = await publisher.ValidateAsync(new("notice", 1, "template", Yaml(model: model)));
            Assert.Equal("TPL005", Assert.Single(report.Diagnostics).Code);
            Assert.Equal(1, report.Diagnostics[0].Line);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SaveAndPublishCompeteForTheSameFullDraftIdentity(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = Feed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", Yaml()), null);
        var gate = new GateEngine();
        var publication = Publisher(store, store, feed, gate).PublishAsync(draft.Id);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var revision = await store.GetCatalogRevisionAsync();

        var save = Task.Run(async () =>
        {
            try
            {
                return (Draft: await store.SaveDefinitionAsync(draft with { Source = Yaml(content: "edited {{ Name }}") }, draft.Id),
                    Error: (Exception?)null);
            }
            catch (Exception error)
            {
                return (Draft: (WorkflowDraft?)null, Error: error);
            }
        });

        gate.Release.TrySetResult();
        var saved = await save;
        var published = await publication;
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());

        if (published.Success)
        {
            Assert.Null(saved.Draft);
            Assert.IsType<WorkflowEngineException>(saved.Error);
            Assert.Equal(draft.Id, published.Template!.PublishedDraft);
            Assert.Null(await store.GetDefinitionAsync("template", "notice", 1));
        }
        else
        {
            Assert.Null(saved.Error);
            Assert.NotNull(saved.Draft);
            Assert.Equal("PUB040", Assert.Single(published.Diagnostics).Code);
            Assert.Equal(saved.Draft.Id, (await store.GetDefinitionAsync("template", "notice", 1))!.Id);
            Assert.Null(await store.GetTemplateVersionAsync("notice", 1));
        }
    }

    [Fact]
    public async Task PreviewHonorsLiquidExecutionLimitsAndTheExactOutputByteBoundary()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = Feed();
        var draft = new WorkflowDraft("notice", 1, "template", Yaml(content: "{% for i in (1..20) %}{{ Name }}{% endfor %}"));
        var engine = new LiquidTemplateEngine(new() { MaxSteps = 3 });
        var result = await Publisher(store, new ReadOnlyArtifacts(store), feed, engine).PreviewTemplateAsync(draft, Request());
        Assert.Null(result.Rendered);
        Assert.Equal("PUB042", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Limit, Assert.Single(result.Diagnostics).Class);

        var exact = Publisher(store, new ReadOnlyArtifacts(store), feed,
            options: new() { NuGetConfigPath = feed.Config, MaxPreviewOutputBytes = 11 });

        var atLimit = await exact.PreviewTemplateAsync(draft with { Source = Yaml().Replace("format: html", "format: text") },
            Request("{\"Name\":\"éé\"}"));

        Assert.Empty(atLimit.Diagnostics);
        Assert.Equal(11, Encoding.UTF8.GetByteCount(atLimit.Rendered!.Content));
        Assert.Empty(await store.GetPackagesAsync());
    }

    public sealed class SourceModel
    {
        public List<SourceItem> Items { get; set; } = [];
        public string Name { get; set; } = "";
    }

    public sealed class SourceItem
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public async Task LiquidLocationsIgnoreTextCommentsRawStringsAndScopedDeclarations()
    {
        var source = """
                     plain item Missing not_a_filter
                     {% comment %}{{ Missing }}{% endcomment %}
                     {% raw %}{{ Missing ' }}{% endraw %}
                     {% for item in Items %}{{ item.Name }}{% endfor %}
                     {{ item.Name }}
                     {{ Name | not_a_filter }}
                     """;

        var diagnostics =
            await new LiquidTemplateEngine().ValidateAsync(new("notice", 1, "de", TemplateFormat.Text, source), typeof(SourceModel));

        Assert.Equal(new int?[] { 5, 6 }, diagnostics.Select(item => item.Line));
        Assert.All(diagnostics, diagnostic => Assert.True(diagnostic.Column > 0));
    }

    [Theory]
    [InlineData("TemplateModels.Surface", false, false)]
    [InlineData("TemplateModels.Surface", true, false)]
    [InlineData("TemplateModels.ValueModel", true, true)]
    [InlineData("TemplateModels.Surface", true, true)]
    public async Task PreviewReleasesTheCollectibleModelContextWhileTheEngineStaysAlive(string model, bool preview, bool liquid)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = Feed();
        var engine = new TrackingEngine { UseLiquid = liquid };
        var publisher = Publisher(store, new ReadOnlyArtifacts(store), feed, engine);
        var draft = new WorkflowDraft("notice", 1, "template", Yaml(model: model));

        if (preview)
        {
            var result = await publisher.PreviewTemplateAsync(draft, Request());
            Assert.Empty(result.Diagnostics);
            Assert.NotNull(result.Rendered);
        }
        else
            Valid(await publisher.ValidateAsync(draft));

        Assert.NotNull(engine.Context);

        for (var index = 0; index < 150 && engine.Context.IsAlive; index++)
        {
            // As in the existing bundle/activity collection tests, advance the runtime's
            // metadata/accessor caches before testing that our owners release the context.
            // The runtime accessor cache has a one-second sliding expiry.
            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(SourceModel),
                new JsonSerializerOptions());

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Assert.False(engine.Context.IsAlive);
        GC.KeepAlive(engine);
        GC.KeepAlive(publisher);
    }

    [Fact]
    public async Task MissingAndUnknownEnginesIdentityAndImportsFailBeforeAcquisition()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, new ReadOnlyArtifacts(store));
        var draft = new WorkflowDraft("notice", 1, "template", Yaml());
        Assert.Equal("TPL012", Assert.Single((await publisher.ValidateAsync(draft)).Diagnostics).Code);

        Assert.Equal("TPL001",
            Assert.Single((await publisher.ValidateAsync(draft with { Source = Yaml().Replace("engine: liquid", "engine: unknown") }))
                .Diagnostics).Code);

        Assert.Equal("PUB005", Assert.Single((await publisher.ValidateAsync(draft with { Name = "other" })).Diagnostics).Code);

        Assert.Equal("TPL008",
            Assert.Single((await publisher.ValidateAsync(draft with
            {
                Source = Yaml().Replace("imports: {catalogs:", "imports: {workflows: [Other@1], catalogs:")
            })).Diagnostics).Code);

        foreach (var member in new[] { "displayName", "description" })
        {
            var invalid = Yaml().Replace("engine: liquid", member + ": \"bad\\u0000text\", engine: liquid");
            Assert.Equal("IXIYAML001", Assert.Single((await publisher.ValidateAsync(draft with { Source = invalid })).Diagnostics).Code);
        }

        Assert.Empty(await store.GetPackagesAsync());
    }

    [Fact]
    public async Task TimeoutAndCancellationCoverValidationModelConstructionAndRenderWithoutWrites()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = Feed();
        var catalog = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var artifacts = new ReadOnlyArtifacts(store);

        foreach (var render in new[] { false, true })
        {
            var gate = new GateEngine { GateRender = render };

            var publisher = Publisher(catalog, artifacts, feed, gate,
                new() { NuGetConfigPath = feed.Config, BuildTimeout = TimeSpan.FromMilliseconds(400) });

            try
            {
                var result = await publisher.PreviewTemplateAsync(new("notice", 1, "template", Yaml()), Request());
                Assert.True(gate.Entered.Task.IsCompleted);
                Assert.Null(result.Rendered);
                var diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal("PUB043", diagnostic.Code);
                Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, diagnostic.Class);
            }
            finally
            {
                gate.Release.TrySetResult();
            }
        }

        var slow = Publisher(catalog, artifacts, feed,
            options: new() { NuGetConfigPath = feed.Config, BuildTimeout = TimeSpan.FromMilliseconds(400) });

        Assert.Equal("PUB043",
            Assert.Single((await slow.PreviewTemplateAsync(new("notice", 1, "template", Yaml(model: "TemplateModels.Slow")), Request()))
                .Diagnostics).Code);

        var cancelGate = new GateEngine();
        using var cancellation = new CancellationTokenSource();

        var pending = Publisher(catalog, artifacts, feed, cancelGate)
            .PreviewTemplateAsync(new("notice", 1, "template", Yaml()), Request(), cancellation.Token);

        await cancelGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancelGate.Release.TrySetResult();
        }

        Assert.Equal(0, probe.Writes);
        Assert.Equal(0, artifacts.Writes);
        Assert.Empty(await store.GetTemplateVersionsAsync());
    }

    private sealed class GateEngine : ITemplateEngine
    {
        public string Name => "liquid";
        public bool GateRender;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource source, Type type,
            CancellationToken token = default)
        {
            if (!GateRender)
            {
                Entered.TrySetResult();
                await Release.Task;
            }

            return [];
        }

        public async ValueTask<string> RenderAsync(TemplateSource source, object value, Type type, CultureInfo culture,
            CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task;

            return "rendered";
        }
    }

    private sealed class TrackingEngine : ITemplateEngine
    {
        private readonly LiquidTemplateEngine _inner = new();
        public string Name => "liquid";
        public WeakReference? Context;
        public bool UseLiquid;

        public ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource source, Type type,
            CancellationToken token = default)
        {
            Context = new(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly)!);

            return UseLiquid ? _inner.ValidateAsync(source, type, token) : ValueTask.FromResult<IReadOnlyList<TemplateDiagnostic>>([]);
        }

        public ValueTask<string> RenderAsync(TemplateSource source, object value, Type type, CultureInfo culture,
            CancellationToken token = default) =>
            UseLiquid ? _inner.RenderAsync(source, value, type, culture, token) : ValueTask.FromResult("rendered");
    }

    private sealed class ReadOnlyArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public int Writes;

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes);

            throw new InvalidOperationException("Unexpected artifact write or age refresh");
        }

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) =>
            throw new InvalidOperationException("Unexpected artifact delete");

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected artifact purge");

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
    }
}