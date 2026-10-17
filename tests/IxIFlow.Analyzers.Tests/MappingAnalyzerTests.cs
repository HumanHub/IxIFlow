using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

/// <summary>
/// The contract analyzer reports at compile time what the engine refuses when a fluent workflow is built (D-26, spec
/// F-22, F-24): an input into a property that is not an input, an output from one that is not an output, and a
/// required input that is not mapped, with the build's messages.
/// </summary>
[Trait("Feature", "authoring")]
[Trait("Spec", "F-24")]
public sealed class MappingAnalyzerTests
{
    private const string Types = """

        public sealed class Data
        {
            public int Number { get; set; }
        }

        public sealed class ChildData
        {
            public int Fixed { get; private set; }
            public int Secret { internal get; set; }
            public int Open { get; set; }
        }

        public sealed class Go
        {
            public int Hidden { internal get; set; }
        }

        public sealed class Plain : IAsyncActivity
        {
            public int Computed { get; private set; }
            public int Hidden { internal get; set; }
            public int Open { get; set; }
            public int Field;

            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        public class Marked : IAsyncActivity
        {
            [WorkflowInput(Required = true)]
            public virtual int Amount { get; set; }

            [WorkflowInput(Required = true, Default = "1")]
            public int Factor { get; set; } = 1;

            [WorkflowInput]
            public int Optional { get; set; }

            [WorkflowOutput]
            public int Result { get; set; }

            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        public sealed class DerivedMarked : Marked
        {
            public override int Amount { get; set; }
        }
        """;

    [Fact]
    public async Task AnInputIntoAPropertyWithoutAPublicSetterIsReported()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("PrivateSetter")
                    .Step<Plain>(step => step.Input(activity => activity.{|#0:Computed|}).From(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnInput, 0, "Activity", "Plain.Computed", "public setter"));
    }

    [Fact]
    public async Task AnOutputFromAPropertyWithoutAPublicGetterIsReported()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("PrivateGetter")
                    .Step<Plain>(step => step.Output(activity => activity.{|#0:Hidden|}).To(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnOutput, 0, "Activity", "Plain.Hidden", "public getter"));
    }

    [Fact]
    public async Task AMarkedActivityTakesInputsOnlyIntoItsMarkedInputsAndGivesOutputsOnlyFromItsMarkedOutputs()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Unmarked")
                    .Step<Marked>(step => step
                        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number)
                        .Input(activity => activity.{|#0:Result|}).From(ctx => ctx.WorkflowData.Number)
                        .Output(activity => activity.{|#1:Amount|}).To(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnInput, 0, "Activity", "Marked.Result", "[WorkflowInput]"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Activity", "Marked.Amount", "[WorkflowOutput]"));
    }

    // Without a setup, with a null one, or with one that maps something else, the required input is missing; the
    // optional input and the required one with a default may stay unmapped.
    [Fact]
    public async Task ARequiredInputThatIsNotMappedIsReportedAtTheStep()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Unmapped")
                    .{|#0:Step<Marked>|}(step => step.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Number))
                    .{|#1:Step<Marked>|}()
                    .{|#2:Step<Marked>|}(null)
                    .{|#3:Step<DerivedMarked>|}(step => step.Input(activity => activity.Amount))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.RequiredInputNotMapped, 0, "Activity", "Marked.Amount"),
            Diagnostic(Descriptors.RequiredInputNotMapped, 1, "Activity", "Marked.Amount"),
            Diagnostic(Descriptors.RequiredInputNotMapped, 2, "Activity", "Marked.Amount"),
            Diagnostic(Descriptors.RequiredInputNotMapped, 3, "Activity", "DerivedMarked.Amount"));
    }

    // A setup that hands its builder elsewhere may map the input there, so nothing is reported; nor is anything for
    // a mapped required input, in a lambda block or through a local.
    [Fact]
    public async Task ARequiredInputIsNotReportedWhenItIsMappedOrTheSetupIsOutOfSight()
    {
        await Analyzer<MappingAnalyzer>("""
            using IxIFlow.Builders.Interfaces;

            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Mapped")
                    .Step<Marked>(step => step.Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number))
                    .Step<DerivedMarked>(step =>
                    {
                        var next = step.Input(activity => activity.Optional).From(ctx => 1);
                        next.Input(activity => activity.Amount).From(ctx => 2);
                    })
                    .Step<Marked>(step => Configure(step))
                    .Step<Marked>(Configure)
                    .Step<Marked>(step =>
                    {
                        Action later = () => step.Input(activity => activity.Amount).From(ctx => 3);
                        later();
                    })
                    .Build();

                private static void Configure<TPrevious>(IActivitySetupBuilder<Data, Marked, TPrevious> step)
                    where TPrevious : class =>
                    step.Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number);
            }
            """ + Types);
    }

    [Fact]
    public async Task ACompensationActivityFollowsTheSameRules()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Compensation")
                    .Step<Plain>()
                    .Saga(saga => saga.Step<Plain>(step => step
                        .CompensateWith<Plain>(compensation => compensation
                            .Input(activity => activity.{|#0:Computed|}).From(ctx => ctx.WorkflowData.Number))
                        .{|#1:CompensateWith<Marked>|}()))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnInput, 0, "Activity", "Plain.Computed", "public setter"),
            Diagnostic(Descriptors.RequiredInputNotMapped, 1, "Activity", "Marked.Amount"));
    }

    [Fact]
    public async Task AChildsDataTakesInputsIntoPublicSettersAndGivesOutputsFromPublicGetters()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Child")
                    .Step<Plain>()
                    .Invoke<ChildData>("Child", 1, step => step
                        .Input(child => child.{|#0:Fixed|}).From(ctx => ctx.WorkflowData.Number)
                        .Input(child => child.Open).From(ctx => ctx.WorkflowData.Number)
                        .Output(child => child.{|#1:Secret|}).To(ctx => ctx.WorkflowData.Number)
                        .Output(child => child.Open).To(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "ChildData.Fixed", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "ChildData.Secret", "public getter"));
    }

    [Fact]
    public async Task AnEventGivesOutputsFromPublicGetters()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Event")
                    .Step<Plain>()
                    .WaitFor<Go>("go", configure: wait => wait
                        .Output(go => go.{|#0:Hidden|}).To(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NotAnOutput, 0, "Event", "Go.Hidden", "public getter"));
    }

    [Fact]
    public async Task AMappingOfAMemberThatIsNotAPublicPropertyIsReported()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Field")
                    .Step<Plain>(step => step.Input(activity => activity.{|#0:Field|}).From(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types,
            Diagnostic(Descriptors.NoPublicProperty, 0, "Activity", "Plain", "Field"));
    }

    [Fact]
    public async Task ValidMappingsReportNothing()
    {
        await Analyzer<MappingAnalyzer>("""
            public static class Flows
            {
                public static object Build() => Workflow.Create<Data>("Valid")
                    .Step<Plain>(step => step
                        .Input(activity => activity.Open).From(ctx => ctx.WorkflowData.Number)
                        .Output(activity => activity.Computed).To(ctx => ctx.WorkflowData.Number))
                    .Step<Marked>(step => step
                        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Number)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Number))
                    .Build();
            }
            """ + Types);
    }
}
