using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Tests.SyntaxTests.Models;

[IxIFlow.Core.Workflow("ValidationWorkflow", Version = 1)]
public class ValidationWorkflow : IWorkflow<ValidationWorkflowData>
{

    public void Build(IWorkflowBuilder<ValidationWorkflowData> builder)
    {
        // Placeholder implementation for testing workflow invocation syntax
        builder.Step<ValidateOrderAsyncActivity>(setup => setup
            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.WorkflowId)
            .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
            .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors));
    }
}