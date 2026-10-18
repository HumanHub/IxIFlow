using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators.Tests;

public sealed class DocumentSerializationTests
{
    [Fact]
    public void JsonAndYamlSharePolymorphismAndSerializerIndependentValues()
    {
        var yaml = """
            workflow: {name: Serialization, dataType: Data}
            definitions:
              - kind: sequence
                steps:
                  - kind: activity
                    activity: Activity
                    defaults: {Text: "true", Flag: true, Count: 2147483648, Ratio: 1.5, Nothing: null, Items: [1, two], Map: {Key: value, Another: other}}
                    input:
                      - target: Value
                        from: {kind: binary, operator: add, left: {kind: constant, value: 2}, right: {path: workflow.Value}}
              - {kind: activityRef, ref: activity}
              - {kind: stepRef, ref: block}
              - {kind: if, condition: {kind: constant, value: true}}
              - {kind: forEach, items: {path: workflow.Values}}
              - {kind: suspend, event: Event, resumeCondition: {kind: function, function: Done}}
              - {kind: invokeWorkflow, workflow: {name: Child, version: 1}}
            """;
        var document = WorkflowYamlSerializer.Read(yaml);
        var json = WorkflowJsonSerializer.Write(document);
        Assert.DoesNotContain("location", json);
        Assert.DoesNotContain("members", json);
        var result = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(json)));
        Assert.Collection(result.Definitions,
            step => Assert.IsType<SequenceStepDocument>(step), step => Assert.IsType<ActivityReferenceStepDocument>(step),
            step => Assert.IsType<StepReferenceStepDocument>(step), step => Assert.IsType<ConditionalStepDocument>(step),
            step => Assert.IsType<ForEachStepDocument>(step), step => Assert.IsType<SuspendStepDocument>(step),
            step => Assert.IsType<InvokeWorkflowStepDocument>(step));
        var activity = Assert.IsType<ActivityStepDocument>(Assert.Single(Assert.IsType<SequenceStepDocument>(result.Definitions[0]).Steps));
        Assert.Equal("true", Assert.IsType<string>(activity.Defaults["Text"]));
        Assert.True(Assert.IsType<bool>(activity.Defaults["Flag"]));
        Assert.Equal(2147483648L, Assert.IsType<long>(activity.Defaults["Count"]));
        Assert.Equal(1.5, Assert.IsType<double>(activity.Defaults["Ratio"]));
        Assert.Null(activity.Defaults["Nothing"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(activity.Defaults["Items"]).Count);
        Assert.Equal(2, Assert.IsType<Dictionary<string, object?>>(activity.Defaults["Map"]).Count);
        var expression = Assert.IsType<BinaryExpressionDocument>(Assert.Single(activity.Input).From);
        Assert.IsType<ConstantExpressionDocument>(expression.Left);
        Assert.IsType<PathExpressionDocument>(expression.Right);
    }
}
