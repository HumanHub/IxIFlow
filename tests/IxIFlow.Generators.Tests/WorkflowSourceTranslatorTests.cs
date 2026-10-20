using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-05")]
public sealed class WorkflowSourceTranslatorTests
{
    private const string Header = "workflow:\n  name: Test\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n";
    private const string Activity = "  - kind: activity\n    id: multiply\n    activity: IxIFlow.Generators.Tests.Multiply\n";

    [Fact]
    public async Task SharedDocumentModelRoundTripsThroughTheGenerator()
    {
        var document = new IxIFlow.Dsl.Documents.WorkflowDocument
        {
            Workflow = new() { Name = "Document", DataType = typeof(FlowData).FullName! },
            Definitions =
            [
                new IxIFlow.Dsl.Documents.ForEachStepDocument
                {
                    Id = "loop", As = "number",
                    Items = new IxIFlow.Dsl.Documents.PathExpressionDocument { Path = "workflow.Values" },
                    Result = new IxIFlow.Dsl.Documents.PathExpressionDocument { Path = "number" },
                    Into = new() { Path = "workflow.Results" }
                }
            ]
        };
        var yaml = IxIFlow.Dsl.Documents.WorkflowYamlSerializer.Write(document);
        Assert.IsType<IxIFlow.Dsl.Documents.ForEachStepDocument>(
            Assert.Single(IxIFlow.Dsl.Documents.WorkflowYamlSerializer.Read(yaml).Definitions));
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "document.yaml"))();
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Values = [1, 2] });
        Assert.Equal([1, 2], result.Data.Results.Select(item => item.Value));
    }

    [Fact]
    public void LibraryAndIncrementalEntryPointsEmitTheSameSource()
    {
        var yaml = Header + Activity + "    input:\n      - target: Value\n        from: {kind: path, path: workflow.Value}\n";
        var file = new WorkflowText("test.yaml", yaml);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()], [file], optionsProvider: new WorkflowFileOptionsProvider(file.Path));
        var compilation = Compilation();
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var options = new WorkflowSourceOptions { Namespace = compilation.AssemblyName + ".Generated" };
        options.Types[typeof(FlowData).FullName!] = "global::" + typeof(FlowData).FullName;
        options.Types[typeof(Multiply).FullName!] = "global::" + typeof(Multiply).FullName;
        Assert.Equal(WorkflowSourceTranslator.Translate(yaml, file.Path, options),
            Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
    }

    [Fact]
    public void IncrementalEntryPointUsesUpdatedYaml()
    {
        var first = new WorkflowText("test.yaml", Header + Activity);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()], [first], optionsProvider: new WorkflowFileOptionsProvider(first.Path));
        driver = driver.RunGenerators(Compilation());
        var before = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        driver = driver.ReplaceAdditionalText(first, new WorkflowText(first.Path, first.Text.Replace("multiply", "updated")))
            .RunGenerators(Compilation());
        var after = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        Assert.NotEqual(before, after);
        Assert.Contains("updated", after);
    }

    [Fact]
    public void IncrementalEntryPointIgnoresOtherAdditionalFiles()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("notes.txt", "not a workflow"), new WorkflowText("docker-compose.yaml", "services: {}")],
            optionsProvider: new WorkflowFileOptionsProvider());
        var result = driver.RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GeneratedTrees);
    }

    [Theory]
    [InlineData("this is not: [valid", "workflow.yaml")]
    [InlineData("schemaVersion: '9.0'\nworkflow: {}", "Unsupported schema")]
    [InlineData("workflow: {}", "Missing 'name'")]
    public void InvalidDocumentsReportYamlLocations(string yaml, string expected)
    {
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "workflow.yaml"));
        Assert.Contains(expected, error.Message);
        Assert.True(error.Line > 0);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("workflow.yaml", yaml)], optionsProvider: new WorkflowFileOptionsProvider("workflow.yaml"));
        var diagnostics = driver.RunGenerators(Compilation()).GetRunResult().Diagnostics;
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("IXIYAML001", diagnostic.Id);
            Assert.Equal("workflow.yaml", diagnostic.Location.GetLineSpan().Path);
        });
    }

    [Theory]
    [InlineData("    input:\n      - target: Internal\n        from: {kind: constant, value: hidden}\n", "Multiply.Internal", "not an input")]
    [InlineData("    input:\n      - target: Value\n        from: {kind: constant, value: 1}\n    output:\n      - source: Internal\n        to: {kind: path, path: workflow.Note}\n", "Multiply.Internal", "not an output")]
    [InlineData("", "Multiply.Value", "required input")]
    public void GeneratedMappingsRunFluentBuildChecks(string mappings, string property, string expected)
    {
        var build = Compile(WorkflowSourceTranslator.Translate(Header + Activity + mappings, "mapping.yaml"));
        var error = Assert.Throws<TargetInvocationException>(() => build());
        var inner = Assert.IsType<WorkflowEngineException>(error.InnerException);
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, inner.Kind);
        Assert.Contains(property, inner.Message);
        Assert.Contains(expected, inner.Message);
        Assert.Contains("mapping.yaml:5", inner.Message);
    }

    [Theory]
    // A missing property is reported at the target; a wrongly typed constant at the constant.
    [InlineData("Missing", "{kind: constant, value: 1}", 8, 16)]
    [InlineData("Value", "{kind: constant, value: text}", 9, 38)]
    public void PropertyAndTypeErrorsAreMappedToYaml(string property, string expression, int line, int column)
    {
        var yaml = Header + Activity + "    input:\n      - target: " + property + "\n        from: " + expression + "\n";
        var source = WorkflowSourceTranslator.Translate(yaml, "invalid.yaml");
        var errors = Compilation(source).GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.Equal("invalid.yaml", error.Location.GetMappedLineSpan().Path));
        Assert.Contains(errors, error => error.Location.GetMappedLineSpan().StartLinePosition == new LinePosition(line, column));
    }

    [Fact]
    public async Task AnalyzersReportGeneratedMappingAndExpressionErrorsAtYamlLocations()
    {
        var yaml = Header + Activity + "    input:\n      - target: Internal\n        from: {kind: constant, value: hidden}\n      - target: Value\n        from: {kind: function, function: clock}\ncode:\n  clock:\n    language: csharp-expression\n    source: System.DateTime.UtcNow.Year\n";
        var source = WorkflowSourceTranslator.Translate(yaml, "analyzed.yaml");
        var diagnostics = await Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerExtensions.WithAnalyzers(
            Compilation(source), [new IxIFlow.Analyzers.MappingAnalyzer(), new IxIFlow.Analyzers.PurityAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("Multiply.Internal"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("DateTime.UtcNow"));
        Assert.All(diagnostics, diagnostic => Assert.Equal("analyzed.yaml", diagnostic.Location.GetMappedLineSpan().Path));
    }

    [Fact]
    public void GeneratedExpressionsRunFluentPurityChecks()
    {
        var yaml = Header + Activity + "    input:\n      - target: Value\n        from: {kind: function, function: clock}\ncode:\n  clock:\n    language: csharp-expression\n    source: System.DateTime.UtcNow.Year\n";
        var build = Compile(WorkflowSourceTranslator.Translate(yaml, "clock.yaml"));
        var error = Assert.Throws<TargetInvocationException>(() => build());
        var inner = Assert.IsType<WorkflowEngineException>(error.InnerException);
        Assert.Contains("DateTime.UtcNow", inner.Message);
        Assert.Contains("clock.yaml:5", inner.Message);
    }

    [Fact]
    public async Task CSharpAssetsAndActivityTemplateDefaultsAreTypedAndExecutable()
    {
        var yaml = Header + "  - kind: activityRef\n    id: reusable\n    ref: multiply\nactivities:\n  multiply:\n    activity: IxIFlow.Generators.Tests.Multiply\n    defaults: {Factor: 3}\n    input:\n      - target: Value\n        from: {kind: function, function: add}\n    output:\n      - source: Result\n        to: {kind: path, path: workflow.Result}\ncode:\n  add:\n    language: csharp-expression\n    source: workflow.Value + 1\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "assets.yaml"))();
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Value = 4 });
        Assert.Equal(15, result.Data.Result);
    }

    [Fact]
    public void ReusedStepTemplatesHaveDistinctStableIds()
    {
        var yaml = Header + "  - {kind: stepRef, id: first, ref: reusable}\n  - {kind: stepRef, id: second, ref: reusable}\nsteps:\n  reusable:\n    steps:\n      - kind: activity\n        id: multiply\n        activity: IxIFlow.Generators.Tests.Multiply\n        input:\n          - target: Value\n            from: {kind: constant, value: 2}\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "templates.yaml"))();
        Assert.Equal("first/multiply", definition.Steps[0].SequenceSteps[0].Id);
        Assert.Equal("second/multiply", definition.Steps[1].SequenceSteps[0].Id);
    }

    [Fact]
    public void NestedTemplateIdsKeepTheirYamlLocations()
    {
        var yaml = Header + "  - {kind: stepRef, id: first, ref: reusable}\n  - {kind: stepRef, id: second, ref: reusable}\nsteps:\n  reusable:\n    steps:\n      - kind: sequence\n        id: inner\n        steps:\n          - kind: activity\n            id: multiply\n            activity: IxIFlow.Generators.Tests.Multiply\n";
        var source = WorkflowSourceTranslator.Translate(yaml, "nested.yaml");
        Assert.Contains("first/multiply", source);
        Assert.Contains("second/multiply", source);
        Assert.Contains("nested.yaml:13", source);
    }

    [Fact]
    public void LibraryResolvesCatalogKeysThroughExplicitTypeNames()
    {
        var options = new WorkflowSourceOptions();
        options.Types.Add("data", typeof(FlowData).FullName!);
        options.Types.Add("multiply", typeof(Multiply).FullName!);
        var yaml = Header.Replace(typeof(FlowData).FullName!, "data") + Activity.Replace(typeof(Multiply).FullName!, "multiply") +
            "    input:\n      - target: Value\n        from: {kind: constant, value: 1}\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "aliases.yaml", options))();
        Assert.Equal(typeof(Multiply), Assert.Single(definition.Steps).ActivityType);
    }

    [Fact]
    public void DuplicateGeneratedClassNamesAreReportedAsYamlDiagnostics()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("one/flow.yaml", Header + Activity), new WorkflowText("two/flow.yaml", Header.Replace("name: Test", "name: Other") + Activity)],
            optionsProvider: new WorkflowFileOptionsProvider("one/flow.yaml", "two/flow.yaml"));
        var result = driver.RunGenerators(Compilation()).GetRunResult();
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal("IXIYAML001", diagnostic.Id));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.GetMessage().Contains("Duplicate generated class"));
    }

    [Fact]
    public void RecursiveStepTemplatesAreRejectedAtTheirReference()
    {
        var yaml = Header + "  - {kind: stepRef, id: first, ref: reusable}\nsteps:\n  reusable:\n    steps:\n      - {kind: stepRef, id: recursive, ref: reusable}\n";
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "cycle.yaml"));
        Assert.Contains("Recursive", error.Message);
    }

    [Fact]
    public void ChildInvocationRequiresAResolvedContractAndPinnedVersion()
    {
        var yaml = Header + "  - kind: invokeWorkflow\n    workflow: {name: child, version: 3}\n";
        Assert.Contains("resolved data contract", Assert.Throws<WorkflowSourceException>(() =>
            WorkflowSourceTranslator.Translate(yaml, "child.yaml")).Message);
        var options = new WorkflowSourceOptions();
        options.Workflows.Add("child@3", new WorkflowSourceReference(typeof(FlowData).FullName!, 3));
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "child.yaml", options))();
        Assert.Equal(typeof(FlowData), Assert.Single(definition.Steps).InvokedDataType);
        Assert.Equal(3, definition.Steps[0].WorkflowVersion);
    }

    [Fact]
    [Trait("Spec", "F-08")]
    public async Task GeneratedWaitPredicatesUseTypedContexts()
    {
        var yaml = Header + "  - kind: suspend\n    event: IxIFlow.Generators.Tests.Answer\n    reason: answer\n    resumeCondition:\n      kind: binary\n      operator: eq\n      left: {kind: path, path: event.Value}\n      right: {kind: path, path: workflow.Value}\n    output:\n      - source: Value\n        to: {kind: path, path: workflow.Result}\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "predicate.yaml"))();
        await using var services = GeneratedWorkflowTests.Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(definition, new DerivedFlowData { Value = 7 });
        var rejected = await client.ResumeAsync(started.InstanceId, "answer", new Answer { Value = 6 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);
        var result = await client.ResumeAsync(started.InstanceId, "answer", new Answer { Value = 7 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(7, Assert.IsAssignableFrom<FlowData>(result.WorkflowData).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachInfersItemsAndStoresTypedResults(bool parallel)
    {
        var yaml = Header + "  - kind: forEach\n    id: items\n    items: {kind: path, path: workflow.Values}\n    as: number\n" +
            (parallel ? "    parallel: {maxConcurrency: 2, join: all}\n" : "") +
            "    result: {kind: path, path: previousStep.Result}\n    into: {kind: path, path: workflow.Results}\n    steps:\n      - kind: activity\n        id: multiply\n        activity: IxIFlow.Generators.Tests.Multiply\n        input:\n          - target: Value\n            from: {kind: path, path: number}\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "foreach.yaml"))();
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Values = [2, 3, 2] });
        Assert.Equal([4, 6, 4], result.Data.Results.Select(item => item.Value));
        Assert.All(result.Data.Results, item => Assert.Equal(ItemStatus.Completed, item.Status));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("conditionally, resultType: int, condition: {kind: function, function: complete}")]
    public async Task ParallelForEachJoinsStopAdmission(string join)
    {
        var yaml = Header + "  - kind: forEach\n    items: {kind: path, path: workflow.Values}\n    as: number\n" +
            "    parallel: {maxConcurrency: 1, join: " + join + "}\n" +
            "    result: {kind: path, path: previousStep.Result}\n    into: {path: workflow.Results}\n    steps:\n      - kind: activity\n        activity: IxIFlow.Generators.Tests.Multiply\n        input:\n          - target: Value\n            from: {kind: path, path: number}\ncode:\n  complete:\n    language: csharp-expression\n    source: results.Count > 0\n";
        var definition = Compile(WorkflowSourceTranslator.Translate(yaml, "join.yaml"))();
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Values = [2, 3] });
        Assert.Equal([ItemStatus.Completed, ItemStatus.NotStarted], result.Data.Results.Select(item => item.Status));
        Assert.Equal(4, result.Data.Results[0].Value);
    }

    private static Func<WorkflowDefinition> Compile(string source)
    {
        using var stream = new MemoryStream();
        var compilation = Compilation(source);
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var build = assembly.GetTypes().Single(type => type.GetMethod("Build") != null).GetMethod("Build")!;
        return () => (WorkflowDefinition)build.Invoke(null, null)!;
    }

    private static CSharpCompilation Compilation(string? source = null)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                typeof(FlowData).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
        return CSharpCompilation.Create("Generated_" + Guid.NewGuid().ToString("N"),
            source == null ? [] : [CSharpSyntaxTree.ParseText(source, path: "generated.g.cs")],
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

}
