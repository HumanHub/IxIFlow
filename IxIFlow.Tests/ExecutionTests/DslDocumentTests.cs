using System.Text.Json;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Tests.ExecutionTests;

public class DslDocumentTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    public void WorkflowDocument_Should_Serialize_And_Deserialize_Polymorphic_Steps()
    {
        var document = new WorkflowDocument
        {
            Workflow = new WorkflowDefinitionDocument
            {
                Name = "OrderProcessing",
                Version = 1,
                DataType = "Samples.OrderWorkflowData"
            },
            Definitions =
            [
                new ActivityStepDocument
                {
                    Id = "validate-order",
                    Kind = "activity",
                    Activity = "ValidateOrder",
                    Input =
                    [
                        new InputMappingDocument
                        {
                            Target = "OrderId",
                            From = new PathExpressionDocument { Path = "workflow.orderId" }
                        }
                    ]
                },
                new SuspendStepDocument
                {
                    Id = "await-approval",
                    Kind = "suspend",
                    Event = "ApprovalReceived",
                    Reason = "Waiting for manager approval",
                    ResumeCondition = new BinaryExpressionDocument
                    {
                        Operator = "eq",
                        Left = new PathExpressionDocument { Path = "event.orderId" },
                        Right = new PathExpressionDocument { Path = "workflow.orderId" }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkflowDocument>(json, SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal("OrderProcessing", roundTripped.Workflow.Name);
        Assert.Collection(roundTripped.Definitions,
            step => Assert.IsType<ActivityStepDocument>(step),
            step => Assert.IsType<SuspendStepDocument>(step));
    }

    [Fact]
    public void WorkflowDocument_Should_Preserve_Reusable_Assets()
    {
        var document = new WorkflowDocument
        {
            Activities = new Dictionary<string, ActivityTemplateDocument>
            {
                ["SendOrderEmail"] = new()
                {
                    Activity = "SendEmail",
                    Input =
                    [
                        new InputMappingDocument
                        {
                            Target = "To",
                            From = new PathExpressionDocument { Path = "workflow.customerEmail" }
                        }
                    ]
                }
            },
            Steps = new Dictionary<string, StepTemplateDocument>
            {
                ["AuditAndNotify"] = new()
                {
                    Kind = "sequenceTemplate",
                    Parameters = new Dictionary<string, TemplateParameterDocument>
                    {
                        ["message"] = new() { Type = "string", Required = true }
                    }
                }
            },
            Code = new Dictionary<string, CodeAssetDocument>
            {
                ["taxFormula"] = new()
                {
                    Language = "csharp-expression",
                    Returns = "decimal",
                    Source = "workflow.amount * 0.20m"
                }
            }
        };

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkflowDocument>(json, SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.Activities.ContainsKey("SendOrderEmail"));
        Assert.True(roundTripped.Steps.ContainsKey("AuditAndNotify"));
        Assert.True(roundTripped.Code.ContainsKey("taxFormula"));
        Assert.Equal("csharp-expression", roundTripped.Code["taxFormula"].Language);
    }
}
