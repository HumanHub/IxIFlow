using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

public sealed class SwitchVerificationReviewTests
{
    [Theory]
    [InlineData("1", false)]
    [InlineData("DateTime.UtcNow.Hour", true)]
    public async Task SwitchLabelPreservesMappingAccessorRules(string label, bool impure)
    {
        var diagnostics = await Analyze($$"""
                                          public class Data { public DateTime Stamp { get => DateTime.UtcNow; set { } } }
                                          public class Stamping : IAsyncActivity
                                          {
                                              public DateTime Stamp { get => DateTime.UtcNow; set { } }
                                              public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
                                          }
                                          public class Author
                                          {
                                              public object Build() => Workflow.Create<Data>("Mapping")
                                                  .Switch(ctx => 1, cases => cases.Case({{label}}, body => body
                                                      .Step<Stamping>(step => step.Output(a => a.Stamp).To(ctx => ctx.WorkflowData.Stamp))))
                                                  .Build();
                                          }
                                          """);

        if (impure)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("DateTime.UtcNow", diagnostic.GetMessage());
            Assert.Equal("DateTime.UtcNow", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        }
        else
            Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SwitchLabelInitializerInsideCasesStaysScoped()
    {
        var diagnostics = await Analyze("""
                                        public class Data { public DateTime Stamp { get => DateTime.UtcNow; set { } } }
                                        public class Stamping : IAsyncActivity
                                        {
                                            public DateTime Stamp { get => DateTime.UtcNow; set { } }
                                            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
                                        }
                                        public class Author
                                        {
                                            public object Build() => Workflow.Create<Data>("LocalMapping")
                                                .Switch(ctx => 1, cases =>
                                                {
                                                    var label = 1;
                                                    cases.Case(label, body => body.Step<Stamping>(step => step.Output(a => a.Stamp).To(ctx => ctx.WorkflowData.Stamp)));
                                                }).Build();
                                        }
                                        """);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("items.Clear();", true)]
    [InlineData("", false)]
    public async Task SwitchInterfaceSetterFollowsConcreteReceiver(string effect, bool mutation)
    {
        var diagnostics = await Analyze($$"""
                                          public interface IView { int Value { set; } }
                                          public class View(List<int> items) : IView { public int Value { set { {{effect}} } } }
                                          public class Data { public List<int> Items = new(); }
                                          public class Author
                                          {
                                              public object Build() => Workflow.Create<Data>("Setter").Switch(ctx => Pick(ctx.WorkflowData.Items), cases => { }).Build();
                                              private static int Pick(List<int> items) { ((IView)new View(items)).Value = 1; return 1; }
                                          }
                                          """);

        Assert.Equal(mutation, diagnostics.Any(d => d.GetMessage().Contains("state mutation")));

        if (!mutation)
            Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("((ISink)new Sink()).Value = items;", true)]
    [InlineData("((ISink)new Sink()).Value = new List<int>();", false)]
    [InlineData("((ISink)new Sink())[items] = 1;", true)]
    [InlineData("((ISink)new Sink())[new List<int>()] = 1;", false)]
    public async Task SwitchInterfaceSettersKeepValueAndIndexBindings(string assignment, bool mutation)
    {
        var diagnostics = await Analyze($$"""
                                          public interface ISink { List<int> Value { set; } int this[List<int> index] { set; } }
                                          public class Sink : ISink
                                          {
                                              List<int> ISink.Value { set { value.Clear(); } }
                                              int ISink.this[List<int> index] { set { index.Clear(); } }
                                          }
                                          public class Data { public List<int> Items = new(); }
                                          public class Author
                                          {
                                              public object Build() => Workflow.Create<Data>("Bindings").Switch(ctx => Pick(ctx.WorkflowData.Items), cases => { }).Build();
                                              private static int Pick(List<int> items) { {{assignment}} return 1; }
                                          }
                                          """);

        Assert.Equal(mutation, diagnostics.Any(d => d.GetMessage().Contains("state mutation")));

        if (!mutation)
            Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SwitchInterfaceSetterAlsoRunsPurityChecks()
    {
        var diagnostics = await Analyze("""
                                        public interface IView { int Value { set; } }
                                        public class View : IView { public int Value { set { _ = DateTime.UtcNow; } } }
                                        public class Data { }
                                        public class Author
                                        {
                                            public object Build() => Workflow.Create<Data>("SetterPurity").Switch(ctx => Pick(), cases => { }).Build();
                                            private static int Pick() { ((IView)new View()).Value = 1; return 1; }
                                        }
                                        """);

        Assert.Contains("DateTime.UtcNow through View.Value through Author.Pick()", Assert.Single(diagnostics).GetMessage());
    }

    [Theory]
    [InlineData("&&", "false", "value.Items.Clear();", true)]
    [InlineData("||", "true", "value.Items.Clear();", true)]
    [InlineData("&&", "false", "_ = DateTime.UtcNow;", true)]
    [InlineData("||", "true", "_ = DateTime.UtcNow;", true)]
    [InlineData("&&", "false", "", false)]
    [InlineData("||", "true", "", false)]
    [InlineData("&&", "&", "left.Items.Clear();", true)]
    [InlineData("||", "|", "left.Items.Clear();", true)]
    [InlineData("&&", "&", "_ = DateTime.UtcNow;", true)]
    [InlineData("||", "|", "_ = DateTime.UtcNow;", true)]
    public async Task SwitchLabelLocalRunsConditionalOperators(string op, string truth, string effect, bool impure)
    {
        var diagnostics = await Analyze($$"""
                                          public class Truth(List<int> items)
                                          {
                                              public List<int> Items = items;
                                              public int Code => 1;
                                              public static bool operator false(Truth value) { {{(truth == "false" ? effect : "")}} return false; }
                                              public static bool operator true(Truth value) { {{(truth == "true" ? effect : "")}} return true; }
                                              public static Truth operator &(Truth left, Truth right) { {{(truth == "&" ? effect : "")}} return left; }
                                              public static Truth operator |(Truth left, Truth right) { {{(truth == "|" ? effect : "")}} return left; }
                                          }
                                          public class Data { }
                                          public class Author
                                          {
                                              public object Build(List<int> items)
                                              {
                                                  var label = (new Truth(items) {{op}} new Truth(items)).Code;
                                                  return Workflow.Create<Data>("LocalLabel").Switch(ctx => 1, cases => cases.Case(label, body => { })).Build();
                                              }
                                          }
                                          """);

        Assert.Equal(impure, diagnostics.Any());

        if (impure)
            Assert.Contains(diagnostics, d => d.GetMessage().Contains(effect.Contains("Clear") ? "state mutation" : "DateTime.UtcNow"));
    }

    [Theory]
    [InlineData("items", "flag && (alias = new List<int>()) != null", true)]
    [InlineData("items", "flag || (alias = new List<int>()) != null", true)]
    [InlineData("items", "false && (alias = new List<int>()) != null", true)]
    [InlineData("items", "true || (alias = new List<int>()) != null", true)]
    [InlineData("items", "true && (alias = new List<int>()) != null", false)]
    [InlineData("items", "false || (alias = new List<int>()) != null", false)]
    [InlineData("new List<int>()", "flag && (alias = items) != null", true)]
    [InlineData("new List<int>()", "flag || (alias = items) != null", true)]
    [InlineData("new List<int>()", "false && (alias = items) != null", false)]
    [InlineData("new List<int>()", "true || (alias = items) != null", false)]
    public async Task SwitchLabelLocalMergesOnlyEvaluatedConditionalEffects(string initial, string conditional, bool mutation)
    {
        var diagnostics = await Analyze($$"""
                                          public class Data { }
                                          public class Author
                                          {
                                              public object Build(List<int> items, bool flag)
                                              {
                                                  var alias = items;
                                                  var label = Consume(alias = {{initial}}, {{conditional}}, alias);
                                                  return Workflow.Create<Data>("ShortCircuit").Switch(ctx => 1, cases => cases.Case(label, body => { })).Build();
                                              }
                                              private static int Consume(List<int> before, bool condition, List<int> after) { after.Clear(); return 1; }
                                          }
                                          """);

        Assert.Equal(mutation, diagnostics.Any(d => d.GetMessage().Contains("state mutation")));

        if (!mutation)
            Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("&&")]
    [InlineData("||")]
    public async Task SwitchLabelLocalKeepsOverloadedShortCircuitAlternative(string op)
    {
        var diagnostics = await Analyze($$"""
                                          public class Truth
                                          {
                                              public Truth(List<int> items) { }
                                              public static bool operator false(Truth value) => false;
                                              public static bool operator true(Truth value) => true;
                                              public static Truth operator &(Truth left, Truth right) => left;
                                              public static Truth operator |(Truth left, Truth right) => left;
                                          }
                                          public class Data { }
                                          public class Author
                                          {
                                              public object Build(List<int> items)
                                              {
                                                  var alias = items;
                                                  var label = Consume(new Truth(alias = items) {{op}} new Truth(alias = new List<int>()), alias);
                                                  return Workflow.Create<Data>("OverloadedShortCircuit").Switch(ctx => 1, cases => cases.Case(label, body => { })).Build();
                                              }
                                              private static int Consume(Truth value, List<int> after) { after.Clear(); return 1; }
                                          }
                                          """);

        Assert.Contains(diagnostics, d => d.GetMessage().Contains("state mutation"));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll"))
            .Distinct().Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create("SwitchVerification", [CSharpSyntaxTree.ParseText(Verify.Usings + source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.All(diagnostics, d => Assert.Equal("IXIF0101", d.Id));

        return diagnostics;
    }
}