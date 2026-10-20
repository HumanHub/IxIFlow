using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

public sealed class DemandPurityAnalyzerTests
{
    [Fact]
    public Task DemandFromChecksTheExpressionAndHelpers() => Analyzer<PurityAnalyzer>("""
        public sealed class DemandData { public string Region { get; set; } = "eu"; }
        public static class Flows
        {
            static string Host() => Environment.MachineName;
            static string Clock(WorkflowContext<DemandData> ctx) => ctx.UtcNow.ToString();
            public static object Build() => Workflow.Create<DemandData>("Demand")
                .DemandFrom("region", ctx => ctx.WorkflowData.Region)
                .DemandFrom("host", ctx => {|#0:Host()|})
                .DemandFrom("random", ctx => {|#1:Guid.NewGuid()|}.ToString())
                .DemandFrom("time", ctx => {|#2:ctx.UtcNow|}.ToString())
                .DemandFrom("helper-time", ctx => {|#3:Clock(ctx)|})
                .Build();
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "Environment.MachineName through Flows.Host()"),
        Diagnostic(Descriptors.ImpureExpression, 1, "Guid.NewGuid"),
        Diagnostic(Descriptors.ImpureExpression, 2, "WorkflowContext.UtcNow"),
        Diagnostic(Descriptors.ImpureExpression, 3, "WorkflowContext.UtcNow through Flows.Clock()"));
}
