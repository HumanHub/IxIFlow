using System.Text;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "scopes"), Trait("Spec", "F-26"), Trait("Spec", "F-14"), Trait("Spec", "F-18")]
public sealed class ScopeAuthoringScenarios
{
    private const string ScopeSource = """
                                       activity: {key: acme.scope, version: 1, scope: System.Text.StringBuilder, concurrency: shared, threading: threadAffine}
                                       input:
                                         Value: {type: int, default: 7}
                                       output:
                                         Opened: {type: int}
                                       body: |
                                         await Task.Yield();
                                         Opened = Value;
                                         return new System.Text.StringBuilder(Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                       declarations: |
                                         public Task CloseAsync(System.Text.StringBuilder value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) => Task.CompletedTask;
                                       """;

    private const string ReadSource = """
                                      activity: {key: acme.scope-reader, version: 1, requiresScopes: [System.Text.StringBuilder]}
                                      output:
                                        Result: {type: int}
                                      body: |
                                        await Task.Yield();
                                        Result = int.Parse(context.Scope<System.Text.StringBuilder>().ToString(), System.Globalization.CultureInfo.InvariantCulture) * 2;
                                      """;

    private const string Imports = "imports: {catalogs: [acme.scope@1.0.0, acme.scope-reader@1.0.0]}\n";

    private static string Header(string name, bool requiring = false) => "workflow: {name: " + name +
        ", dataType: IxIFlow.BundleFixtures.PublishedActivityData" + (requiring ? ", requiresScopes: [System.Text.StringBuilder]" : "") +
        "}\n" + Imports;

    private const string ReadStep =
        "{kind: activity, id: read, activity: acme.scope-reader, output: [{source: Result, to: {path: workflow.Result}}]}";

    private static string Scoped(string name, string body) => Header(name) + "definitions:\n" +
        "  - kind: scope\n    id: session\n    activity: acme.scope\n    activityVersion: '1'\n" +
        "    defaults: {Value: 5}\n    input: [{target: Value, from: {path: workflow.Attempts}}]\n" +
        "    output: [{source: Opened, to: {path: workflow.Attempts}}]\n" +
        "    retry: {maximumAttempts: 3, initialInterval: '00:00:05', maximumInterval: '00:00:10', backoffCoefficient: 2}\n" +
        "    threading: threadAffine\n    steps:\n      - " + body + "\n";

    private static async Task<WorkflowPublishResult> Publish(IWorkflowStateRepository store, string source)
    {
        var identity = WorkflowDocumentKinds.Identify(source);
        var draft = await store.SaveDefinitionAsync(new(identity.Name, identity.Version, identity.Kind, source), null);
        var result = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return result;
    }

    private static async Task Activities(IWorkflowStateRepository store)
    {
        await Publish(store, ScopeSource);
        await Publish(store, ReadSource);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedAndLocalScopeDescriptorsSurviveAllCatalogProjectionsAndReopen(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Activities(store);

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        store = database.NewRepository();
        using var catalog = new WorkflowCatalog(GuardedStateRepository.Wrap(store));
        var query = new CatalogActivityQuery(CatalogScope.Unrestricted, Limit: 1);
        var rows = new List<CatalogActivity>();

        do
        {
            var page = await catalog.ListActivitiesAsync(query);
            rows.AddRange(page.Items);
            query = query with { After = page.NextCursor };
        } while (query.After != null);

        Assert.Equal(new[] { "acme.scope", "acme.scope-reader" }, rows.Select(item => item.Descriptor.Key));

        foreach (var row in rows)
        {
            var descriptor = row.Descriptor;

            var detail = (await catalog.GetActivityAsync(descriptor.Key, descriptor.Version,
                new(row.Provider.PackageId, row.Provider.PackageVersion, row.Provider.Build), CatalogScope.Unrestricted))!;

            Assert.Equal(descriptor.ToJson(), detail.Activity.Descriptor.ToJson());

            Assert.Equal(descriptor.ToJson(),
                Assert.Single((await catalog.ListActivityVersionsAsync(descriptor.Key, new(CatalogScope.Unrestricted))).Items).Descriptor
                    .ToJson());

            Assert.Equal(descriptor.ToJson(),
                Assert.Single((await catalog.GetPackageActivitiesAsync(row.Provider.PackageId, row.Provider.PackageVersion,
                    new(CatalogScope.Unrestricted))).Items).Descriptor.ToJson());

            var package = (await catalog.GetPackageDetailAsync(row.Provider.PackageId, row.Provider.PackageVersion,
                CatalogScope.Unrestricted))!;

            Assert.Equal(descriptor.ToJson(), Assert.Single(package.Activities.Activities).ToJson());
            Assert.Equal(ActivityOrigin.Published, row.Provider.Origin);
            Assert.Null(descriptor.ActivityType);
        }

        var scope = rows[0].Descriptor.Scope!;
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(StringBuilder)), scope.ValueType);
        Assert.Equal("global::System.Text.StringBuilder", scope.ValueTypeName);
        Assert.Equal(ScopeConcurrency.Shared, scope.Concurrency);
        Assert.Equal(ScopeThreading.ThreadAffine, scope.Threading);
        Assert.Equal(new ScopeRequirement(scope.ValueType, scope.ValueTypeName), Assert.Single(rows[1].Descriptor.RequiresScopes));
        Assert.Null(rows[1].Descriptor.Scope);
        Assert.Empty(rows[0].Descriptor.RequiresScopes);
        var denied = new CatalogScope(new([]), new([]), new([]), new([]));
        Assert.Empty((await catalog.ListActivitiesAsync(new(denied))).Items);
        var steps = await catalog.ListStepDescriptorsAsync(denied);
        Assert.Equal(new[] { "renderTemplate", "scope" }, steps.Select(item => item.Kind));
        var step = Assert.Single(steps, item => item.Kind == "scope");
        Assert.Equal(JsonSerializer.Serialize(ScopeStepDescriptors.Scope()), JsonSerializer.Serialize(step));

        Assert.Equal(new[] { "Activity", "ActivityVersion", "Defaults", "Input", "Output", "Retry", "Threading", "Steps" },
            step.Inputs.Select(item => item.Name));

        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(ScopeThreading?)), step.Inputs.Single(item => item.Name == "Threading").Type);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(List<WorkflowStep>)), step.Inputs.Single(item => item.Name == "Steps").Type);
        Assert.Empty(step.Outputs);

        using var services = new ServiceCollection().AddIxIFlow().AddActivity<LocalScope>().AddActivity<LocalRead>().Services
            .BuildServiceProvider();

        var installed = Assert.Single(services.GetRequiredService<IActivityRegistry>().Packages);

        var member = new MemberRegistration(Guid.NewGuid(), MemberKind.Host, "scope catalog", null, 1, "1.0.0", TimeSpan.FromSeconds(5),
            new Dictionary<string, string>()) { InstalledPackages = [installed] };

        await store.RegisterMemberAsync(member);

        foreach (var row in (await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, Origin: ActivityOrigin.Local))).Items)
        {
            var original = installed.Activities.Single(item => item.Key == row.Descriptor.Key);
            Assert.Equal(original.ToJson(), row.Descriptor.ToJson());
            Assert.Null(row.Descriptor.ActivityType);
        }

        Assert.Equal(2, (await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, Origin: ActivityOrigin.Local))).Items.Count);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RequiringYamlCalleePinsItsContractAndExecutesColdInsideAYamlScope(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Activities(store);
        var leaf = (await Publish(store, Header("ScopeLeaf", true) + "definitions: [" + ReadStep + "]")).Workflow!;
        var contract = WorkflowContract.Parse(leaf.ContractJson!);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(StringBuilder)), Assert.Single(contract.RequiredScopes).Type);

        var caller = (await Publish(store,
                Scoped("ScopeCaller",
                    "{kind: use, id: call, workflow: {name: ScopeLeaf}, input: [{target: Attempts, from: {path: workflow.Attempts}}], output: [{source: Result, to: {path: workflow.Result}}]}")))
            .Workflow!;

        await using (var content = await store.OpenAsync(caller.ArtifactDigest!.Value))
        {
            var bundle = await WorkflowBundleArchive.ReadAsync(content!, caller.ArtifactDigest.Value);
            Assert.Equal(leaf.ContractHash, Assert.Single(bundle.Manifest.InlineWorkflows).ContractHash);
            Assert.Empty(WorkflowContract.Parse(bundle.Manifest.Contract).RequiredScopes);

            Assert.Contains("requiresScopes:",
                Encoding.UTF8.GetString(bundle.Entries.Single(item =>
                    item.Key.StartsWith("inline/", StringComparison.Ordinal) &&
                    item.Key.EndsWith("workflow.yaml", StringComparison.Ordinal)).Value.Span));
        }

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        store = database.NewRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.ScopeAuthoring", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = PublishedBundleScenario.Host(store, cache, configure: database.ConfigureHost);
            await database.StartHostLinksAsync(host);
            var client = host.GetRequiredService<IWorkflowClient>();
            var id = await client.StartAsync("ScopeCaller", 1, new PublishedActivityData { Attempts = 7 });
            var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(14, result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));

            var refusal = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                client.StartAsync("ScopeLeaf", 1, new PublishedActivityData()));

            Assert.Equal(WorkflowErrorKind.InvalidRequest, refusal.Kind);
            await host.GetRequiredService<WorkflowHost>().StopAsync(default);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopeDeclarationsAndLexicalRequirementsAreRefusedBeforePublicationWithYamlLocations(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Activities(store);
        await Publish(store, Header("ScopeLeaf", true) + "definitions: [" + ReadStep + "]");

        foreach (var (source, expected) in new[]
                 {
                     (Header("MissingScope") + "definitions:\n  - " + ReadStep + "\n", "requires an enclosing scope"),
                     (Scoped("WeakThread", ReadStep).Replace("threading: threadAffine", "threading: anyThread"), "weaken"),
                     (Header("WrongKind") + "definitions: [{kind: activity, activity: acme.scope}]", "kind: scope"),
                     (Header("Independent") + "definitions: [{kind: invokeWorkflow, workflow: {name: ScopeLeaf, version: 1}}]", "scope")
                 })
        {
            var identity = WorkflowDocumentKinds.Identify(source);
            var report = await new WorkflowPublisher(store, store).ValidateAsync(new(identity.Name, 1, identity.Kind, source));

            Assert.Contains(report.Diagnostics,
                diagnostic => diagnostic.Severity == IxIFlow.Dsl.Compilation.WorkflowDiagnosticSeverity.Error &&
                    diagnostic.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));

            Assert.All(report.Diagnostics.Where(item => item.Severity == IxIFlow.Dsl.Compilation.WorkflowDiagnosticSeverity.Error),
                diagnostic => Assert.True(diagnostic.Line > 1));

            if (identity.Name == "MissingScope")
            {
                var canonical = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(source)));
                var error = Assert.Single(report.Diagnostics);
                Assert.Equal("PUB004", error.Code);
                Assert.Equal("workflow.yaml", error.Path);
                Assert.Equal(canonical.Definitions[0].Location.Line, error.Line);
            }

            Assert.Null(await store.GetWorkflowVersionAsync(identity.Name, 1));
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task YamlAndFluentScopesIncludingTypedInlineRequirementsHaveEqualHashesContractsAndResults(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var type = typeof(ParityScope).FullName!.Replace('+', '.');
        var inline = typeof(ParityInline).FullName!.Replace('+', '.');
        var data = typeof(PublishedActivityData).FullName!;
        var options = new WorkflowSourceOptions();

        options.Activities[type] = new(type, "")
            { Scope = "global::System.Text.StringBuilder", Concurrency = "shared", Threading = "threadAffine" };

        var yaml = $"workflow: {{name: ScopeParity, dataType: {data}}}\ndefinitions:\n" +
            $"  - kind: scope\n    id: session\n    activity: {type}\n    input: [{{target: Value, from: {{path: workflow.Attempts}}}}]\n" +
            "    retry: {maximumAttempts: 3, initialInterval: '00:00:05', maximumInterval: '00:00:10', backoffCoefficient: 2}\n" +
            "    steps:\n      - kind: useType\n        id: inline\n" + $"        workflowType: {inline}\n        dataType: {data}\n" +
            "        output: [{source: Result, to: {path: workflow.Result}}]\n";

        var generated =
            IxIFlow.Tests.Infrastructure.TestWorkflowCompilation.Build(WorkflowSourceTranslator.Translate(yaml, "parity.yaml", options));

        var fluent = new WorkflowBuilder<PublishedActivityData, object>([], "ScopeParity")
            .Scope<ParityScope>(setup =>
                {
                    setup.Input(activity => activity.Value).From(context => context.WorkflowData.Attempts);
                    setup.Retry(new RetryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 3, 2));
                },
                body => body.Use<ParityInline, PublishedActivityData>(setup =>
                    setup.Output(value => value.Result).To(context => context.WorkflowData.Result)).WithId("inline"))
            .WithId("session").Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).ToJson(), WorkflowDefinitionValidator.GetContract(generated).ToJson());

        foreach (var definition in new[] { fluent, generated })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddIxIFlow();
            services.AddSingleton(store);
            database.ConfigureHost(services);
            await using var host = services.BuildServiceProvider();
            await database.StartHostLinksAsync(host);
            var result = await host.GetRequiredService<IWorkflowClient>().RunAsync(definition, new PublishedActivityData { Attempts = 7 });
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(14, result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
            await host.GetRequiredService<WorkflowHost>().StopAsync(default);
        }
    }

    [ScopeConcurrency(ScopeConcurrency.Shared), ScopeThreading(ScopeThreading.ThreadAffine)]
    public sealed class ParityScope : IScopeActivity<StringBuilder>
    {
        [ActivityInput] public int Value { get; set; }

        public Task<StringBuilder> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new StringBuilder(Value.ToString()));

        public Task CloseAsync(StringBuilder value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [Workflow("ScopeParityInline")]
    public sealed class ParityInline : IWorkflow<PublishedActivityData>
    {
        public void Build(IWorkflowBuilder<PublishedActivityData> builder) => builder.RequiresScope<StringBuilder>()
            .Step<ParityRead>(setup => setup.Output(activity => activity.Result).To(context => context.WorkflowData.Result));
    }

    [RequiresScope<StringBuilder>]
    public sealed class ParityRead : IAsyncActivity
    {
        [ActivityOutput] public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = int.Parse(context.Scope<StringBuilder>().ToString()) * 2;

            return Task.CompletedTask;
        }
    }

    [WorkflowActivity("scope.local"), ScopeConcurrency(ScopeConcurrency.Shared), ScopeThreading(ScopeThreading.ThreadAffine)]
    public sealed class LocalScope : IScopeActivity<StringBuilder>
    {
        public Task<StringBuilder> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new StringBuilder());

        public Task CloseAsync(StringBuilder value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [WorkflowActivity("scope.local.read"), RequiresScope<StringBuilder>]
    public sealed class LocalRead : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}