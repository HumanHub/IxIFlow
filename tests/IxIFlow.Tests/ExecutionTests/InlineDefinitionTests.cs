using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.Loader;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring"), Trait("Spec", "F-17")]
public sealed class InlineDefinitionTests
{
    [Fact]
    public void RepeatedNestedGraftsQualifyFinalAuthorIdsWithoutRewritingSteps()
    {
        var definition = Workflow.Create<Data>("Caller").Use<Nested, Data>().WithId("first")
            .Use<Nested, Data>().WithId("second").Build();
        Assert.Contains(definition.StepTable, s => s.AuthorId == "first/leaf/work");
        Assert.Contains(definition.StepTable, s => s.AuthorId == "second/leaf/work");
        Assert.Equal("leaf", definition.Steps[0].InlineSteps[0].Id);
        Assert.Equal("work", definition.Steps[0].InlineSteps[0].InlineSteps[0].Id);
        Assert.Equal(2, definition.StepTable.Count(s => s.CatalogId.Contains("/inline/0/inline/0")));
        Assert.Contains(definition.StepTable, s => s.AuthorId == "first/leaf/work" &&
            s.SourceLocation == "leaf.cs:12");
    }

    [Fact]
    public void QualifiedIdsCannotCollideWithCallerIds()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Collision")
            .Use<Leaf, Data>().WithId("call").Step<Noop>().WithId("call/work").Build());
        Assert.Contains("Duplicate step ID 'call/work'", error.Message);
    }

    [Fact]
    public void CompensationCatalogEntriesRetainQualifiedAuthorsAndPhysicalSources()
    {
        var definition = Workflow.Create<Data>("CompensationSites")
            .Use<InlineRuntimeTests.Compensating, InlineRuntimeTests.Callee>().WithId("first")
            .Use<InlineRuntimeTests.Compensating, InlineRuntimeTests.Callee>().WithId("second").Build();
        var compensations = definition.StepTable.Where(s => s.CatalogId.Contains("/compensation/")).ToArray();
        Assert.Equal(2, compensations.Length);
        Assert.Contains(compensations, s => s.AuthorId.StartsWith("first/", StringComparison.Ordinal));
        Assert.Contains(compensations, s => s.AuthorId.StartsWith("second/", StringComparison.Ordinal));
        Assert.All(compensations, s => Assert.Contains("InlineRuntimeTests.cs:", s.SourceLocation));
    }

    [Fact]
    public void FactoryRunsOncePerSiteAndVerifiesEveryPinField()
    {
        var pin = Pin(Fresh());
        var count = 0;
        WorkflowDefinition Factory() { count++; return Fresh(); }
        var definition = Workflow.Create<Data>("FactoryCaller").Use<Data>(Factory, pin).Build();
        Assert.Equal(1, count);
        Assert.Equal(pin.StructureHash, definition.Steps[0].InlineStructureHash);
        foreach (var bad in new[] { pin with { Name = "Other" }, pin with { Version = 2 },
                     pin with { StructureHash = "bad" }, pin with { ContractHash = "bad" } })
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, Assert.Throws<WorkflowEngineException>(() =>
                Workflow.Create<Data>("BadPin").Use<Data>(Fresh, bad)).Kind);
    }

    [Fact]
    public void FactoryCannotReuseGraftedOrCatalogedStepsEvenThroughANewDefinition()
    {
        var pin = Pin(Fresh());
        var shared = Fresh();
        Workflow.Create<Data>("First").Use<Data>(() => shared, pin).Build();
        Assert.Contains("fresh definition", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<Data>("Second").Use<Data>(() => shared, pin)).Message);
        var cataloged = Fresh();
        WorkflowDefinitionValidator.Inspect(cataloged);
        Assert.Contains("fresh definition", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<Data>("Cataloged").Use<Data>(() => new WorkflowDefinition
            { Name = "Factory", Version = 1, WorkflowDataType = typeof(Data), Steps = cataloged.Steps }, pin)).Message);
    }

    [Fact]
    public void ConstructorCyclesAndFactoryCyclesAreGuardedBeforeUserCodeAndGuardResets()
    {
        Assert.Contains("cycle", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<Data>("Cycle").Use<Recursive, Data>()).Message, StringComparison.OrdinalIgnoreCase);
        var pin = new WorkflowInlinePin("Factory", 1, "", "");
        WorkflowDefinition Recurse() => Workflow.Create<Data>("Factory").Use<Data>(Recurse, pin).Build();
        Assert.Contains("cycle", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<Data>("Cycle").Use<Data>(Recurse, pin)).Message, StringComparison.OrdinalIgnoreCase);
        Workflow.Create<Data>("AfterCycle").Use<Leaf, Data>().Build();
    }

    [Fact]
    public void FactoryDepthIsBoundedBeforeTheSeventeenthFactory()
    {
        var called = 0;
        WorkflowDefinition Recurse(int depth)
        {
            called++;
            return Workflow.Create<Data>("Depth" + depth).Use<Data>(() => Recurse(depth + 1),
                new("Depth" + (depth + 1), 1, "", "")).Build();
        }
        Assert.Contains("depth", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Caller")
            .Use<Data>(() => Recurse(1), new("Depth1", 1, "", ""))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, called);
    }

    [Fact]
    public void RequestsAndForEachHandlesAreSeparateNamespaces()
    {
        var definition = Workflow.Create<Data>("Namespaces").Step<Noop>()
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Use<WithItems, Data>())
            .Use<WithRequest, Data>().WaitFor<Signal>("approval").Build();
        Assert.Null(definition.Steps[^1].ReplyToRequest);
        Assert.Equal("approval", definition.Steps[^2].InlineSteps[^1].ReplyToRequest);
        WorkflowDefinitionValidator.Validate(definition);
    }

    [Fact]
    public void ParallelConflictAnalysisOnlySeesCallSiteOutputs()
    {
        Workflow.Create<Data>("SeparateWrites").Step<Noop>().Parallel(p => p
            .Do(b => b.Use<WritesValue, Other>())
            .Do(b => b.Step<Noop>(a => a.Output(x => x.Value).To(c => c.WorkflowData.Value)))).Build();
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("ConflictingWrites").Step<Noop>()
            .Parallel(p => p.Do(b => b.Use<WritesValue, Other>(u => u.Output(x => x.Value).To(c => c.WorkflowData.Value)))
                .Do(b => b.Step<Noop>(a => a.Output(x => x.Value).To(c => c.WorkflowData.Value)))).Build());
    }

    [Fact]
    public void LiteralDemandsUnionTransitivelyAndDynamicOrConflictingDemandsFail()
    {
        var definition = Workflow.Create<Data>("Demands").Demand("region", "east")
            .Use<Demanded, Data>().Build();
        var region = Assert.Single(definition.Demands);
        Assert.Equal("east", region.Value);
        Assert.True(region.Inherit);
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Conflict").Demand("region", "west")
            .Use<Demanded, Data>().Build());
        Assert.Contains("DemandFrom", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Dynamic")
            .Use<DynamicDemand, Data>()).Message);
    }

    [Fact]
    public void InlineDataRequiresFaithfulCopyAndHasNoCancellationSetting()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Uncopyable").Use<BadCopy, GetterOnly>());
        Assert.DoesNotContain(typeof(IWorkflowInlineSetupBuilder<Data, Other, object>).GetMethods(),
            method => method.Name == "CancelWithParent");
    }

    [Fact]
    public void CallerHashIncludesEveryDataTypesCombinersAndExcludesAuthorIds()
    {
        var first = Workflow.Create<Data>("Hash").Use<Leaf, Data>().WithId("one").Build();
        var second = Workflow.Create<Data>("Hash").Use<Leaf, Data>().WithId("two").Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(second));
        Assert.Contains(WorkflowDefinitionValidator.GetDependencies(first), d => d.Name == "Leaf" && d.Version == 1);
    }

    [Fact]
    public void ChangingOnlyTheCalleeCombinerChangesTheCallerHash()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using IxIFlow.Builders;
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            public sealed class Caller { public string Other { get; set; } = ""; }
            public sealed class Data { [Combine<Sum<int>>] public int Value { get; set; } }
            public sealed class Noop : IAsyncActivity { public Task ExecuteAsync(IActivityContext c, CancellationToken t) => Task.CompletedTask; }
            [Workflow("Callee")] public sealed class Body : IWorkflow<Data>
            { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>(); }
            public static class Definition
            { public static WorkflowDefinition Build() => Workflow.Create<Caller>("Caller").Use<Body, Data>().Build(); }
            """;
        using var first = new Compiled(source);
        using var second = new Compiled(source.Replace("Sum<int>", "FirstWins<int>"));
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first.Definition), WorkflowDefinitionValidator.Inspect(second.Definition));
    }

    private sealed class Compiled : IDisposable
    {
        private readonly AssemblyLoadContext context = new("inline-combiner", isCollectible: true);
        public WorkflowDefinition Definition { get; }
        public Compiled(string source)
        {
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var compilation = CSharpCompilation.Create("InlineCombinerFixture", [CSharpSyntaxTree.ParseText(source)],
                paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            stream.Position = 0;
            Definition = (WorkflowDefinition)context.LoadFromStream(stream).GetType("Definition")!.GetMethod("Build")!.Invoke(null, null)!;
        }
        public void Dispose() => context.Unload();
    }

    private static WorkflowDefinition Fresh() => Workflow.Create<Data>("Factory").Step<Noop>().WithId("work").Build();
    private static WorkflowInlinePin Pin(WorkflowDefinition definition) => new(definition.Name, definition.Version,
        WorkflowDefinitionValidator.Inspect(definition), WorkflowDefinitionValidator.GetContract(definition).Hash);
    public sealed class Data { public int Value { get; set; } public List<int> Items { get; set; } = [1, 2]; }
    public sealed class Other { public int Value { get; set; } }
    public sealed class GetterOnly { public List<int> Values { get; } = []; }
    public sealed class Signal { }
    public sealed class Noop : IAsyncActivity { public int Value { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    public sealed class Ask : IRequestActivity<Signal> { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    [Workflow("Leaf")] public sealed class Leaf : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>(sourcePath: "leaf.cs", sourceLine: 12).WithId("work", "leaf.cs", 12); }
    [Workflow("Nested")] public sealed class Nested : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Use<Leaf, Data>().WithId("leaf"); }
    [Workflow("Recursive")] public sealed class Recursive : IWorkflow<Data>
    { public Recursive() => Workflow.Create<Data>("Recursion").Use<Recursive, Data>(); public void Build(IWorkflowBuilder<Data> b) { } }
    [Workflow("WithItems")] public sealed class WithItems : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Noop>(a => a.Input(x => x.Value).From(c => c.Item(item)))); }
    [Workflow("WithRequest")] public sealed class WithRequest : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Request<Ask>("approval").WaitFor<Signal>("approval"); }
    [Workflow("WritesValue")] public sealed class WritesValue : IWorkflow<Other>
    { public void Build(IWorkflowBuilder<Other> b) => b.Step<Noop>().Parallel(p => p.Do(x => x.Step<Noop>(a => a.Output(x => x.Value).To(c => c.WorkflowData.Value)))); }
    [Workflow("Demanded")] public sealed class Demanded : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Demand("region", "east", inherit: true); }
    [Workflow("DynamicDemand")] public sealed class DynamicDemand : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.DemandFrom("region", c => "east"); }
    [Workflow("BadCopy")] public sealed class BadCopy : IWorkflow<GetterOnly>
    { public void Build(IWorkflowBuilder<GetterOnly> b) { } }
}
