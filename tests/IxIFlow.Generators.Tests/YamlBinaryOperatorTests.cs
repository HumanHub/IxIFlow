using System.Linq.Expressions;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Generators.Tests;

public sealed partial class YamlParityTests
{
    public static IEnumerable<object[]> BinaryCases()
    {
        foreach (var op in new[] { "eq", "ne", "gt", "ge", "lt", "le" })
        foreach (var (left, right) in new[] { (2, 2), (2, 3), (3, 2) })
            yield return [op, left, right, op switch
            { "eq" => left == right ? 1 : 0, "ne" => left != right ? 1 : 0, "gt" => left > right ? 1 : 0,
                "ge" => left >= right ? 1 : 0, "lt" => left < right ? 1 : 0, _ => left <= right ? 1 : 0 }];
        foreach (var op in new[] { "and", "or" })
        foreach (var left in new[] { 0, 1 })
        foreach (var right in new[] { 0, 1 })
            yield return [op, left, right, op == "and" ? left & right : left | right];
        foreach (var op in new[] { "add", "subtract", "multiply", "divide" })
        foreach (var (left, right) in new[] { (7, 2), (-7, 2), (0, 3) })
            yield return [op, left, right, op switch
            { "add" => left + right, "subtract" => left - right, "multiply" => left * right, _ => left / right }];
    }

    [Theory, MemberData(nameof(BinaryCases)), Trait("Feature", "authoring"), Trait("Spec", "F-06"), Trait("Spec", "F-25")]
    public async Task EveryBinaryOperatorExecutesDistinguishingOperandsWithTheFluentFingerprint(string op, int left, int right, int expected)
    {
        var arithmetic = op is "add" or "subtract" or "multiply" or "divide";
        var boolean = op is "and" or "or";
        var expression = "{kind: binary, operator: " + op + ", left: {path: workflow." + (boolean ? "Selected" : "Value") +
            "}, right: {path: workflow." + (boolean ? "Answered" : "Second") + "}}";
        WorkflowDefinition fluent;
        WorkflowDefinition generated;
        if (arithmetic)
        {
            Expression<Func<WorkflowContext<ParityData>, int>> value = op switch
            {
                "add" => ctx => ctx.WorkflowData.Value + ctx.WorkflowData.Second,
                "subtract" => ctx => ctx.WorkflowData.Value - ctx.WorkflowData.Second,
                "multiply" => ctx => ctx.WorkflowData.Value * ctx.WorkflowData.Second,
                _ => ctx => ctx.WorkflowData.Value / ctx.WorkflowData.Second
            };
            fluent = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(s => s.Input(a => a.Value).From(value)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("operator").Build();
            generated = Build("- kind: activity\n  id: operator\n  activity: ParityEcho\n  input: [{target: Value, from: " + expression +
                "}]\n  output: [{source: Value, to: {path: workflow.Result}}]");
        }
        else
        {
            Expression<Func<WorkflowContext<ParityData, object>, bool>> condition = op switch
            {
                "eq" => ctx => ctx.WorkflowData.Value == ctx.WorkflowData.Second,
                "ne" => ctx => ctx.WorkflowData.Value != ctx.WorkflowData.Second,
                "gt" => ctx => ctx.WorkflowData.Value > ctx.WorkflowData.Second,
                "ge" => ctx => ctx.WorkflowData.Value >= ctx.WorkflowData.Second,
                "lt" => ctx => ctx.WorkflowData.Value < ctx.WorkflowData.Second,
                "le" => ctx => ctx.WorkflowData.Value <= ctx.WorkflowData.Second,
                "and" => ctx => ctx.WorkflowData.Selected && ctx.WorkflowData.Answered,
                _ => ctx => ctx.WorkflowData.Selected || ctx.WorkflowData.Answered
            };
            fluent = Start().If(condition,
                then => then.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 1)
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("yes"),
                otherwise => otherwise.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 0)
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("no"))
                .WithId("operator").Build();
            generated = Build("- kind: if\n  id: operator\n  condition: " + expression + "\n" + """
                  then:
                    - kind: activity
                      id: yes
                      activity: ParityEcho
                      input: [{target: Value, from: {kind: constant, value: 1}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                  else:
                    - kind: activity
                      id: no
                      activity: ParityEcho
                      input: [{target: Value, from: {kind: constant, value: 0}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                """);
        }
        ParityData Data() => new() { Value = left, Second = right, Selected = left != 0, Answered = right != 0, Result = -999 };
        var first = await Execute(fluent, null, Data);
        var second = await Execute(generated, null, Data);
        Assert.Equal(expected, first.Data.Result);
        Assert.Equal(expected, second.Data.Result);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(Shape(fluent), Shape(generated));
    }
}
