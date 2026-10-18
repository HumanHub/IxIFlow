using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Tests.ExecutionTests.Models;

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

        var validator = new WorkflowDocumentValidator();
        var result = await validator.ValidateAsync(document, CreateContext());

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

        var definition = await YamlTestCompilation.BuildAsync(document, CreateContext());

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
                    Kind = "sequenceTemplate",
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

        var definition = await YamlTestCompilation.BuildAsync(document, CreateContext());

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

        var validator = new WorkflowDocumentValidator();
        var result = await validator.ValidateAsync(document, CreateContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == "EXPR001" && d.Path == "definitions[0].input[0].from");
        Assert.Contains(result.Diagnostics, d => d.Code == "MAP003" && d.Path == "definitions[0].output[0].to");
        Assert.Contains(result.Diagnostics, d => d.Code == "EXPR001" && d.Path == "definitions[1].resumeCondition");
    }

    private static WorkflowValidationContext CreateContext()
    {
        return new WorkflowValidationContext(
            new InMemoryActivityRegistry(),
            new InMemoryWorkflowCatalog(),
            new InMemoryEventRegistry(),
            new InMemoryDataTypeRegistry(),
            new InMemoryStepTemplateRegistry());
    }

    private sealed class InMemoryActivityRegistry : IActivityRegistry
    {
        public ValueTask<ActivityDescriptor?> FindAsync(string activityKey, CancellationToken cancellationToken = default)
        {
            var descriptor = activityKey switch
            {
                nameof(ValidateOrderAsyncActivity) => new ActivityDescriptor { Key = activityKey, ActivityType = typeof(ValidateOrderAsyncActivity) },
                nameof(ProcessOrderAsyncActivity) => new ActivityDescriptor { Key = activityKey, ActivityType = typeof(ProcessOrderAsyncActivity) },
                _ => null
            };

            return ValueTask.FromResult<ActivityDescriptor?>(descriptor);
        }
    }

    private sealed class InMemoryWorkflowCatalog : IWorkflowCatalog
    {
        public ValueTask<WorkflowDescriptor?> FindAsync(string workflowName, int? version, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<WorkflowDescriptor?>(new WorkflowDescriptor
            {
                Name = workflowName,
                Version = version ?? 1,
                RuntimeDefinition = new WorkflowDefinition
                {
                    Name = workflowName,
                    Version = version ?? 1,
                    WorkflowDataType = typeof(SuspendResumeTestData)
                }
            });
        }
    }

    private sealed class InMemoryEventRegistry : IEventRegistry
    {
        public ValueTask<EventDescriptor?> FindAsync(string eventKey, CancellationToken cancellationToken = default)
        {
            var descriptor = eventKey switch
            {
                nameof(ApprovalEvent) => new EventDescriptor { Key = eventKey, EventType = typeof(ApprovalEvent) },
                _ => null
            };

            return ValueTask.FromResult<EventDescriptor?>(descriptor);
        }
    }

    private sealed class InMemoryDataTypeRegistry : IDataTypeRegistry
    {
        public ValueTask<DataTypeDescriptor?> FindAsync(string typeKey, CancellationToken cancellationToken = default)
        {
            var descriptor = typeKey switch
            {
                nameof(OrderWorkflowData) => new DataTypeDescriptor { Key = typeKey, ClrType = typeof(OrderWorkflowData) },
                nameof(SuspendResumeTestData) => new DataTypeDescriptor { Key = typeKey, ClrType = typeof(SuspendResumeTestData) },
                _ => null
            };

            return ValueTask.FromResult<DataTypeDescriptor?>(descriptor);
        }
    }

    private sealed class InMemoryStepTemplateRegistry : IStepTemplateRegistry
    {
        public ValueTask<StepTemplateDescriptor?> FindAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<StepTemplateDescriptor?>(null);
        }
    }
}
