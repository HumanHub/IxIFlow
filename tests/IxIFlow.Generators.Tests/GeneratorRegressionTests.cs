using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-05")]
public sealed class GeneratorRegressionTests
{
    private const string Header = "workflow:\n  name: Test\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n";
    private const string Activity = "  - kind: activity\n    id: multiply\n    activity: IxIFlow.Generators.Tests.Multiply\n    input:\n      - target: Value\n        from: {kind: path, path: workflow.Value}\n";

    [Fact]
    public void WindowsLinePathsAreVerbatimAndBuildIsUnmapped()
    {
        const string path = @"S:\work\flows\invalid.yaml";
        var source = WorkflowSourceTranslator.Translate(Header + Activity.Replace("workflow.Value", "workflow.Missing"), path);
        var compilation = Compilation(source);
        var error = Assert.Single(compilation.GetDiagnostics().Where(item => item.Id == "CS1061"));
        Assert.Equal(path, error.Location.GetMappedLineSpan().Path);
        var build = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Single(node => node.Expression.ToString().EndsWith(".Build", StringComparison.Ordinal));
        Assert.Equal("generated.g.cs", build.GetLocation().GetMappedLineSpan().Path);
    }

    [Fact]
    public void QuotedSourcePathsAreRejected()
    {
        Assert.Contains("quotes", Assert.Throws<WorkflowSourceException>(() =>
            WorkflowSourceTranslator.Translate(Header + Activity, "bad\"path.yaml")).Message);
    }

    [Fact]
    public void UnrelatedCSharpEditsReuseYamlGeneration()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("cached.yaml", Header + Activity)], optionsProvider: new WorkflowFileOptionsProvider("cached.yaml"), driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        var compilation = Compilation();
        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class Unrelated { public int Value => 3; }")));
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(result.Diagnostics);
        foreach (var name in new[] { "YamlTypes", "YamlGeneration" })
            Assert.All(result.TrackedSteps[name].SelectMany(step => step.Outputs), output =>
                Assert.True(output.Reason == IncrementalStepRunReason.Cached,
                    name + ": " + output.Reason));
        Assert.All(result.TrackedOutputSteps.SelectMany(pair => pair.Value).SelectMany(step => step.Outputs),
            output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    [Fact]
    public void YmlAdditionalFilesAreGenerated()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("short.yml", Header + Activity)], optionsProvider: new WorkflowFileOptionsProvider("short.yml"));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        Assert.Single(driver.GetRunResult().GeneratedTrees);
    }

    [Theory]
    [InlineData("custom.definition", true)]
    [InlineData("unmarked.workflow.yaml", false)]
    public void WorkflowMetadataControlsSelectionRegardlessOfExtension(string path, bool enabled)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText(path, Header + Activity)],
            optionsProvider: enabled ? new WorkflowFileOptionsProvider(path) : new WorkflowFileOptionsProvider());
        var result = driver.RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.Equal(enabled ? 1 : 0, result.GeneratedTrees.Length);
    }

    [Fact]
    public void InvalidTypeNamesAreLocatedAtTheirScalarAndDiagnosticsDoNotRepeatThePath()
    {
        var yaml = Header + Activity.Replace("IxIFlow.Generators.Tests.Multiply", "'not a type!'");
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "type.yaml"));
        Assert.Equal(7, error.Line);
        Assert.True(error.Column > 1);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("type.yaml", yaml)], optionsProvider: new WorkflowFileOptionsProvider("type.yaml"));
        var diagnostic = Assert.Single(driver.RunGenerators(Compilation()).GetRunResult().Diagnostics);
        Assert.Equal(6, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.DoesNotContain("type.yaml", diagnostic.GetMessage());
    }

    [Fact]
    public async Task EventKeywordsAreEscapedInsideExpressions()
    {
        var yaml = Header + """
              - kind: suspend
                event: IxIFlow.Generators.Tests.Answer
                reason: answer
                resumeCondition: {kind: function, function: same}
            code:
              same:
                language: csharp-expression
                source: workflow.Value == event.Value
            """;
        var definition = Build(yaml);
        var step = Assert.Single(definition.Steps);
        Assert.False(step.CompiledCondition!(new ResumeEventContext<FlowData, Answer>
        { WorkflowData = new() { Value = 3 }, ResumeEvent = new() { Value = 2 } }));
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Value = 7 }, resume: true);
        Assert.Equal(7, result.Data.Value);
    }

    [Fact]
    public async Task YamlFloatsPopulateDoubleInputs()
    {
        var yaml = Header + """
              - kind: activity
                activity: IxIFlow.Generators.Tests.DoubleActivity
                input:
                  - target: Value
                    from: {kind: constant, value: 1.5}
                output:
                  - source: Result
                    to: {path: workflow.Result}
            """;
        var result = await GeneratedWorkflowTests.Execute(Build(yaml), new FlowData());
        Assert.Equal(15, result.Data.Result);
    }

    [Fact]
    [Trait("Spec", "F-06")]
    public async Task TemplatesDefaultsAndAssetsMatchFluentFingerprintAndResults()
    {
        var yaml = Header + """
              - kind: stepRef
                id: reusable
                ref: block
            steps:
              block:
                parameters:
                  factor: {type: int, required: true, default: 3}
                steps:
                  - kind: activityRef
                    id: multiply
                    ref: reusable
            activities:
              reusable:
                activity: IxIFlow.Generators.Tests.Multiply
                defaults: {Factor: 3}
                input:
                  - target: Value
                    from: {kind: function, function: plusOne}
                  - target: Factor
                    from: {kind: path, path: factor}
                output:
                  - source: Result
                    to: {path: workflow.Result}
            code:
              plusOne:
                language: csharp-expression
                source: workflow.Value + 1
            """;
        var generated = Build(yaml);
        var fluent = new WorkflowBuilder<FlowData, object>([], "Test")
            .Sequence(sequence => sequence.Step<Multiply>(setup => setup
                .Input(act => act.Value).From(ctx => ctx.WorkflowData.Value + 1)
                .Input(act => act.Factor).From(ctx => 3)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result)).WithId("reusable/multiply"))
            .WithId("reusable").Build();
        Assert.Equal(GeneratedWorkflowTests.Shape(fluent), GeneratedWorkflowTests.Shape(generated));
        var first = await GeneratedWorkflowTests.Execute(fluent, new() { Value = 4 });
        var second = await GeneratedWorkflowTests.Execute(generated, new() { Value = 4 });
        Assert.Equal(15, second.Data.Result);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Data.Result, second.Data.Result);
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("all")]
    [InlineData("any")]
    [InlineData("conditionally")]
    [Trait("Spec", "F-06")]
    public async Task ForEachMatchesFluentFingerprintAndResults(string join)
    {
        var yaml = Header + "  - kind: forEach\n    id: items\n    items: {path: workflow.Values}\n    as: number\n" +
            (join == "sequential" ? "" : "    parallel: {maxConcurrency: " + (join == "all" ? 2 : 1) + ", join: " +
                (join == "conditionally" ? "conditionally, resultType: int, condition: {kind: function, function: done}" : join) + "}\n") +
            "    result: {path: previousStep.Result}\n    into: {path: workflow.Results}\n    steps:\n" +
            "      - kind: activity\n        id: multiply\n        activity: IxIFlow.Generators.Tests.Multiply\n" +
            "        input:\n          - target: Value\n            from: {path: number}\n" +
            "code:\n  done:\n    language: csharp-expression\n    source: results.Count > 0\n";
        var generated = Build(yaml);
        var fluent = new WorkflowBuilder<FlowData, object>([], "Test")
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Values, (body, number) => body
                .Step<Multiply>(setup => setup.Input(act => act.Value).From(ctx => ctx.Item(number)))
                .WithId("multiply")
                .Result(valueExpression: ctx => ctx.PreviousStep.Result).WithId("items/result"),
                setup =>
                {
                    setup.As("number");
                    if (join != "sequential") setup.Parallel(join == "all" ? 2 : 1);
                    if (join == "any") setup.WaitAny();
                    if (join == "conditionally")
                        setup.WaitConditionally<int>(completeWhenExpression: (data, results) => results.Count > 0);
                    setup.Into(ctx => ctx.WorkflowData.Results);
                }).WithId("items").Build();
        Assert.Equal(GeneratedWorkflowTests.Shape(fluent), GeneratedWorkflowTests.Shape(generated));
        var first = await GeneratedWorkflowTests.Execute(fluent, new() { Values = [2, 3, 2] });
        var second = await GeneratedWorkflowTests.Execute(generated, new() { Values = [2, 3, 2] });
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Data.Results.Select(item => (item.Status, item.Value)),
            second.Data.Results.Select(item => (item.Status, item.Value)));
        var expected = join is "sequential" or "all" ? new[] { 4, 6, 4 } : [4];
        Assert.Equal(expected, second.Data.Results.Where(item => item.Status == ItemStatus.Completed).Select(item => item.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForEachSetupErrorsUseLoopLocation(bool readOnly)
    {
        var yaml = Header.Replace(typeof(FlowData).FullName!, readOnly ? typeof(ReadOnlyResultData).FullName! : typeof(FlowData).FullName!) +
            "  - kind: forEach\n    items: {path: workflow.Values}\n" +
            (readOnly ? "" : "    parallel: {join: conditionally, resultType: string, condition: {kind: function, function: done}}\n") +
            "    result: {kind: constant, value: 7}\n    into: {path: workflow.Results}\n" +
            "code:\n  done:\n    language: csharp-expression\n    source: results.Count > 0\n";
        var error = Assert.Throws<WorkflowEngineException>(() => Build(yaml));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.StartsWith("regression.yaml:5:", error.Message);
        Assert.Contains(readOnly ? "settable property" : "decides on String", error.Message);
    }

    [Fact]
    public void ParallelForEachBuildErrorsNameTheLoopRatherThanTheLastMapping()
    {
        var yaml = Header + "  - kind: forEach\n    items: {path: workflow.Values}\n    parallel: {maxConcurrency: 2}\n" +
            "    steps:\n      - kind: activity\n        activity: IxIFlow.Generators.Tests.Multiply\n" +
            "        input:\n          - target: Value\n            from: {path: item}\n" +
            "        output:\n          - source: Result\n            to: {path: workflow.Result}\n";
        var error = Assert.Throws<WorkflowEngineException>(() => Build(yaml));
        Assert.Contains("no combiner", error.Message);
        Assert.StartsWith("regression.yaml:5:", error.Message);
    }


    [Fact]
    public void ReferencedActivityKeysResolveWithoutEnumeratingTypeSymbols()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()],
            [new WorkflowText("package.yaml", Header + Activity.Replace("IxIFlow.Generators.Tests.Multiply", "test.multiply"))],
            optionsProvider: new WorkflowFileOptionsProvider("package.yaml"));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        Assert.Contains("global::IxIFlow.Generators.Tests.Multiply", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "F-06")]
    public async Task ActivityDefaultsAndOverridesMatchFluentFingerprintAndResults(bool overridden)
    {
        var yaml = Header + "  - {kind: activityRef, id: multiply, ref: multiply}\nactivities:\n  multiply:\n" +
            "    activity: IxIFlow.Generators.Tests.Multiply\n    defaults: {Factor: 3}\n    input:\n" +
            (overridden ? "      - target: Factor\n        from: {kind: constant, value: 5}\n" : "") +
            "      - target: Value\n        from: {path: workflow.Value}\n" +
            "    output:\n      - source: Result\n        to: {path: workflow.Result}\n";
        var generated = Build(yaml);
        var fluent = Workflow.Create<FlowData>("Test").Step<Multiply>(setup =>
        {
            if (overridden) setup.Input(act => act.Factor).From(ctx => 5);
            else setup.Input(act => act.Factor).From(ctx => 3);
            setup.Input(act => act.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result);
        }).WithId("multiply").Build();
        Assert.Equal(GeneratedWorkflowTests.Shape(fluent), GeneratedWorkflowTests.Shape(generated));
        var first = await GeneratedWorkflowTests.Execute(fluent, new() { Value = 4 });
        var second = await GeneratedWorkflowTests.Execute(generated, new() { Value = 4 });
        Assert.Equal(overridden ? 20 : 12, second.Data.Result);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Data.Result, second.Data.Result);
    }

    internal static WorkflowDefinition Build(string yaml)
    {
        var compilation = Compilation(WorkflowSourceTranslator.Translate(yaml, "regression.yaml"));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        try { return (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null)
            .GetMethod("Build")!.Invoke(null, null)!; }
        catch (TargetInvocationException error)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }

    internal static CSharpCompilation Compilation(string? source = null)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                typeof(FlowData).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
        return CSharpCompilation.Create("Regression_" + Guid.NewGuid().ToString("N"),
            source == null ? [] : [CSharpSyntaxTree.ParseText(source, path: "generated.g.cs")],
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

}

public sealed class DoubleActivity : IAsyncActivity
{
    public double Value { get; set; }
    public int Result { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Result = (int)(Value * 10); return Task.CompletedTask; }
}

public sealed class ReadOnlyResultData
{
    public List<int> Values { get; set; } = [];
    public List<ItemResult<int>> Results { get; } = [];
}
