using IxIFlow.Core.Runtime;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Generators.Tests.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-06")]
public sealed class GeneratedWorkflowTests
{
    [Fact]
    public async Task GeneratedActivitiesSequencesAndConditionsMatchFluentDefinitionAndResults()
    {
        var fluent = Workflow.Create<FlowData>("Arithmetic")
            .Step<Multiply>(setup => setup.Input(act => act.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result))
            .WithId("multiply")
            .Sequence(sequence => sequence.Step<Multiply>(setup => setup
                .Input(act => act.Value).From(ctx => ctx.PreviousStep.Result)
                .Input(act => act.Factor).From(ctx => 3)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result)).WithId("triple"))
            .WithId("nested")
            .If(ctx => ctx.WorkflowData.Result > 10, then => then.Step<Multiply>(setup => setup
                .Input(act => act.Value).From(ctx => ctx.WorkflowData.Result)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result)).WithId("final"))
            .WithId("conditional").Build();
        var generated = Workflow_arithmetic.Build();
        Assert.Equal(Shape(fluent), Shape(generated));
        var first = await Execute(fluent, new FlowData { Value = 4 });
        var second = await Execute(generated, new FlowData { Value = 4 });
        Assert.Equal(48, second.Data.Result);
        Assert.Equal(first.Data.Result, second.Data.Result);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task GeneratedWaitAndInvocationMatchFluentDefinitionFingerprintAndResults()
    {
        var fluent = new WorkflowBuilder<FlowData, object>([], "Waiting")
            .Suspend<Answer>("answer", configure: setup => setup
                .Output(act => act.Value).To(ctx => ctx.WorkflowData.Value))
            .WithId("answer")
            .Invoke<FlowData>("Child", 3, setup => setup
                .Input(act => act.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Result))
            .WithId("child").Build();
        var generated = Workflow_waiting.Build();
        Assert.Equal(Shape(fluent), Shape(generated));
        var first = await Execute(fluent, new FlowData(), resume: true);
        var second = await Execute(generated, new FlowData(), resume: true);
        Assert.Equal(14, second.Data.Result);
        Assert.Equal(first.Data.Result, second.Data.Result);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    [Trait("Spec", "F-05")]
    public void GeneratedStepIdsAndSourceAreStable()
    {
        var first = Workflow_arithmetic.Build();
        var second = Workflow_arithmetic.Build();
        Assert.Equal(Shape(first), Shape(second));
        Assert.All(first.Steps, step => Assert.Contains("arithmetic.yaml:", step.SourceLocation));
        Assert.Equal("triple", first.Steps[1].SequenceSteps[0].Id);
    }

    [Fact]
    [Trait("Spec", "F-08")]
    public async Task TypedDispatchUsesDeclaredDataTypeForDerivedRuntimeData()
    {
        var result = await Execute(Workflow_arithmetic.Build(), new DerivedFlowData { Value = 4 });
        Assert.Equal(48, result.Data.Result);
    }

    [Fact]
    [Trait("Spec", "F-08")]
    public async Task ClonedStepsKeepTheirCapturedTypedMappings()
    {
        var definition = Workflow_arithmetic.Build();
        var step = definition.Steps[0].Clone();
        var data = new FlowData { Value = 5 };
        await using var services = Services();
        var prepared = await services.GetRequiredService<IActivityExecutor>()
            .PrepareTypedActivityAsync(new TypedActivityExecutionRequest<FlowData, object>
            {
                ActivityType = typeof(Multiply), Step = step, WorkflowData = data,
                InputMappings = step.InputMappings, OutputMappings = step.OutputMappings,
                ExecutionContext = new StepExecutionContext<FlowData>
                {
                    WorkflowData = data, WorkflowDefinition = definition,
                    WorkflowInstance = new WorkflowInstance { InstanceId = "typed-clone" }
                }
            }, true, "invocation", "attempt");
        Assert.Equal(5, Assert.IsType<Multiply>(prepared.Activity).Value);
    }

    internal static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    internal static async Task<(FlowData Data, string Fingerprint)> Execute(WorkflowDefinition definition,
        FlowData data, bool resume = false)
    {
        await using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Workflow_child.Build());
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, data);
        if (resume)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            result = await client.ResumeAsync(result.InstanceId, "answer", new Answer { Value = 7 });
        }
        if (result.Status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended)
            result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId);
        using var checkpoint = JsonDocument.Parse(saved!.ExecutionStateJson);
        return (Assert.IsAssignableFrom<FlowData>(result.WorkflowData), checkpoint.RootElement.GetProperty("DefinitionFingerprint").GetString()!);
    }

    internal static string Shape(WorkflowDefinition definition) => JsonSerializer.Serialize(new
    {
        definition.Name, definition.Version, definition.Description, definition.Tags,
        Steps = definition.Steps.Select(Shape)
    });

    private static object Shape(WorkflowStep step) => new
    {
        step.Id, step.Name, step.StepType, step.Order,
        Data = step.WorkflowDataType?.FullName, Previous = step.PreviousStepDataType?.FullName,
        Activity = step.ActivityType?.FullName, Event = step.ResumeEventType?.FullName,
        step.WorkflowName, step.WorkflowVersion, Invoked = step.InvokedDataType?.FullName,
        Inputs = step.InputMappings.Select(mapping => new { mapping.TargetProperty, Expression = mapping.Expression == null ? null : WorkflowExpressionSignature.Of(mapping.Expression) }),
        Outputs = step.OutputMappings.Select(mapping => new { mapping.TargetProperty, Expression = mapping.Expression == null ? null : WorkflowExpressionSignature.Of(mapping.Expression) }),
        WaitKey = step.StepMetadata.GetValueOrDefault("WaitKey"), step.WaitTimeout, step.DelayDuration,
        step.ParallelJoinMode, step.ForEachParallel, step.ForEachMaxConcurrency,
        Result = step.ResultType?.FullName, step.ForEachIntoPath,
        ForEach = step.ForEachBodySteps.Select(Shape), Timeout = step.TimeoutSteps.Select(Shape),
        Parallel = step.ParallelBranches.Select(branch => branch.Select(Shape)),
        Sequence = step.SequenceSteps.Select(Shape), Then = step.ThenSteps.Select(Shape), Else = step.ElseSteps.Select(Shape)
    };
}
