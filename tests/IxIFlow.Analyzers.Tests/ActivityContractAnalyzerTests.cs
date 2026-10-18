using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-24")]
public sealed class ActivityContractAnalyzerTests
{
    [Theory]
    [InlineData("[ActivityInput]", "{ get; private set; }", "IXIF0005")]
    [InlineData("[ActivityOutput]", "{ private get; set; }", "IXIF0006")]
    [InlineData("[ActivityInputOutput]", "{ get; private set; }", "IXIF0005")]
    [InlineData("[ActivityInputOutput]", "{ private get; set; }", "IXIF0006")]
    [InlineData("[ActivityOutput, Required]", "{ get; set; }", "IXIF0007")]
    [InlineData("[Required]", "{ get; private set; }", "IXIF0007")]
    [InlineData("[DefaultValue(1)]", "{ get; private set; }", "IXIF0008")]
    [InlineData("[ActivityInput, DefaultValue(\"invalid\")]", "{ get; set; }", "IXIF0009")]
    [InlineData("[ActivityInput, DefaultValue(\"2147483648\")]", "{ get; set; }", "IXIF0009")]
    [InlineData("[ActivityInput, DefaultValue(null)]", "{ get; set; }", "IXIF0009")]
    [InlineData("[ActivityInput, DefaultValue(typeof(int), \"invalid\")]", "{ get; set; }", "IXIF0009")]
    public async Task InvalidContractsAreReportedAtTheProperty(string attributes, string accessors, string id)
    {
        var descriptor = id switch
        {
            "IXIF0005" => Descriptors.InputNeedsSetter,
            "IXIF0006" => Descriptors.OutputNeedsGetter,
            "IXIF0007" => Descriptors.RequiredOnOutput,
            "IXIF0008" => Descriptors.DefaultNeedsSetter,
            _ => Descriptors.InvalidDefault
        };
        await Analyzer<MappingAnalyzer>($$"""
            using System.ComponentModel;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { }
            public sealed class Bad : IAsyncActivity
            {
                {{attributes}}
                public int {|#0:Value|} {{accessors}}
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Contract").Step<Bad>().Step<Bad>().Build();
            }
            """, id == "IXIF0009" ? Diagnostic(descriptor, 0, "Bad.Value", "Int32") : Diagnostic(descriptor, 0, "Bad.Value"));
    }

    [Fact]
    public async Task APrivateMarkerCannotTurnIntoConvention()
    {
        await Analyzer<MappingAnalyzer>("""
            public sealed class Data { }
            public sealed class Bad : IAsyncActivity
            {
                [ActivityInput] private int {|#0:Value|} { get; set; }
                public int Other { get; set; }
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Private").Step<Bad>().Build();
            }
            """, Diagnostic(Descriptors.InputNeedsSetter, 0, "Bad.Value"));
    }

    [Fact]
    public async Task InOutRequiredAndTypedDefaultsUseTheNewSymbols()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Number { get; set; } }
            public sealed class Contract : IAsyncActivity
            {
                [ActivityInput, Required, DefaultValue(2)] public int Typed { get; set; }
                [ActivityInput, Required, DefaultValue("3")] public int Converted { get; set; }
                [ActivityInput, DefaultValue(typeof(decimal), "2.5")] public decimal Decimal { get; set; }
                [ActivityInput, DefaultValue(null)] public int? Nullable { get; set; }
                [ActivityInputOutput, Required] public int Shared { get; set; }
                [ActivityOutput] public int Result { get; private set; }
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Valid() => Workflow.Create<Data>("Valid").Step<Contract>(step => step
                    .Input(activity => activity.Shared).From(ctx => ctx.WorkflowData.Number)
                    .Output(activity => activity.Shared).To(ctx => ctx.WorkflowData.Number)).Build();
                public static object Missing() => Workflow.Create<Data>("Missing").{|#0:Step<Contract>|}().Build();
            }
            """, Diagnostic(Descriptors.RequiredInputNotMapped, 0, "Activity", "Contract.Shared"));
    }

    [Fact]
    public async Task ConventionRequiredInputsStillNeedMappings()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public string Text { get; set; } = ""; }
            public sealed class Plain : IAsyncActivity
            {
                [Required] public string Value { get; set; } = "";
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Missing() => Workflow.Create<Data>("Missing").{|#0:Step<Plain>|}().Build();
                public static object Valid() => Workflow.Create<Data>("Valid").Step<Plain>(step => step
                    .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Text)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Text)).Build();
            }
            """, Diagnostic(Descriptors.RequiredInputNotMapped, 0, "Activity", "Plain.Value"));
    }
}
