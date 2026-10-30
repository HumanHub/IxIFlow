using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Json.Schema;
using YamlDotNet.Core;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26"), Trait("Spec", "F-06")]
public sealed class ScopeYamlTests
{
    [Fact]
    public void ActivitySchemaMatchesTheScopeDocumentModel()
    {
        var path = Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "src/IxIFlow/schemas/activity.schema.json");
        var schema = DocumentSchema.Write(typeof(ActivityDocument));

        if (Environment.GetEnvironmentVariable("IXIFLOW_WRITE_SCHEMA") == "1")
            File.WriteAllText(path, schema);

        Assert.Equal(schema, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ScopeMatchesFluentStructureHashContractAndResults()
    {
        var yaml = Generated.Workflow_scope.Build();

        var fluent = new WorkflowBuilder<ScopeYamlData, object>([], "ScopeYaml")
            .Scope<ScopeYamlActivity>(setup =>
                {
                    setup.Input(activity => activity.Factor).From(ctx => ctx.WorkflowData.Value)
                        .Output(activity => activity.Opened).To(ctx => ctx.WorkflowData.Opened);

                    setup.Retry(new RetryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 3, 2))
                        .Threading(ScopeThreading.ThreadAffine);
                },
                body => body.Step<ScopeYamlRead>(setup => setup.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result))
                    .WithId("read"))
            .WithId("session").Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).ToJson(), WorkflowDefinitionValidator.GetContract(yaml).ToJson());
        Assert.Equal(ScopeConcurrency.Shared, yaml.Steps[0].ScopeConcurrency);

        foreach (var definition in new[] { fluent, yaml })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddIxIFlow();
            await using var host = services.BuildServiceProvider();
            var result = await host.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeYamlData { Value = 7 });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var data = Assert.IsType<ScopeYamlData>(result.WorkflowData);
            Assert.Equal(21, data.Result);
            Assert.Equal(7, data.Opened);
        }
    }

    [Fact]
    public void RootRequirementsHaveTheFluentContractAndSurviveANonActivityFirstStep()
    {
        var yaml = Generated.Workflow_scope_requiring.Build();

        var fluent = Workflow.Create<ScopeYamlData>("ScopeRequiringYaml").RequiresScope<ScopeYamlValue>()
            .Step<ScopeYamlRead>(setup => setup.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result)).WithId("read")
            .Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).Hash, WorkflowDefinitionValidator.GetContract(yaml).Hash);

        var source = WorkflowSourceTranslator.Translate(
            "workflow: {name: Empty, dataType: IxIFlow.Generators.Tests.ScopeYamlData, requiresScopes: [IxIFlow.Generators.Tests.ScopeYamlValue]}\ndefinitions: [{kind: sequence, steps: []}]",
            "root.yaml");

        Assert.Equal(new[] { typeof(ScopeYamlValue) }, Build(source).RequiredScopes);
    }

    [Theory]
    [InlineData("{kind: scope, activity: Ordinary}", "SCOPE003")]
    [InlineData("{kind: activity, activity: Scope}", "SCOPE004")]
    [InlineData("{kind: saga, steps: [{kind: activity, activity: Scope}]}", "SCOPE004")]
    [InlineData("{kind: saga, steps: [{kind: scope, activity: Scope}]}", "SCOPE001")]
    [InlineData("{kind: saga, onError: [{compensate: all, steps: [{kind: scope, activity: Scope}]}]}", "SCOPE001")]
    [InlineData("{kind: scope, activity: Scope, threading: anyThread}", "SCOPE005")]
    [InlineData("{kind: scope, activity: Scope, retry: {maximumAttempts: -1}}", "RETRY001")]
    public async Task InvalidScopeUsesAreCurated(string step, string code)
    {
        var options = new WorkflowSourceOptions();
        options.Activities["Scope"] = new("Scope", "1") { Scope = "object", Concurrency = "shared", Threading = "threadAffine" };
        var document = WorkflowYamlSerializer.Read("workflow: {name: Test, dataType: Data}\ndefinitions: [" + step + "]");
        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData("threading: unknown")]
    [InlineData("typo: true")]
    [InlineData("retry: {maximumAttempts: 3, multiplier: 2}")]
    public void SchemaAndReaderRefuseUnknownScopeMembersAndModes(string member)
    {
        var yaml = "workflow: {name: Test, dataType: Data}\ndefinitions: [{kind: scope, activity: Scope, " + member + "}]";
        var schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        Assert.False(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid);
        Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read(yaml));
    }

    [Fact]
    public void GeneratorRejectsAnOrdinaryTypeUsedAsAScopeAtItsYamlLocation()
    {
        var yaml =
            "workflow: {name: Test, dataType: IxIFlow.Generators.Tests.ScopeYamlData}\ndefinitions:\n  - kind: scope\n    activity: IxIFlow.Generators.Tests.ScopeYamlRead\n";

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("scope-error.yaml", yaml)], optionsProvider: new WorkflowFileOptionsProvider("scope-error.yaml"));

        var diagnostic = Assert.Single(driver.RunGenerators(GeneratorRegressionTests.Compilation()).GetRunResult().Diagnostics);
        Assert.Contains("not a scope", diagnostic.GetMessage());
        Assert.Equal("scope-error.yaml", diagnostic.Location.GetLineSpan().Path);
        Assert.Equal(3, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void CompilerContractsMatchCoreScopeDescriptorsIncludingRequirementsAndDefaults()
    {
        var compilation = GeneratorRegressionTests.Compilation();

        foreach (var type in new[] { typeof(ScopeYamlActivity), typeof(ScopeYamlRead) })
        {
            var symbol = compilation.GetTypeByMetadataName(type.FullName!)!;
            var document = ActivityDocumentContract.FromSymbol(symbol, "test", "1")!;

            if (type == typeof(ScopeYamlActivity))
            {
                var core = ActivityDescriptor.OfType(type, "test", "1.0.0");
                Assert.Equal(core.Scope!.ValueTypeName, document.Scope);
                Assert.Equal("shared", document.Concurrency);
                Assert.Equal("anyThread", document.Threading);
                Assert.Empty(document.RequiresScopes);
            }
            else
            {
                Assert.Null(document.Scope);
                Assert.Null(document.Concurrency);
                Assert.Null(document.Threading);
                Assert.Equal("global::IxIFlow.Generators.Tests.ScopeYamlValue", Assert.Single(document.RequiresScopes));
            }
        }

        Assert.Null(ActivityDocumentContract.FromSymbol(compilation.GetTypeByMetadataName(typeof(ScopeYamlData).FullName!)!, "invalid",
            ""));
    }

    [Fact]
    public void RequiresScopeBuildFailureNamesTheAuthoredYamlStep()
    {
        var yaml =
            "workflow: {name: Missing, dataType: IxIFlow.Generators.Tests.ScopeYamlData}\ndefinitions:\n  - kind: activity\n    activity: IxIFlow.Generators.Tests.ScopeYamlRead\n";

        var source = WorkflowSourceTranslator.Translate(yaml, "scope-location.yaml");
        var error = Assert.Throws<WorkflowEngineException>(() => Build(source));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("scope-location.yaml:3", error.Message);
        Assert.Contains("requires an enclosing scope", error.Message);
    }

    [Fact]
    public void StandaloneScopeDocumentCompilesAndProjectsEveryScopeMember()
    {
        var source = File.ReadAllText(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(),
            "tests/IxIFlow.Generators.Tests/Documents/scope-activity.yaml"));

        var result = ActivityCompilation.Compile(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        using var stream = new MemoryStream(result.Assembly);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var type = assembly.GetExportedTypes().Single();
        var descriptor = ActivityDescriptor.OfType(type, "acme.scope", "1.0.0");

        Assert.Equal(typeof(System.Text.StringBuilder),
            type.GetInterfaces().Single(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IScopeActivity<>))
                .GetGenericArguments()[0]);

        Assert.Equal(ScopeConcurrency.Shared, descriptor.Scope!.Concurrency);
        Assert.Equal(ScopeThreading.ThreadAffine, descriptor.Scope.Threading);
        Assert.Equal("global::System.Object", Assert.Single(descriptor.RequiresScopes).TypeName);
    }

    [Theory]
    [InlineData("concurrency: shared", "ACTD017")]
    [InlineData("threading: threadAffine", "ACTD017")]
    [InlineData("scope: 'bad type!'", "ACTD018")]
    [InlineData("requiresScopes: ['bad type!']", "ACTD018")]
    public void StandaloneInvalidScopeDeclarationsAreRefused(string header, string code)
    {
        var result = ActivityCompilation.Compile("activity: {key: acme.invalid, version: 1, " + header +
            "}\nbody: |\n  await Task.Yield();\n");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static WorkflowDefinition Build(string source)
    {
        using var stream = new MemoryStream();
        var emitted = GeneratorRegressionTests.Compilation(source).Emit(stream);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);

        try
        {
            return (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null).GetMethod("Build")!.Invoke(null,
                null)!;
        }
        catch (TargetInvocationException error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

            throw;
        }
    }
}

public sealed class ScopeYamlData
{
    public int Value { get; set; }
    public int Opened { get; set; }
    public int Result { get; set; }
}

public sealed record ScopeYamlValue(int Factor, int Thread);

[WorkflowActivity("scope.yaml", Version = "1.0"), ScopeConcurrency(ScopeConcurrency.Shared)]
public sealed class ScopeYamlActivity : IScopeActivity<ScopeYamlValue>
{
    [ActivityInput] public int Factor { get; set; }
    [ActivityOutput] public int Opened { get; set; }

    public async Task<ScopeYamlValue> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();
        Opened = Factor;

        return new(Factor, Environment.CurrentManagedThreadId);
    }

    public Task CloseAsync(ScopeYamlValue value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken)
    {
        Assert.Equal(value.Thread, Environment.CurrentManagedThreadId);

        return Task.CompletedTask;
    }
}

[RequiresScope<ScopeYamlValue>]
public sealed class ScopeYamlRead : IAsyncActivity
{
    [ActivityOutput] public int Result { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        var value = context.Scope<ScopeYamlValue>();
        await Task.Yield();
        Assert.Equal(value.Thread, Environment.CurrentManagedThreadId);
        Result = value.Factor * 3;
    }
}