using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Tests.ExecutionTests.Models;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "documents")]
public class DslCompilationTests
{
    [Fact]
    public async Task WorkflowDocumentValidator_Should_Fail_For_Unknown_Activity()
    {
        var document = new WorkflowDocument
        {
            Workflow = new WorkflowDefinitionDocument
            {
                Name = "OrderProcessing",
                Version = 1,
                DataType = nameof(OrderWorkflowData)
            },
            Definitions =
            [
                new ActivityStepDocument
                {
                    Id = "validate-order",
                    Activity = "MissingActivity"
                }
            ]
        };

        var result = await YamlTestContracts.ValidateAsync(document, CreateOptions());

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == "STEP003");
    }

    [Fact]
    public async Task GeneratedYaml_PreservesBasicActivitySteps()
    {
        var document = new WorkflowDocument
        {
            Workflow = new WorkflowDefinitionDocument
            {
                Name = "OrderProcessing",
                Version = 1,
                DataType = nameof(OrderWorkflowData)
            },
            Definitions =
            [
                new ActivityStepDocument
                {
                    Id = "validate-order",
                    Name = "Validate Order",
                    Activity = nameof(ValidateOrderAsyncActivity)
                }
            ]
        };

        var definition = await YamlTestCompilation.BuildAsync(document, CreateOptions());

        Assert.Equal("OrderProcessing", definition.Name);
        Assert.Single(definition.Steps);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);
    }

    [Fact]
    public async Task GeneratedYaml_PreservesActivityReferencesSequencesWaitsAndChildInvocations()
    {
        var document = new WorkflowDocument
        {
            Workflow = new WorkflowDefinitionDocument
            {
                Name = "OrderProcessingExtended",
                Version = 1,
                DataType = nameof(SuspendResumeTestData)
            },
            Activities = new Dictionary<string, ActivityTemplateDocument>
            {
                ["validate-template"] = new()
                {
                    Activity = nameof(ValidateOrderAsyncActivity),
                    Input =
                    [
                        new InputMappingDocument
                        {
                            Target = "OrderId",
                            From = new PathExpressionDocument { Path = "workflow.orderId" }
                        }
                    ]
                }
            },
            Steps = new Dictionary<string, StepTemplateDocument>
            {
                ["processing-sequence"] = new()
                {
                    Steps =
                    [
                        new ActivityStepDocument
                        {
                            Id = "process-order",
                            Activity = nameof(ProcessOrderAsyncActivity)
                        }
                    ]
                }
            },
            Definitions =
            [
                new ActivityReferenceStepDocument
                {
                    Id = "validate-order",
                    Ref = "validate-template"
                },
                new SequenceStepDocument
                {
                    Id = "main-sequence",
                    Steps =
                    [
                        new StepReferenceStepDocument
                        {
                            Id = "process-order-ref",
                            Ref = "processing-sequence"
                        },
                        new SuspendStepDocument
                        {
                            Id = "await-approval",
                            Event = nameof(ApprovalEvent),
                            Reason = "Waiting for approval"
                        },
                        new InvokeWorkflowStepDocument
                        {
                            Id = "invoke-child",
                            Workflow = new WorkflowReferenceDocument
                            {
                                Name = "ChildWorkflow",
                                Version = 3
                            }
                        }
                    ]
                }
            ]
        };

        var definition = await YamlTestCompilation.BuildAsync(document, CreateOptions());

        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);
        var sequence = definition.Steps[1];
        Assert.Equal(WorkflowStepType.Sequence, sequence.StepType);
        Assert.Equal(typeof(ProcessOrderAsyncActivity), Assert.Single(sequence.SequenceSteps[0].SequenceSteps).ActivityType);
        Assert.Equal(WorkflowStepType.SuspendResume, sequence.SequenceSteps[1].StepType);
        Assert.Equal(typeof(ApprovalEvent), sequence.SequenceSteps[1].ResumeEventType);
        Assert.Equal("Waiting for approval", sequence.SequenceSteps[1].StepMetadata["SuspendReason"]);
        Assert.Equal(WorkflowStepType.WorkflowInvocation, sequence.SequenceSteps[2].StepType);
        Assert.Equal("ChildWorkflow", sequence.SequenceSteps[2].WorkflowName);
        Assert.Equal(3, sequence.SequenceSteps[2].WorkflowVersion);
        Assert.Equal([0, 1, 2], sequence.SequenceSteps.Select(step => step.Order).ToArray());
    }

    [Fact]
    public async Task WorkflowDocumentValidator_Should_Fail_For_Invalid_Path_Roots()
    {
        var document = new WorkflowDocument
        {
            Workflow = new WorkflowDefinitionDocument
            {
                Name = "InvalidPaths",
                Version = 1,
                DataType = nameof(SuspendResumeTestData)
            },
            Definitions =
            [
                new ActivityStepDocument
                {
                    Id = "validate-order",
                    Activity = nameof(ValidateOrderAsyncActivity),
                    Input =
                    [
                        new InputMappingDocument
                        {
                            Target = "OrderId",
                            From = new PathExpressionDocument { Path = "activity.orderId" }
                        }
                    ],
                    Output =
                    [
                        new OutputMappingDocument
                        {
                            Source = "IsValid",
                            To = new PathExpressionDocument { Path = "previousStep.isValid" }
                        }
                    ]
                },
                new SuspendStepDocument
                {
                    Id = "await-approval",
                    Event = nameof(ApprovalEvent),
                    Reason = "Waiting",
                    ResumeCondition = new PathExpressionDocument { Path = "previousStep.isApproved" }
                }
            ]
        };

        var result = await YamlTestContracts.ValidateAsync(document, CreateOptions());

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == "EXPR001" && d.Path == "definitions[0].input[0].from");
        Assert.Contains(result.Diagnostics, d => d.Code == "MAP003" && d.Path == "definitions[0].output[0].to");
        Assert.Contains(result.Diagnostics, d => d.Code == "EXPR001" && d.Path == "definitions[1].resumeCondition");
    }

    private static WorkflowSourceOptions CreateOptions()
    {
        var options = YamlTestContracts.Create(
            (nameof(OrderWorkflowData), typeof(OrderWorkflowData)),
            (nameof(SuspendResumeTestData), typeof(SuspendResumeTestData)),
            (nameof(ValidateOrderAsyncActivity), typeof(ValidateOrderAsyncActivity)),
            (nameof(ProcessOrderAsyncActivity), typeof(ProcessOrderAsyncActivity)),
            (nameof(ApprovalEvent), typeof(ApprovalEvent)));
        options.Workflows["ChildWorkflow@3"] = new(YamlTestContracts.Name(typeof(SuspendResumeTestData)), 3);
        return options;
    }
}
