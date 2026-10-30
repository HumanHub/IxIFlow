using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryMappingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FaultContextPolicyChainsAcceptCompleteMappings(bool switchCase) =>
        Analyzer<MappingAnalyzer>(Source(switchCase, "Invoke<Child>", """
                                                                      .Input(data => data.First).OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                          .OnError(error => error.ThenIgnore()).From(_ => 1)
                                                                      .Input(data => data.Second).OnError(error => error.ThenIgnore()).From(_ => 2)
                                                                      .Output(data => data.Result).OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                          .OnError(error => error.ThenIgnore()).To(ctx => ctx.WorkflowData.Value)
                                                                      """));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FaultContextPolicyChainsReportMissingRequiredInput(bool switchCase) =>
        Analyzer<MappingAnalyzer>(Source(switchCase, "{|#0:Invoke<Child>|}", """
                                                                             .Input(data => data.First).OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                                 .OnError(error => error.ThenIgnore()).From(_ => 1)
                                                                             """),
            Diagnostic(Descriptors.WorkflowRequiredInputNotMapped, 0, "Child.Second"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FaultContextPolicyChainsCheckInputAndOutputMembers(bool switchCase) =>
        Analyzer<MappingAnalyzer>(Source(switchCase, "Invoke<Child>", """
                                                                      .Input(data => data.First).OnError(error => error.ThenIgnore()).From(_ => 1)
                                                                      .Input(data => data.Second).OnError(error => error.ThenIgnore()).From(_ => 2)
                                                                      .Input(data => data.{|#0:Result|}).OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                          .OnError(error => error.ThenIgnore()).From(_ => 3)
                                                                      .Output(data => data.{|#1:First|}).OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                          .OnError(error => error.ThenIgnore()).To(ctx => ctx.WorkflowData.Value)
                                                                      """), Diagnostic(Descriptors.WorkflowNotAnInput, 0, "Child.Result"),
            Diagnostic(Descriptors.WorkflowNotAnOutput, 1, "Child.First"));

    private static string Source(bool switchCase, string invoke, string mappings)
    {
        var invocation = $"body.{invoke}(\"Child\", 1, setup => setup {mappings})";

        var handler = switchCase
            ? $"handler.Switch(_ => 1, cases => cases.Case(1, body => {invocation}))"
            : invocation.Replace("body.", "handler.");

        return $$"""
                 using System.ComponentModel.DataAnnotations;
                 public sealed class Data { public int Value { get; set; } }
                 public sealed class Child
                 {
                     [WorkflowInput, Required] public int First { get; set; }
                     [WorkflowInput, Required] public int Second { get; set; }
                     [WorkflowOutput] public int Result { get; set; }
                 }
                 public sealed class Noop : IAsyncActivity
                 {
                     public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
                 }
                 public static class Flow
                 {
                     public static object Build() => Workflow.Create<Data>("FaultPolicyMappings").Step<Noop>()
                         .Try(body => body.Step<Noop>()).Catch<Exception>(handler => {{handler}}).Build();
                 }
                 """;
    }
}