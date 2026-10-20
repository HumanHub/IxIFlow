using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Deployment placement added to a workflow's definition at registration.</summary>
public sealed class WorkflowRegistration
{
    internal List<WorkflowDemand> Demands { get; } = [];
    public WorkflowRegistration Demand(string key, string? value = null, bool inherit = false)
    {
        var demand = new WorkflowDemand(key, value, Inherit: inherit);
        WorkflowDemands.Validate(demand);
        Demands.Add(demand);
        return this;
    }
}
