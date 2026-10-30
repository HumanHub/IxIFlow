using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-27")]
public sealed class Slice4StructureHashTests
{
    [Fact]
    public void ChangingAnIntegerLiteralChangesTheHash() => Different(
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Number).From(_ => 1)).Build(),
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Number).From(_ => 2)).Build());

    [Fact]
    public void ChangingAStringLiteralChangesTheHash() => Different(
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Text).From(_ => "first")).Build(),
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Text).From(_ => "second")).Build());

    [Fact]
    public void ChangingAnEnumLiteralChangesTheHash() => Different(
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Day).From(_ => DayOfWeek.Monday)).Build(),
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Day).From(_ => DayOfWeek.Tuesday)).Build());

    [Fact]
    public void ChangingATypeLiteralChangesTheHash() => Different(
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Type).From(_ => typeof(int))).Build(),
        Workflow.Create<Data>("Literals").Step<Copy>(s => s.Input(a => a.Type).From(_ => typeof(string))).Build());

    private static void Different(WorkflowDefinition first, WorkflowDefinition second) =>
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(second));

    public sealed class Data;

    public sealed class Copy : IAsyncActivity
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
        public DayOfWeek Day { get; set; }
        public Type Type { get; set; } = typeof(object);
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}