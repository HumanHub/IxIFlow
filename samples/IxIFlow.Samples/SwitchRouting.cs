using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class RoutingData
{
    public string Route { get; set; } = "ship";
    public string Result { get; set; } = "";
}

/// <summary>Selects a routing body with the same definition in fluent C# and YAML.</summary>
public static class SwitchRouting
{
    // snippet: switch-routing
    public static WorkflowDefinition Definition() => Workflow.Create<RoutingData>("SwitchRouting")
        .Switch(ctx => ctx.WorkflowData.Route, cases => cases
            .Case("ship", body => body.Step<Label>(s => s.Input(a => a.Value).From(ctx => "shipping")
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)))
            .Case("hold", body => body.Step<Label>(s => s.Input(a => a.Value).From(ctx => "on hold")
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)))
            .Default(body => body.Step<Label>(s => s.Input(a => a.Value).From(ctx => "manual review")
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result))))
        .WithId("route").Build();

    // end-snippet: switch-routing
}