using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

/// <summary>
/// The purity analyzer reports at compile time the members the engine refuses in expressions when a workflow is built
/// (D-07, D-42, spec A-05), in every kind of workflow code, in the helpers that code calls, and in combiners, which
/// the build cannot see.
/// </summary>
[Trait("Feature", "authoring")]
[Trait("Spec", "A-05")]
public sealed class PurityAnalyzerTests
{
    private const string Types = """

        public sealed class Data
        {
            public string Folder { get; set; } = "";
            public DateTime Due { get; set; }
            public byte[] Bytes { get; set; } = [];
            public int Count { get; set; }
            public List<int> Items { get; set; } = [];
            public bool IsLate => DateTime.UtcNow > Due;
        }

        public sealed class Go
        {
            public int Hour { get; set; }
        }

        public sealed class Takes : IAsyncActivity
        {
            public string Text { get; set; } = "";

            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
        """;

    public static TheoryData<string, string> Impure => new()
    {
        { "DateTime.Now", "DateTime.Now" },
        { "DateTime.Today", "DateTime.Today" },
        { "DateTimeOffset.UtcNow", "DateTimeOffset.UtcNow" },
        { "Guid.NewGuid", "Guid.NewGuid()" },
        { "Environment.MachineName", "Environment.MachineName" },
        { "Random.Next", "new Random().Next()" },
        { "new Random(...)", "new Random(1)" },
        { "new HttpClient(...)", "new System.Net.Http.HttpClient()" },
        { "File.ReadAllText", "File.ReadAllText(\"settings.json\")" },
        { "TimeProvider.System", "TimeProvider.System" },
        { "IDbCommand.ExecuteScalar", "Command.ExecuteScalar()" },
        { "new StreamReader(path)", "new StreamReader(\"settings.json\")" },
        { "Task.Delay", "Task.Delay(1)" },
        { "new TcpClient(...)", "new System.Net.Sockets.TcpClient()" },
        { "Dns.GetHostName", "System.Net.Dns.GetHostName()" }
    };

    [Theory]
    [MemberData(nameof(Impure))]
    public async Task AnImpureMemberInAMappingIsReported(string member, string code)
    {
        await Analyzer<PurityAnalyzer>($$"""
            public static class Flows
            {
                public static readonly System.Data.IDbCommand Command = null!;

                public static object Build() => Workflow.Create<Data>("ImpureMapping")
                    .Step<Takes>(step => step.Input(activity => activity.Text).From(ctx => "" + {|#0:{{code}}|}))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, member));
    }

    // Conditions are expression trees; wait predicates, loop conditions, item lists, completion conditions, results
    // and subjects are delegates. The analyzer reads both.
    [Fact]
    public async Task EveryKindOfWorkflowCodeIsChecked()
    {
        await Analyzer<PurityAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Impure")
                    .Request<Takes>("ask", subject: ctx => {|#0:Environment.UserName|})
                    .If(ctx => {|#1:DateTime.UtcNow|}.Hour < 12, then => then.Step<Takes>())
                    .WaitFor<Go>("go", (go, ctx) => go.Hour < {|#2:DateTime.Now|}.Hour)
                    .WhileDo(ctx => {|#3:Guid.NewGuid()|} == Guid.Empty, body => body.Step<Takes>())
                    .DoWhile(body => body.Step<Takes>(), ctx =>
                    {
                        {|#4:Thread.Sleep(1)|};
                        return false;
                    })
                    .ForEach(ctx => {|#5:Directory.GetFiles("in")|}, (body, file) => body.Step<Takes>(),
                        setup => setup.WaitConditionally<int>((data, results) => {|#6:Random.Shared.Next()|} > 1))
                    .Parallel(parallel => parallel
                        .WaitConditionally(data => {|#7:File.Exists("done")|})
                        .Do(branch => branch.Step<Takes>()))
                    .Result(ctx => {|#8:Environment.TickCount|})
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, "Environment.UserName"),
            Diagnostic(Descriptors.ImpureExpression, 1, "DateTime.UtcNow"),
            Diagnostic(Descriptors.ImpureExpression, 2, "DateTime.Now"),
            Diagnostic(Descriptors.ImpureExpression, 3, "Guid.NewGuid"),
            Diagnostic(Descriptors.ImpureExpression, 4, "Thread.Sleep"),
            Diagnostic(Descriptors.ImpureExpression, 5, "Directory.GetFiles"),
            Diagnostic(Descriptors.ImpureExpression, 6, "Random.Next"),
            Diagnostic(Descriptors.ImpureExpression, 7, "File.Exists"),
            Diagnostic(Descriptors.ImpureExpression, 8, "Environment.TickCount"));
    }

    // The build does not look into a helper; the analyzer does when the helper is in the compilation, and reports
    // the call that leads to the impure member: a property getter, a method group, a nested call.
    [Fact]
    public async Task AHelperInTheCompilationIsLookedInto()
    {
        await Analyzer<PurityAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Helpers")
                    .Step<Takes>()
                    .If(ctx => {|#0:ctx.WorkflowData.IsLate|}, then => then.Step<Takes>())
                    .WhileDo({|#1:Stop|}, body => body.Step<Takes>())
                    .WaitFor<Go>("go", (go, ctx) => {|#2:new Stamp()|}.Hour == go.Hour)
                    .If(ctx => Count(ctx.WorkflowData) > 0, then => then.Step<Takes>())
                    .Build();

                private static bool Stop(WorkflowContext<Data, Takes> ctx) => Clock.Day() > 3;

                private static int Count(Data data) => data.Items.Count(item => item > 0);
            }

            public static class Clock
            {
                public static int Day() => DateTime.Today.Day;
            }

            public sealed class Stamp
            {
                public int Hour { get; } = DateTimeOffset.Now.Hour;
            }
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow through Data.IsLate"),
            Diagnostic(Descriptors.ImpureExpression, 1, "DateTime.Today through Clock.Day() through Flows.Stop()"),
            Diagnostic(Descriptors.ImpureExpression, 2, "DateTimeOffset.Now through new Stamp(...)"));
    }

    // Code kept in a local is reported where it is written; code kept in a field or property, or made by a method,
    // is reported where it is given, naming where it is kept.
    [Fact]
    public async Task StoredWorkflowCodeIsChecked()
    {
        await Analyzer<PurityAnalyzer>("""
            using System.Linq.Expressions;

            public static class Flows
            {
                private static readonly Func<Go, WorkflowContext<Data>, bool> Matches = (go, ctx) => go.Hour < DateTime.Now.Hour;

                private static Expression<Func<WorkflowContext<Data, Takes>, bool>> Late => ctx => DateTime.Today.Day > 1;

                public static object Build()
                {
                    Expression<Func<WorkflowContext<Data, Takes>, bool>> condition = ctx => {|#0:DateTime.UtcNow|}.Hour > 12;
                    return Workflow.Create<Data>("Stored")
                        .Step<Takes>()
                        .If(condition, then => then.Step<Takes>())
                        .If({|#2:Late|}, then => then.Step<Takes>())
                        .WaitFor<Go>("go", {|#1:Matches|})
                        .Build();
                }
            }
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"),
            Diagnostic(Descriptors.ImpureExpression, 1, "DateTime.Now through Flows.Matches"),
            Diagnostic(Descriptors.ImpureExpression, 2, "DateTime.Today through Flows.Late"));
    }

    // A selector only names its property and a destination sets its property, so their getters never run; a local
    // function runs where it is called, not where it is declared.
    [Fact]
    public async Task OnlyCodeThatRunsIsChecked()
    {
        await Analyzer<PurityAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Stamps>("Runs")
                    .Step<Stamping>(step => step
                        .Input(activity => activity.Stamp).From(ctx => ctx.UtcNow.UtcDateTime)
                        .Output(activity => activity.Stamp).To(ctx => ctx.WorkflowData.Stamp))
                    .WhileDo(ctx =>
                    {
                        bool Unused() => DateTime.UtcNow.Hour > 0;
                        return false;
                    }, body => body.Step<Stamping>())
                    .WhileDo(ctx =>
                    {
                        bool Used() => DateTime.UtcNow.Hour > 0;
                        return {|#0:Used()|};
                    }, body => body.Step<Stamping>())
                    .Build();
            }

            public sealed class Stamping : IAsyncActivity
            {
                public DateTime Stamp { get => DateTime.UtcNow; set { } }

                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
                    Task.CompletedTask;
            }

            public sealed class Stamps
            {
                public DateTime Stamp { get => DateTime.UtcNow; set { } }
            }
            """,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow through Used()"));
    }

    // User-defined operators and conversions run, and so do the base constructors a constructor calls implicitly.
    [Fact]
    public async Task OperatorsAndBaseConstructorsAreLookedInto()
    {
        await Analyzer<PurityAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Operators")
                    .Step<Takes>()
                    .If(ctx => {|#0:new Flag()|}, then => then.Step<Takes>())
                    .WhileDo(ctx => {|#1:new Derived()|}.Seed > 0, body => body.Step<Takes>())
                    .Build();
            }

            public sealed class Flag
            {
                public static implicit operator bool(Flag flag) => DateTime.UtcNow.Hour > 0;
            }

            public class Base
            {
                public int Seed { get; } = Environment.TickCount;
            }

            public sealed class Derived : Base;
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow through Flag.implicit operator bool(Flag)"),
            Diagnostic(Descriptors.ImpureExpression, 1, "Environment.TickCount through new Base(...) through new Derived(...)"));
    }

    [Fact]
    public async Task AnImpureCombinerIsReported()
    {
        await Analyzer<PurityAnalyzer>("""
            public sealed class Stamped : ICombiner<DateTime>
            {
                private readonly int _seed = {|#0:Environment.ProcessId|};

                public DateTime Combine(DateTime original, IReadOnlyList<DateTime> branchValues) =>
                    branchValues.Count > _seed ? {|#1:DateTime.UtcNow|} : original;
            }

            public sealed class Seeded : ICombiner<int>
            {
                private static readonly int Seed = {|#2:Environment.TickCount|};

                public int Combine(int original, IReadOnlyList<int> branchValues) => original + Seed;
            }

            public class Picker
            {
                public int Combine(int original, IReadOnlyList<int> branchValues) => Environment.ProcessId;
            }

            public sealed class {|#3:Inherited|} : Picker, ICombiner<int>;

            public sealed class Totals : ICombiner<int>
            {
                public int Combine(int original, IReadOnlyList<int> branchValues) => branchValues.Sum() + original;

                // Not run by a join.
                private static int Unused() => DateTime.Now.Day;
            }
            """,
            Diagnostic(Descriptors.ImpureCombiner, 0, "Stamped", "Environment.ProcessId"),
            Diagnostic(Descriptors.ImpureCombiner, 1, "Stamped", "DateTime.UtcNow"),
            Diagnostic(Descriptors.ImpureCombiner, 2, "Seeded", "Environment.TickCount"),
            Diagnostic(Descriptors.ImpureCombiner, 3, "Inherited", "Environment.ProcessId through Picker.Combine()"));
    }

    // Pure members of the same types and namespaces, the run's clock, a stream in memory, nameof, builder code that
    // runs once when the workflow is built, and a lambda given to the application's own method report nothing.
    [Fact]
    public async Task PureCodeReportsNothing()
    {
        await Analyzer<PurityAnalyzer>("""
            public static class Flows
            {
                public static object Build()
                {
                    Use(() => DateTime.Now);
                    return Workflow.Create<Data>("Pure")
                        .Step<Takes>(step =>
                        {
                            var built = DateTime.Now;
                            step.Input(activity => activity.Text)
                                .From(ctx => Path.Combine(ctx.WorkflowData.Folder, nameof(DateTime.Now)) +
                                             DateTime.MinValue.Year + ctx.UtcNow.Hour +
                                             new StreamReader(new MemoryStream(ctx.WorkflowData.Bytes)).ReadToEnd());
                        })
                        .WaitFor<Go>("go", (go, ctx) => go.Hour > ctx.UtcNow.Hour)
                        .Build();
                }

                private static void Use(Func<DateTime> clock)
                {
                }
            }
            """ + Types);
    }
}
