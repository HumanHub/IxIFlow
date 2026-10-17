using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

/// <summary>
/// Workflow code that reads the system clock is fixed to read the run's clock from the workflow context (D-42), where
/// the code has the context; elsewhere the diagnostic stands without a fix.
/// </summary>
[Trait("Feature", "authoring")]
[Trait("Spec", "A-05")]
public sealed class ContextClockCodeFixTests
{
    private const string Types = """

        public sealed class Data
        {
            public int Count { get; set; }
        }

        public sealed class Go
        {
            public int Hour { get; set; }
        }

        public sealed class Takes : IAsyncActivity
        {
            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
        """;

    [Fact]
    public async Task TheSystemClockIsReplacedWithTheContextsClock()
    {
        await CodeFix<PurityAnalyzer, ContextClockCodeFix>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Clock")
                    .Step<Takes>()
                    .If(ctx => {|#0:DateTime.UtcNow|}.Hour < 12, then => then.Step<Takes>())
                    .WaitFor<Go>("go", (go, context) => go.Hour < {|#1:DateTimeOffset.UtcNow|}.Hour)
                    .Build();
            }
            """ + Types, """
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Clock")
                    .Step<Takes>()
                    .If(ctx => ctx.UtcNow.UtcDateTime.Hour < 12, then => then.Step<Takes>())
                    .WaitFor<Go>("go", (go, context) => go.Hour < context.UtcNow.Hour)
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"),
            Diagnostic(Descriptors.ImpureExpression, 1, "DateTimeOffset.UtcNow"));
    }

    // A completion condition sees only the data, and local time depends on the host's time zone: no fix.
    [Fact]
    public async Task NoFixIsOfferedWithoutTheContextOrForLocalTime()
    {
        const string source = """
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("NoFix")
                    .Step<Takes>()
                    .If(ctx => {|#0:DateTime.Now|}.Hour < 12, then => then.Step<Takes>())
                    .Parallel(parallel => parallel
                        .WaitConditionally(data => {|#1:DateTime.UtcNow|}.Hour > data.Count)
                        .Do(branch => branch.Step<Takes>()))
                    .Build();
            }
            """ + Types;
        await CodeFix<PurityAnalyzer, ContextClockCodeFix>(source, source,
            Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.Now"),
            Diagnostic(Descriptors.ImpureExpression, 1, "DateTime.UtcNow"));
    }
}
