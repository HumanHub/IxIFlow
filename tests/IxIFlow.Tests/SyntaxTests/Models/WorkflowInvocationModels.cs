using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Tests.SyntaxTests.Models;

public class ValidationWorkflow : IWorkflow<ValidationWorkflowData>
{
    public int Version { get; } = 1;

    public void Build(IWorkflowBuilder<ValidationWorkflowData> builder)
    {
        // Placeholder implementation for testing workflow invocation syntax
        builder.Step<ValidateOrderAsyncActivity>(setup => setup
            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.WorkflowId)
            .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
            .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors));
    }
}