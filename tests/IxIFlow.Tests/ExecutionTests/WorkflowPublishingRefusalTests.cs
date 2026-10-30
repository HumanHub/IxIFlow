using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class WorkflowPublishingRefusalTests
{
    private const string Secret = "private implementation detail";
    private static readonly string[] Classes = ["author", "limit", "unavailable", "internal"];
    public static IEnumerable<object[]> Cases => Classes.Select(value => new object[] { value });
    public static IEnumerable<object[]> UploadCases => Cases.Append(["malformed"]);

    public static IEnumerable<object[]> ValidationCases => new[] { "workflow", "activity", "template", "preview" }
        .SelectMany(stage => Classes.Select(value => new object[] { stage, value }));

    private static (string Code, WorkflowPublishDiagnosticClass Class) Expected(string failure) => failure switch
    {
        "author" => ("PUB003", WorkflowPublishDiagnosticClass.Authoring),
        "malformed" => ("PUB004", WorkflowPublishDiagnosticClass.Authoring),
        "limit" => ("PUB042", WorkflowPublishDiagnosticClass.Limit),
        "unavailable" => ("PUB041", WorkflowPublishDiagnosticClass.Unavailable),
        _ => ("PUB007", WorkflowPublishDiagnosticClass.Internal)
    };

    private static void Check(WorkflowPublishDiagnostic diagnostic, string failure, string? authorText = null)
    {
        Assert.Equal(Expected(failure), (diagnostic.Code, diagnostic.Class));

        if (failure == "author")
            Assert.Contains(authorText ?? "Duplicate package import", diagnostic.Message);
        else
            Assert.DoesNotContain(Secret, diagnostic.Message);

        if (failure == "internal")
            Assert.Equal("An unexpected publication failure occurred", diagnostic.Message);
    }

    private static async Task<(InMemoryWorkflowStateRepository Store, IWorkflowCatalogStore Catalog,
        WorkflowPublisherOptions Options, string Imports)> ResolverCase(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();

        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(
            ActivityPackageFixture.Package("refusal.one", "1.0.0", new())));

        var imports = failure == "author" ? "refusal.one@1.0.0, refusal.one@1.0.0" : "refusal.one@1.0.0";

        var options = failure switch
        {
            "limit" => new WorkflowPublisherOptions { MaxPackageResolutionBytes = 1 },
            "unavailable" => new WorkflowPublisherOptions
                { NuGetConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing.config") },
            _ => new WorkflowPublisherOptions()
        };

        IWorkflowCatalogStore catalog = store;

        if (failure == "internal")
        {
            var broken = new Mock<IWorkflowCatalogStore>();

            broken.Setup(value =>
                    value.GetPackageAsync(It.IsAny<string>(), It.IsAny<string>(), cancellationToken: Moq.It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(Secret));

            catalog = broken.Object;
        }

        return (store, catalog, options, imports);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task ResolveClassifiesExpectedRefusalsAndRedactsInternalErrors(string failure)
    {
        var setup = await ResolverCase(failure);

        var error = await Record.ExceptionAsync(async () =>
        {
            using var packages = await new WorkflowPackageResolver(setup.Catalog, setup.Store, setup.Options)
                .ResolveAsync(setup.Imports.Split(", ").ToList(), PackageAcquisition.Scratch, [], default);
        });

        Assert.NotNull(error);
        Check(WorkflowPublishFailures.Classify(error, WorkflowPublishStage.Validate), failure);

        if (failure == "author")
            Assert.IsType<WorkflowPublishException>(error);

        if (failure is "limit" or "unavailable")
            Assert.IsType<WorkflowEngineException>(error);
    }

    [Theory, MemberData(nameof(ValidationCases))]
    public async Task ValidationAndPreviewKeepRefusalClasses(string kind, string failure)
    {
        var setup = await ResolverCase(failure);

        // A fault during package lookup must occur after the kind-specific identity checks.
        IWorkflowCatalogStore catalog = setup.Catalog;
        var imports = setup.Imports;

        if (kind == "activity" && failure == "author")
        {
            var image = WorkflowCompilation.Compile("public sealed class ReferenceOnly;").Assembly;

            await new WorkflowPublisher(setup.Store, setup.Store).UploadPackageAsync(new MemoryStream(
                ActivityPackageFixture.Package("refusal.reference-only", "1.0.0", new() { ["ref/net10.0/ReferenceOnly.dll"] = image })));

            imports = "refusal.reference-only@1.0.0";
        }

        if (failure == "internal" && kind is "activity" or "template" or "preview")
        {
            var broken = new Mock<IWorkflowCatalogStore>();

            broken.Setup(value => value.GetPackageAsync("refusal.one", "1.0.0", cancellationToken: Moq.It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(Secret));

            catalog = broken.Object;
        }

        var source = kind switch
        {
            "activity" => "activity: {key: refusal.activity, version: 1}\nbody: |\n  return;\n",
            "template" or "preview" =>
                "template: {key: refusal.template, version: 1, engine: liquid, model: System.Object, format: text}\ncontent: hello\n",
            _ => "workflow: {name: Refusal, version: 1, dataType: System.Object}\ndefinitions: []\n"
        };

        source += kind == "activity" ? "packages: [" + imports + "]\n" : "imports: {catalogs: [" + imports + "]}\n";

        var draft = new WorkflowDraft(
            kind == "activity" ? "refusal.activity" : kind is "template" or "preview" ? "refusal.template" : "Refusal",
            1, kind == "preview" ? "template" : kind, source);

        var publisher = new WorkflowPublisher(catalog, setup.Store, setup.Options,
            templateEngines: new IxIFlow.Templates.TemplateEngines([new IxIFlow.Templates.Liquid.LiquidTemplateEngine()]));

        var diagnostics = kind == "preview"
            ? (await publisher.PreviewTemplateAsync(draft, new(null, System.Text.Json.JsonSerializer.SerializeToElement(new { }))))
            .Diagnostics
            : (await publisher.ValidateAsync(draft)).Diagnostics;

        var diagnostic = Assert.Single(diagnostics);

        // Activity dependency refusals already have the more specific shared code.
        if (kind == "activity" && failure == "author")
        {
            Assert.Equal("PUB020", diagnostic.Code);
            Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, diagnostic.Class);
            Assert.Contains("runtime implementation", diagnostic.Message);
        }
        else
            Check(diagnostic, failure);

        Assert.Equal(WorkflowPublishStage.Validate, diagnostic.Stage);
        Assert.Empty(await setup.Store.GetWorkflowVersionsAsync("Refusal"));
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task TestStagePreservesCuratedRefusalsAndRedactsErrorsWithSourceFrames(string failure)
    {
        var expression = failure switch
        {
            "limit" => "new IxIFlow.Core.WorkflowEngineException(IxIFlow.Core.WorkflowErrorKind.ResourceExhausted, \"" + Secret + "\")",
            "unavailable" => "new IxIFlow.Core.WorkflowEngineException(IxIFlow.Core.WorkflowErrorKind.ArtifactMismatch, \"" + Secret +
                "\")",
            _ => "new System.InvalidOperationException(\"" + Secret + "\")"
        };

        var compiled =
            WorkflowCompilation.Compile(
                "public static class RefusalBuild { public static IxIFlow.Core.WorkflowDefinition Build() {\n#line 12 \"workflow.yaml\"\nthrow " +
                expression + ";\n} }");

        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));

        PackageScopeClaim[] claims = failure == "author"
            ?
            [
                new(typeof(WorkflowDefinition).Assembly.GetName().Name!, "MissingActivity", "refusal.one", "1.0.0", "refusal.key", "1.0",
                    null, default, default, [])
            ]
            : [];

        var result = await WorkflowBuild.RunAsync(compiled, new Dictionary<string, byte[]>(), TimeSpan.FromSeconds(10),
            NullLogger.Instance, default, claims);

        Check(WorkflowPublishFailures.Diagnostic(result.ErrorCode, result.Error!, result.ErrorClass, WorkflowPublishStage.Test),
            failure, "Package activity 'refusal.key' has no runtime implementation");
    }

    private static Exception Fault(string failure) => failure switch
    {
        "author" => new WorkflowPublishException("PUB003", "Curated package refusal", new(7, 9)),
        "limit" => new WorkflowPublishException("PUB042", "Curated package limit", null),
        "unavailable" => new WorkflowPublishException("PUB041", "Curated package unavailable", null),
        _ => new InvalidOperationException(Secret)
    };

    [Theory, InlineData("missing"), InlineData("entries"), InlineData("native-imports")]
    public async Task CalleeArtifactFailuresRemainUnavailableAcrossUnionWrapping(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();
        var entries = failure == "native-imports" ? new Dictionary<string, byte[]> { ["runtimes/win/native/broken.dll"] = [1] } : new();
        var bytes = ActivityPackageFixture.Package("refusal.callee", "1.0.0", entries);
        var candidate = await new WorkflowPublisher(store, store).ReadPackageAsync(new MemoryStream(bytes));

        if (failure != "missing")
            await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));

        var hashes = failure == "entries" ? new Dictionary<string, ArtifactDigest>() : candidate.Package.Entries;

        var native = failure == "native-imports"
            ? new Dictionary<string, WorkflowPackageAssets>
                { ["win"] = new([], ["runtimes/win/native/broken.dll"]) }
            : new();

        var pin = new WorkflowPackagePin(candidate.Package.Id, candidate.Package.Version, candidate.Package.Digest, hashes, [], native,
            false);

        using var packages = new ResolvedWorkflowPackages();

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPackageResolver(store, store, new())
            .UnionAsync(packages, [pin], new(1, 1), default));

        Assert.Equal(failure == "missing" ? WorkflowErrorKind.ArtifactUnavailable : WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Check(WorkflowPublishFailures.Classify(error), "unavailable");
    }

    [Theory, InlineData(false), InlineData(true)]
    public void ScopeReferenceRefusalsUseCuratedAuthorDiagnostics(bool invalidMode)
    {
        var source = invalidMode
            ? "[IxIFlow.Core.ScopeConcurrency((IxIFlow.Core.ScopeConcurrency)42)] public sealed class Refused : IxIFlow.Core.IScopeActivity<object> { }"
            : "public abstract class Refused : IxIFlow.Core.IAsyncActivity { }";

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("RefusalScope",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path)));

        var type = compilation.GetTypeByMetadataName("Refused")!;

        var error = Assert.Throws<WorkflowPublishException>(() =>
            ScopePackageContracts.Describe(type, "refusal.scope", "1.0.0", "refusal.scope", "1.0"));

        Assert.Equal("PUB003", error.Code);
        Assert.Contains(invalidMode ? "invalid scope mode" : "exactly one ordinary or scope implementation", error.Message);
        Check(WorkflowPublishFailures.Classify(error), "author", "Package activity 'refusal.scope'");
    }

    [Theory, InlineData("unavailable"), InlineData("internal")]
    public async Task UploadReadFailuresAreNotMisreportedAsMalformedInput(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var stream = new BrokenRead(failure == "unavailable" ? new IOException(Secret) : Fault(failure));
        var error = await Record.ExceptionAsync(() => new WorkflowPublisher(store, store).ReadPackageAsync(stream));
        Assert.NotNull(error);
        Check(WorkflowPublishFailures.Classify(error), failure);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task PublishPersistsCuratedDiagnosticsFromReconciliationReads(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();

        var draft = await store.SaveDefinitionAsync(new("Refusal", 1, "workflow",
            "workflow: {name: Refusal, version: 1, dataType: System.Object}\ndefinitions: []"), null);

        var catalog = new Mock<IWorkflowCatalogStore>();

        catalog.Setup(value =>
                value.GetWorkflowVersionAsync(draft.Name, draft.Version, cancellationToken: Moq.It.IsAny<CancellationToken>()))
            .ThrowsAsync(Fault(failure));

        var publisher = new WorkflowPublisher(catalog.Object, store);
        using var service = new PublicationService(store, publisher);
        var attempt = await service.PublishAsync(draft.Id, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkflowPublicationState.Failed, attempt.State);
        var saved = (await service.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted))!;
        var diagnostic = Assert.Single(saved.Outcome!.Diagnostics);
        Assert.Equal("Publish", diagnostic.Stage);
        Assert.Equal(Expected(failure).Code, diagnostic.Code);
        Assert.Equal(Expected(failure).Class, saved.Outcome.FailureClass);

        if (failure == "author")
            Assert.Equal("Curated package refusal", diagnostic.Message);

        if (failure == "internal")
            Assert.Equal("An unexpected publication failure occurred", diagnostic.Message);

        Assert.Equal(draft.Id, (await store.GetDefinitionAsync("workflow", draft.Name, 1))!.Id);
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        await service.StopAsync(default);
    }

    [Theory, MemberData(nameof(UploadCases))]
    public async Task UploadReturnsTheRefusalClassAndCuratedAuthorMessage(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();
        var bytes = ActivityPackageFixture.Package("refusal.upload", "1.0.0", new());

        if (failure == "malformed")
            bytes = [1, 2, 3];

        if (failure == "author")
        {
            var image = WorkflowCompilation.Compile("public sealed class DuplicateAssembly;").Assembly;

            bytes = ActivityPackageFixture.Package("refusal.upload", "1.0.0", new()
                { ["lib/net10.0/one.dll"] = image, ["lib/net10.0/two.dll"] = image });
        }

        var options = failure switch
        {
            "limit" => new WorkflowPublisherOptions { PackageReadLimits = new() { MaxArchiveBytes = 1 } },
            "unavailable" => new WorkflowPublisherOptions
                { NuGetConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing.config") },
            _ => new WorkflowPublisherOptions()
        };

        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(options => options.AddPolicy("publish", policy => policy.RequireAssertion(_ => true)));
        services.AddSingleton<ICatalogQueryScope>(new AllPackages());
        await using var provider = services.BuildServiceProvider();

        using var uploads = new WorkflowPackageUploadService(new WorkflowPublisher(store,
                failure == "internal" ? new BrokenPut(store, Fault(failure)) : store, options), options,
            new() { PublishPolicy = "publish" }, NullLogger<WorkflowPackageUploadService>.Instance);

        var http = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "author")], "test"))
        };

        http.Request.Body = new MemoryStream(bytes);
        var response = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(await uploads.UploadAsync(http, "/packages"));
        Assert.Equal(Expected(failure).Code, response.ProblemDetails.Extensions["code"]);
        Assert.Equal(Expected(failure).Class.ToString(), response.ProblemDetails.Extensions["class"]);

        Assert.Equal(failure switch { "author" => 422, "malformed" => 400, "limit" => 413, "unavailable" => 503, _ => 500 },
            response.StatusCode);

        if (failure == "author")
            Assert.Contains("duplicate", response.ProblemDetails.Title!, StringComparison.OrdinalIgnoreCase);

        if (failure == "malformed")
            Assert.Equal("Invalid activity package upload", response.ProblemDetails.Title);

        if (failure == "internal")
            Assert.Equal("An unexpected publication failure occurred", response.ProblemDetails.Title);

        Assert.Empty(await store.GetPackagesAsync());
    }

    private sealed class AllPackages : ICatalogQueryScope
    {
        public ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CatalogScope.Unrestricted);
    }

    public sealed class RenderModel
    {
        public DateTime Local => new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        public string Text => "one";
        public string Broken => throw new InvalidOperationException(Secret);
    }

    [Theory, InlineData("author"), InlineData("limit"), InlineData("recursion"), InlineData("internal")]
    public async Task PreviewDistinguishesLiquidRefusalsBudgetsAndModelErrors(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();

        var content = failure switch
        {
            "author" => "{{ Local }}", "limit" => "{% for i in (1..20) %}{{ Text }}{% endfor %}",
            "recursion" => "{% for i in (1..2) %}{% for j in (1..2) %}{% for k in (1..2) %}{{ Text }}{% endfor %}{% endfor %}{% endfor %}",
            _ => "{{ Broken }}"
        };

        var source = "template: {key: refusal.render, version: 1, engine: liquid, format: text, model: " +
            typeof(RenderModel).FullName!.Replace('+', '.') + "}\ncontent: |\n  " + content + "\n";

        var engine = new IxIFlow.Templates.Liquid.LiquidTemplateEngine(failure == "recursion"
            ? new() { MaxRecursion = 1 }
            : new() { MaxSteps = 3 });

        var publisher = new WorkflowPublisher(store, store, templateEngines: new IxIFlow.Templates.TemplateEngines([engine]));

        var result = await publisher.PreviewTemplateAsync(new("refusal.render", 1, "template", source),
            new(null, System.Text.Json.JsonSerializer.SerializeToElement(new { })));

        Assert.Null(result.Rendered);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(WorkflowPublishStage.Test, diagnostic.Stage);

        if (failure == "author")
        {
            Assert.Equal("TPL006", diagnostic.Code);
            Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, diagnostic.Class);
            Assert.Contains("kind Local", diagnostic.Message);
        }
        else
            Check(diagnostic, failure == "recursion" ? "limit" : failure);
    }

    private sealed class BrokenRead(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw failure;
    }

    private sealed class BrokenPut(IArtifactStore store, Exception failure) : IArtifactStore
    {
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) => throw failure;
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => store.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => store.ExistsAsync(digest, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => store.DeleteAsync(digest, token);

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize,
            CancellationToken token = default) =>
            store.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, token);
    }
}