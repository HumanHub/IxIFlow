using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "H-03")]
[Trait("Spec", "F-14")]
public sealed class TemplateModelScenarios
{
    [Fact]
    public void EngineCachesDoNotHoldACollectibleDerivedRuntimeType()
    {
        var engine = new LiquidTemplateEngine();
        var weak = RenderCollectibleDerived(engine);
        for (var index = 0; index < 12 && weak.IsAlive; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RenderCollectibleDerived(LiquidTemplateEngine engine)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new("TemplateCollectible" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("model").DefineType("DerivedModel", TypeAttributes.Public, typeof(Data)).CreateType()!;
        var value = (Data)Activator.CreateInstance(type)!; value.Name = "Ana";
        Assert.Equal("Ana", engine.RenderAsync(new("weak", 1, "", TemplateFormat.Text, "{{ Name }}"), value, typeof(Data), CultureInfo.InvariantCulture).GetAwaiter().GetResult());
        return new(type);
    }

    [Fact]
    public async Task SharedProfileSendActivityKeyPublishesWithoutAPackageImport()
    {
        var store = new InMemoryWorkflowStateRepository();
        var yaml = """
            workflow:
              name: SharedSend
              version: 1
              dataType: SendData
            definitions:
              - kind: activity
                activity: ixiflow.templates.send-message
                input:
                  - target: Channel
                    from: { kind: constant, value: email }
                  - target: Recipients
                    from: { kind: path, path: workflow.Recipients }
                  - target: Body
                    from: { kind: path, path: workflow.Body }
            """;
        var draft = await store.SaveDefinitionAsync(new("SharedSend", 1, WorkflowDraftKinds.Workflow, yaml), null);
        var options = new WorkflowPublisherOptions(); options.SourceOptions.Types["SendData"] = TemplateVersion.ModelTypeName(typeof(SendData));
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }
    public static TheoryData<Type> Supported => new(typeof(Envelope<Data>), typeof(ValueModel), typeof(RecordModel), typeof(Nested.Model), typeof(IReadSurface), typeof(AbstractSurface));
    public static TheoryData<Type> Unsupported => new(typeof(object), typeof(string), typeof(int), typeof(Guid), typeof(DateTime), typeof(DayOfWeek),
        typeof(Data[]), typeof(List<Data>), typeof(Dictionary<string, Data>), typeof(Envelope<>), typeof(CollectionRoot), typeof(Hidden), typeof(Untyped), typeof(UntypedDictionary), typeof(Action));

    [Theory, MemberData(nameof(Supported))]
    public void ClosedObjectReadSurfacesHaveCanonicalPortableIdentities(Type type)
    {
        TemplateModelTypes.Validate(type);
        var version = new TemplateVersion("model", 1, "liquid", TemplateFormat.Text, TemplateVersion.ModelTypeName(type), WorkflowTypeIdentity.SavedName(type), [], null, null, [new("", "content")]);
        var bytes = TemplateJson.Serialize(version);
        Assert.Equal(bytes, TemplateJson.Serialize(TemplateJson.Deserialize(bytes)));
        Assert.DoesNotContain("Digest", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("Reference", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Same(type, WorkflowTypeIdentity.Resolve(version.ModelTypeId));
        Assert.DoesNotContain('+', version.ModelType);
    }

    [Theory, MemberData(nameof(Unsupported))]
    public void UnsupportedRootModelsAreRefused(Type type) => Assert.Throws<ArgumentException>(() => TemplateModelTypes.Validate(type));

    [Fact]
    public void BaseCultureCanonicalizationAndKeyRulesAreDeterministic()
    {
        var value = TemplateVersion.Create<Data>("valid.key-1", 1, "liquid", TemplateFormat.Text, new("DE", "German"), new("", "Base"));
        Assert.Equal(new[] { "", "de" }, value.Variants.Select(item => item.Culture));
        Assert.Equal("German", value.Resolve(CultureInfo.GetCultureInfo("de-AT")).Content);
        Assert.Equal("Base", value.Resolve(CultureInfo.GetCultureInfo("fr-FR")).Content);
        Assert.Equal(TemplateVariant.DigestOf("Base"), value.Variants[0].Digest);
        Assert.Throws<ArgumentException>(() => TemplateVersion.Create<Data>("key", 1, "liquid", TemplateFormat.Text, new TemplateVariant("de", "missing base")));
        Assert.Throws<ArgumentException>(() => TemplateVersion.Create<Data>("key", 1, "liquid", TemplateFormat.Text, new("", "base"), new("de", "a"), new("DE", "b")));
        foreach (var key in new[] { "UPPER", "key ", " key", "bad/key", new string('x', 101), "" }) Assert.Throws<ArgumentException>(() => new TemplateReference(key, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateReference("key", 0));
        Assert.Throws<ArgumentException>(() => new TemplateVariant("not-a-culture", "x"));
        Assert.Throws<ArgumentException>(() => TemplateModelTypes.Validate(new { Name = "hidden" }.GetType()));
    }

    [Fact]
    public async Task ExplicitDeclaredTypesHideRuntimeOnlyMembersAndSupportStructsAndInterfaces()
    {
        var engine = new LiquidTemplateEngine();
        var model = new Derived { Name = "Ana", Secret = "hidden" };
        var source = new TemplateSource("surface", 1, "", TemplateFormat.Text, "[{{ Name }}][{{ Secret }}]");
        Assert.Equal("[Ana][]", await engine.RenderAsync(source, model, typeof(Data), CultureInfo.InvariantCulture));
        Assert.Equal("[Ana][]", await engine.RenderAsync(source, model, typeof(IReadSurface), CultureInfo.InvariantCulture));
        Assert.Equal("[Ana][]", await engine.RenderAsync(source, new RecordModel("Ana"), typeof(RecordModel), CultureInfo.InvariantCulture));
        Assert.Equal("[Ana][]", await engine.RenderAsync(source, new ValueModel { Name = "Ana" }, typeof(ValueModel), CultureInfo.InvariantCulture));
        Assert.Equal("[Ana][]", await engine.RenderAsync(source, new Nested.Model { Name = "Ana" }, typeof(Nested.Model), CultureInfo.InvariantCulture));
        var generic = new TemplateSource("generic", 1, "", TemplateFormat.Text, "{{ Value.Name }}");
        Assert.Empty(await engine.ValidateAsync(generic, typeof(Envelope<Data>)));
        Assert.Equal("Ana", await engine.RenderAsync(generic, new Envelope<Data> { Value = model }, typeof(Envelope<Data>), CultureInfo.InvariantCulture));
        var html = new TemplateSource("html", 1, "", TemplateFormat.Html, "{{ Name }} {{ Total }}");
        Assert.Equal("&lt;b&gt; 2,5", await engine.RenderAsync(html, new Data { Name = "<b>", Total = 2.5m }, typeof(Data), CultureInfo.GetCultureInfo("de-DE")));
        var limited = new LiquidTemplateEngine(new() { MaxSteps = 3 });
        await Assert.ThrowsAsync<TemplateRenderException>(() => limited.RenderAsync(new("limit", 1, "", TemplateFormat.Text, "{% for x in (1..100) %}{{ x }}{% endfor %}"), model, typeof(Data), CultureInfo.InvariantCulture).AsTask());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CodePublisherValidatesEveryVariantAndBindsEveryTypeArgumentPackage(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var dependency = await TemplateCatalogScenarios.Package(store, "closure.dependency", []);
        var assembly = typeof(Data).Assembly;
        var bytes = await File.ReadAllBytesAsync(assembly.Location);
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([4, 5, 6]));
        var package = new WorkflowPackage("models", "1.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["lib/net10.0/" + assembly.GetName().Name + ".dll"] = ArtifactDigest.Of(bytes) }, []);
        await store.RegisterPackageAsync(package, new([], PackageExtraction.Indexed, null, [new(dependency.Id, dependency.Version, dependency.Digest)], []), store);
        var publisher = new TemplatePublisher(store, store, new TemplateEngines([new LiquidTemplateEngine()]));
        // Envelope<T> and T live in different assemblies; the generic argument's package must still be found.
        var value = TemplateVersion.Create<System.Tuple<Data>>("generic", 1, "liquid", TemplateFormat.Text, new("", "{{ Item1.Name }}"), new("de", "{{ Item1.Missing }}"));
        await Assert.ThrowsAsync<TemplateValidationException>(() => publisher.PublishAsync<System.Tuple<Data>>(value));
        Assert.Empty(await store.GetTemplateVersionsAsync());
        var valid = TemplateVersion.Create<System.Tuple<Data>>("generic", 1, "liquid", TemplateFormat.Text, new TemplateVariant("", "{{ Item1.Name }}"));
        await publisher.PublishAsync<System.Tuple<Data>>(valid);
        var saved = (await store.GetTemplateVersionAsync("generic", 1))!.Version;
        Assert.Equal(new[] { "closure.dependency", "models" }, saved.ModelPackages.Select(item => item.Id));
        Assert.Equal("Ana", (await new TemplateRenderer(store, new([new LiquidTemplateEngine()])).RenderAsync(saved.Reference, Tuple.Create(new Data { Name = "Ana" }), null)).Content);
        await store.RegisterPackageAsync(package with { Id = "models.copy" }, new([], PackageExtraction.Indexed, null, [], []), store);
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync<Tuple<Data>>(valid));
        // Exact supplied pins resolve equal-byte package ownership without guessing a version.
        await publisher.PublishAsync<Tuple<Data>>(saved);
    }

    [Fact]
    public async Task CodeTemplateRendersAfterRecreatingTheSqliteServiceProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), "ixiflow-template-" + Guid.NewGuid().ToString("N") + ".db");
        ServiceProvider Provider()
        {
            var services = new ServiceCollection();
            services.AddIxIFlow().UseSqlite("Data Source=" + path).AddIxIFlowTemplates().AddLiquid();
            return services.BuildServiceProvider();
        }
        try
        {
            await using (var first = Provider())
            {
                await first.GetRequiredService<TemplatePublisher>().PublishAsync<Data>(TemplateVersion.Create<Data>("cold", 1, "liquid", TemplateFormat.Text,
                    new("", "Hello {{ Name }}"), new("de", "Hallo {{ Name }}")));
                var descriptor = first.GetRequiredService<IActivityRegistry>().Find("ixiflow.templates.send-message")!;
                Assert.Equal("1.0", descriptor.Version); Assert.Equal("Templates", descriptor.Category); Assert.Equal("send", descriptor.Icon);
                Assert.All(descriptor.Inputs.Concat(descriptor.Outputs), member => Assert.NotNull(member.DisplayName));
            }
            await using var second = Provider();
            var result = await second.GetRequiredService<TemplateRenderer>().RenderAsync(new("cold", 1), new Data { Name = "Ana" }, "de-AT");
            Assert.Equal("Hallo Ana", result.Content); Assert.Equal("de", result.Culture);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    }

    public class Data : IReadSurface { public string Name { get; set; } = ""; public decimal Total { get; set; } }
    public sealed class SendData
    {
        public List<string> Recipients { get; set; } = ["ana@example.com"];
        public RenderedTemplate Body { get; set; } = new("source", 1, "", TemplateFormat.Text, "body");
    }
    public sealed class Derived : Data { public string Secret { get; set; } = ""; }
    public sealed class Envelope<T> { public T Value { get; set; } = default!; }
    public struct ValueModel { public string Name { get; set; } }
    public record struct RecordModel(string Name);
    public static class Nested { public sealed class Model { public string Name { get; set; } = ""; } }
    public interface IReadSurface { string Name { get; } }
    public abstract class AbstractSurface { public abstract string Name { get; } }
    private sealed class Hidden { public string Name { get; set; } = ""; }
    public sealed class Untyped { public object? Value { get; set; } }
    public sealed class UntypedDictionary { public Dictionary<string, object> Values { get; set; } = []; }
    public sealed class CollectionRoot : IEnumerable<int> { public IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
}
