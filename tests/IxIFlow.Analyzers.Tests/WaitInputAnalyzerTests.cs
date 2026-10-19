using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

public sealed class WaitInputAnalyzerTests
{
    private const string Types = """
        public sealed class Data { public int Value { get; set; } }
        public sealed class Answer
        {
            public int Value { get; set; }
            public int Fixed { get; private set; }
            [System.ComponentModel.DataAnnotations.Required] public string Customer { get; set; } = "";
        }
        """;

    [Theory]
    [InlineData("WaitFor")]
    [InlineData("Suspend")]
    public Task WaitInputPurityIsChecked(string method) => Analyzer<PurityAnalyzer>($$"""
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "WaitPurity")
                .{{method}}<Answer>("review", configure: wait => wait.Input(answer => answer.Value)
                    .From(ctx => {|#0:DateTime.UtcNow|}.Hour)).Build();
        }
        """ + Types, Diagnostic(Descriptors.ImpureExpression, 0, "DateTime.UtcNow"));

    [Fact]
    public Task WaitInputNeedsAPublicSetter() => Analyzer<MappingAnalyzer>("""
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "WaitSetter")
                .WaitFor<Answer>("review", configure: wait => wait
                    .DefaultPayload(new Answer { Customer = "Ada" })
                    .Input(answer => answer.{|#0:Fixed|}).From(ctx => ctx.WorkflowData.Value)).Build();
        }
        """ + Types, Diagnostic(Descriptors.NotAnInput, 0, "Event", "Answer.Fixed", "public setter"));

    [Fact]
    public Task RequiredSuggestionInputIsChecked() => Analyzer<MappingAnalyzer>("""
        public static class Flows
        {
            public static object Build() => new WorkflowBuilder<Data, object>([], "WaitRequired")
                .{|#0:WaitFor<Answer>|}("review", configure: wait => wait.Input(answer => answer.Value)
                    .From(ctx => ctx.WorkflowData.Value)).Build();
        }
        """ + Types, Diagnostic(Descriptors.RequiredInputNotMapped, 0, "Event", "Answer.Customer"));
}
