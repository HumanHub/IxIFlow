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

            public sealed class Totals : ICombiner<int>
            {
                public int Combine(int original, IReadOnlyList<int> branchValues) => branchValues.Sum() + original;

                // Not run by a join.
                private static int Unused() => DateTime.Now.Day;
            }
            """,
            Diagnostic(Descriptors.ImpureCombiner, 0, "Stamped", "Environment.ProcessId"),
            Diagnostic(Descriptors.ImpureCombiner, 1, "Stamped", "DateTime.UtcNow"));
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
