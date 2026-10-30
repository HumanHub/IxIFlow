using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-04")]
[Trait("Spec", "W-05")]
[Trait("Spec", "F-24")]
public sealed class Slice4DiagnosticTests
{
    [Fact]
    public Task AssignmentPurityReportsTheMemberAndItsExactLocation() => Analyzer<PurityAnalyzer>(
        """
        public sealed class Data { public int Value { get; set; } }
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "AssignLocation")
                .Assign(ctx => ctx.WorkflowData.Value, ctx => {|#0:DateTime.UtcNow|}.Day).Build();
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"));

    [Fact]
    public Task SwitchPurityReportsTheMemberAndItsExactLocation() => Analyzer<PurityAnalyzer>(
        """
        public sealed class Data { public int Value { get; set; } }
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "SwitchLocation")
                .Switch(ctx => {|#0:DateTime.UtcNow|}.Day, cases => cases.Case(1, body => { }).Default(body => { })).Build();
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"));

    [Fact]
    public Task ThrowPurityReportsTheMemberAndItsExactLocation() => Analyzer<PurityAnalyzer>(
        """
        public sealed class Data { public int Value { get; set; } }
        public sealed class Failure : Exception { public int Code { get; init; } }
        public static class Flows
        {
            public static object Build() => Workflow.Create<Data>("ThrowLocation")
                .Throw<Failure>(ctx => new Failure { Code = {|#0:DateTime.UtcNow|}.Day }).Build();
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"));

    [Fact]
    public Task AssignmentWriteBackReportsTheReceiverAndSetterMember() => Analyzer<PurityAnalyzer>(
        """
        public struct Point { public int X { get; set; } }
        public sealed class Data
        {
            public Point Point { get => new(); set { _ = DateTime.UtcNow; } }
        }
        public static class Flows
        {
            public static object Build() => Workflow.Create<Data>("WriteBackLocation")
                .Assign(ctx => {|#0:ctx.WorkflowData.Point|}.X, ctx => 1).Build();
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow through Data.Point"));

    [Fact]
    public Task SwitchMutationReportsTheCallSiteAndMutatedMember() => Analyzer<PurityAnalyzer>(
        """
        public sealed class Data { public List<int> Items { get; set; } = new(); }
        public static class Flows
        {
            public static object Build() => Workflow.Create<Data>("MutationLocation")
                .Switch(ctx => {|#0:Pick(ctx.WorkflowData.Items)|}, cases => cases.Default(body => { })).Build();
            private static int Pick(List<int> items) { items.Clear(); return 1; }
        }
        """, Diagnostic(Descriptors.ImpureExpression, 0, "List.Clear (state mutation)"));

    [Fact]
    public Task InvalidEnumDefaultReportsThePropertyAndBothArguments() => Analyzer<MappingAnalyzer>(
        """
        using System.ComponentModel;
        public enum Choice : byte { None = 0, Read = 1 }
        public sealed class Data { }
        public sealed class Contract : IAsyncActivity
        {
            [ActivityInput, DefaultValue("256")]
            public Choice {|#0:Value|} { get; set; }
            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        public static class Flows
        {
            public static object Build() => Workflow.Create<Data>("DefaultLocation").Step<Contract>().Build();
        }
        """, Diagnostic(Descriptors.InvalidDefault, 0, "Contract.Value", "Choice"));

    [Fact]
    public Task WaitInputsAcceptPureMappingsWithoutDiagnostics() => Analyzer<PurityAnalyzer>(
        """
        public sealed class Data { public int Value { get; set; } }
        public sealed class Answer { public int Value { get; set; } }
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "PureWait")
                .WaitFor<Answer>("review", configure: wait => wait.Input(answer => answer.Value)
                    .From(ctx => ctx.WorkflowData.Value + 1)).Build();
        }
        """);
}