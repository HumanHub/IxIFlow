using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.AuthoringTests;

/// <summary>
/// Mappings follow the activity's contract when the workflow is built (D-26, spec F-22): inputs go into inputs and
/// outputs come from outputs, a marked activity's required inputs are mapped or defaulted, and a child's data and an
/// event follow the same visibility rules. Each violation names the type and the property.
/// </summary>
[Trait("Feature", "authoring")]
[Trait("Spec", "F-22")]
public sealed class MappingValidationTests
{
    [Fact]
    public void AnInputIntoAPropertyWithoutAPublicSetterFailsTheBuild()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("PrivateSetter")
            .Step<Plain>(step => step.Input(activity => activity.Computed).From(ctx => ctx.WorkflowData.Number))
            .Build());

        Assert.Contains("Activity property 'Plain.Computed' is not an input: it has no public setter", error.Message);
    }

    [Fact]
    public void AnOutputFromAPropertyWithoutAPublicGetterFailsTheBuild()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("PrivateGetter")
            .Step<Plain>(step => step.Output(activity => activity.Hidden).To(ctx => ctx.WorkflowData.Number))
            .Build());

        Assert.Contains("Activity property 'Plain.Hidden' is not an output: it has no public getter", error.Message);
    }

    [Fact]
    public void AMarkedActivityTakesInputsOnlyIntoItsMarkedInputs()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("UnmarkedInput")
            .Step<Marked>(step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number)
                .Input(activity => activity.Result).From(ctx => ctx.WorkflowData.Number))
            .Build());

        Assert.Contains("Activity property 'Marked.Result' is not an input: it has no [WorkflowInput]", error.Message);
    }

    [Fact]
    public void AMarkedActivityGivesOutputsOnlyFromItsMarkedOutputs()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("UnmarkedOutput")
            .Step<Marked>(step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number)
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Number))
            .Build());

        Assert.Contains("Activity property 'Marked.Amount' is not an output: it has no [WorkflowOutput]", error.Message);
    }

    [Fact]
    public void ARequiredInputIsMappedOrHasADefault()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("Unmapped")
            .Step<Marked>(step => step.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Number))
            .Build());
        Assert.Contains("Activity property 'Marked.Amount' is a required input and is not mapped", error.Message);

        // Mapped, it builds; an optional input and a required one with a default may stay unmapped.
        Workflow.Create<Data>("Mapped")
            .Step<Marked>(step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Number))
            .Build();
    }

    [Fact]
    public void ACompensationActivityFollowsTheSameRules()
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("Compensation")
            .Step<Plain>()
            .Saga(saga => saga.Step<Plain>(step => step.CompensateWith<Plain>(compensation => compensation
                .Input(activity => activity.Computed).From(ctx => ctx.WorkflowData.Number))))
            .Build());

        Assert.Contains("Activity property 'Plain.Computed' is not an input: it has no public setter", error.Message);
    }

    [Fact]
    public void AChildsDataTakesInputsIntoPublicSettersAndGivesOutputsFromPublicGetters()
    {
        var input = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("ChildInput")
            .Step<Plain>()
            .Invoke<ChildData>("Child", 1, step => step.Input(child => child.Fixed).From(ctx => ctx.WorkflowData.Number))
            .Build());
        Assert.Contains("Child workflow property 'ChildData.Fixed' is not an input: it has no public setter", input.Message);

        var output = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<Data>("ChildOutput")
            .Step<Plain>()
            .Invoke<ChildData>("Child", 1, step => step.Output(child => child.Secret).To(ctx => ctx.WorkflowData.Number))
            .Build());
        Assert.Contains("Child workflow property 'ChildData.Secret' is not an output: it has no public getter", output.Message);
    }

    // A definition made without the fluent builder is checked by the same rules through the public call.
    [Fact]
    public void AHandBuiltDefinitionIsValidatedByTheSameRules()
    {
        var handBuilt = new WorkflowDefinition
        {
            Name = "HandBuilt",
            Version = 1,
            WorkflowDataType = typeof(Data),
            Steps =
            [
                new WorkflowStep
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = nameof(Plain),
                    StepType = WorkflowStepType.Activity,
                    ActivityType = typeof(Plain),
                    WorkflowDataType = typeof(Data),
                    InputMappings =
                    [
                        new PropertyMapping
                        {
                            TargetProperty = nameof(Plain.Computed),
                            Direction = PropertyMappingDirection.Input,
                            SourceFunction = _ => 1
                        }
                    ]
                }
            ]
        };

        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition,
            () => WorkflowDefinitionValidator.Validate(handBuilt));

        Assert.Contains("Activity property 'Plain.Computed' is not an input: it has no public setter", error.Message);
        WorkflowDefinitionValidator.Validate(Workflow.Create<Data>("Valid")
            .Step<Marked>(step => step.Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number))
            .Build());
    }

    public sealed class Data
    {
        public int Number { get; set; }
    }

    public sealed class ChildData
    {
        public int Fixed { get; private set; }
        public int Secret { internal get; set; }
    }

    /// <summary>No attributes: a public setter makes an input, a public getter an output.</summary>
    public sealed class Plain : IAsyncActivity
    {
        public int Computed { get; private set; }
        public int Hidden { internal get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Marked: only marked properties are inputs or outputs.</summary>
    public sealed class Marked : IAsyncActivity
    {
        [WorkflowInput(Required = true)]
        public int Amount { get; set; }

        [WorkflowInput(Required = true, Default = "1")]
        public int Factor { get; set; } = 1;

        [WorkflowInput]
        public int Optional { get; set; }

        [WorkflowOutput]
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
