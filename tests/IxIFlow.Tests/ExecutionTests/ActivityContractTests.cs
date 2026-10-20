using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-22")]
public sealed class ActivityContractTests
{
    [Theory]
    [InlineData(typeof(PrivateInput), "is marked as an input but has no public setter")]
    [InlineData(typeof(InputWithoutSetter), "is marked as an input but has no public setter")]
    [InlineData(typeof(OutputWithoutGetter), "is marked as an output but has no public getter")]
    [InlineData(typeof(InOutWithoutSetter), "is marked as an input but has no public setter")]
    [InlineData(typeof(InOutWithoutGetter), "is marked as an output but has no public getter")]
    [InlineData(typeof(RequiredOutput), "has [Required] but is output-only")]
    [InlineData(typeof(RequiredConventionOutput), "has [Required] but is output-only")]
    [InlineData(typeof(DefaultWithoutSetter), "has [DefaultValue] but has no public setter")]
    [InlineData(typeof(InconvertibleDefault), "has a [DefaultValue] that cannot be converted to Int32")]
    [InlineData(typeof(NullValueDefault), "has a [DefaultValue] that cannot be converted to Int32")]
    [InlineData(typeof(DerivedInvalidTypedLiteral), "has a [DefaultValue] that cannot be converted to String")]
    [InlineData(typeof(InvalidTypedLiteral), "has a [DefaultValue] that cannot be converted to String")]
    public void InvalidContractsNameThePropertyEvenWithoutAMapping(Type activity, string reason)
    {
        var definition = new WorkflowDefinition
        {
            Name = "InvalidContract", Version = 1, WorkflowDataType = typeof(Data),
            Steps = [new WorkflowStep { Id = "contract", Name = activity.Name, StepType = WorkflowStepType.Activity,
                ActivityType = activity, WorkflowDataType = typeof(Data) }]
        };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains($"Activity property '{activity.Name}.Value' {reason}", error.Message);
    }

    [Fact]
    public void RequiredInputsNeedAMappingOrAnAttributeDefault()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("MissingRequired")
            .Step<RequiredText>().Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("RequiredText.Value", error.Message);
        Workflow.Create<Data>("MappedRequired").Step<RequiredText>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Text)).Build();
        Workflow.Create<Data>("DefaultRequired").Step<Defaults>().Build();
    }

    [Fact]
    public void AMarkerExcludesEveryUnmarkedProperty()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Unmarked")
            .Step<InOut>(step => step.Input(activity => activity.Unmarked).From(ctx => ctx.WorkflowData.Number)).Build());
        Assert.Contains("InOut.Unmarked' is not an input", error.Message);
        var output = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("UnmarkedOutput")
            .Step<InOut>(step => step.Output(activity => activity.Unmarked).To(ctx => ctx.WorkflowData.Number)).Build());
        Assert.Contains("InOut.Unmarked' is not an output", output.Message);
    }

    [Fact]
    public async Task InOutMapsBothWaysThroughFluent()
    {
        var definition = Workflow.Create<Data>("FluentInOut").Step<InOut>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Number)
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Number)).Build();
        var result = await Execute(definition, new Data { Number = 4 });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(5, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 12)]
    public async Task InOutMapsBothWaysThroughYamlAndStepDefaultsSatisfyRequired(bool stepDefault, int expected)
    {
        var yaml = """
            schemaVersion: "1.0"
            workflow:
              name: YamlInOut
              version: 1
              dataType: Data
            definitions:
              - kind: activity
                id: in-out
                activity: InOut
                input:
                  - target: Value
                    from:
                      kind: path
                      path: workflow.Number
                output:
                  - source: Value
                    to:
                      kind: path
                      path: workflow.Number
            """;
        yaml = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (stepDefault)
            yaml = yaml.Replace("    input:\n      - target: Value\n        from:\n          kind: path\n          path: workflow.Number", "    defaults:\n      Value: 11", StringComparison.Ordinal);
        var options = new WorkflowSourceOptions();
        options.Types["Data"] = "global::IxIFlow.Tests.ExecutionTests.ActivityContractTests.Data";
        options.Types["InOut"] = "global::IxIFlow.Tests.ExecutionTests.ActivityContractTests.RequiredInOut";
        var definition = IxIFlow.Authoring.WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(yaml, "contract.yaml", options));
        var result = await Execute(definition, new Data { Number = 4 });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(expected, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Fact]
    public async Task ConventionActivitiesStillMapBothWays()
    {
        var definition = Workflow.Create<Data>("Convention").Step<Convention>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Number)
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Number)).Build();
        var result = await Execute(definition, new Data { Number = 8 });
        Assert.Equal(9, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RequiredRejectsNullAndEmptyBeforeExecute(string? value)
    {
        var definition = Workflow.Create<Data>("RequiredRuntime").Step<RequiredText>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Text)).Build();
        var result = await Execute(definition, new Data { Text = value });
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("InvalidInput", result.ErrorMessage);
        Assert.Contains("RequiredText.Value", result.ErrorMessage);
        Assert.False(Assert.IsType<Data>(result.WorkflowData).Executed);
    }

    [Fact]
    public async Task RequiredFailureIsAnAttemptFailureThatCanBeRetried()
    {
        var definition = Workflow.Create<Data>("RetryRequired").Step<Empty>().Saga(saga => saga.Step<RequiredText>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Text)
            .OnError<WorkflowEngineException>(handler => handler.ThenRetry(1)))).Build();
        using var provider = Services();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        var trace = await provider.GetRequiredService<IWorkflowClient>().GetTraceAsync(result.InstanceId);
        Assert.Collection(trace.Where(entry => entry.EntryType == TraceEntryType.ActivityFailed),
            first => Assert.Contains("InvalidInput", first.ErrorMessage),
            second => Assert.Contains("InvalidInput", second.ErrorMessage));
    }

    [Fact]
    public async Task RequiredAcceptsWhitespaceAsSpecified()
    {
        var definition = Workflow.Create<Data>("RequiredWhitespace").Step<RequiredText>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Text)).Build();
        var result = await Execute(definition, new Data { Text = " " });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<Data>(result.WorkflowData).Executed);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 23)]
    public async Task TypedAndConvertedDefaultsAreAppliedUnlessMapped(bool mapped, int expected)
    {
        var definition = Workflow.Create<Data>("TypedDefaults").Step<Defaults>(step =>
        {
            if (mapped) step.Input(activity => activity.Typed).From(ctx => ctx.WorkflowData.Number);
            step.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Number);
        }).Build();
        var result = await Execute(definition, new Data { Number = 23 });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(expected + 12, data.Number);
        Assert.Equal("customer-db", data.Text);
    }

    [Fact]
    public async Task AttributeDefaultsAreKeptAcrossARetry()
    {
        var definition = Workflow.Create<Data>("RetryDefault").Step<Empty>().Saga(saga => saga.Step<RetryDefault>(step => step
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Number)
            .OnError<InvalidOperationException>(handler => handler.ThenRetry(1)))).Build();
        var result = await Execute(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(7, Assert.IsType<Data>(result.WorkflowData).Number);
        Assert.Equal("7,7,", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    [Fact]
    public void CatalogUsesDotNetMetadataForInputsOutputsAndInOutAndSerializesTypedDefaults()
    {
        var registry = new ActivityPackageRegistry();
        registry.AddAssembly(typeof(CatalogActivity).Assembly, "contracts", "1.0");
        var activity = Assert.Single(Assert.Single(registry.Packages).Activities, activity => activity.Key == "contract.catalog");
        var input = Assert.Single(activity.Fields, field => field.Key == "Input");
        Assert.Equal("Input label", input.Label);
        Assert.Equal("Input help", input.Help);
        Assert.True(input.Required);
        Assert.Equal("code", input.Control);
        Assert.Equal("Shared label", activity.Outputs["Shared"].Label);
        Assert.Equal("Shared help", activity.Outputs["Shared"].Help);
        Assert.Equal("Output label", activity.Outputs["Output"].Label);
        Assert.Equal("Output help", activity.Outputs["Output"].Help);
        Assert.Contains(activity.Fields, field => field.Key == "Shared" && field.Control == "text");
        Assert.DoesNotContain(activity.Fields, field => field.Key == "Unmarked");
        Assert.DoesNotContain("Unmarked", activity.Outputs.Keys);
        Assert.Equal(3, Assert.IsType<int>(activity.Defaults["Input"]));
        var json = JsonSerializer.Serialize(activity);
        Assert.Equal(3, JsonDocument.Parse(json).RootElement.GetProperty("Defaults").GetProperty("Input").GetInt32());
        var yaml = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
            .Serialize(new { SchemaVersion = "1.0", Package = Assert.Single(registry.Packages).Package,
                Activities = Assert.Single(registry.Packages).Activities });
        var parsed = ActivityPackageManifest.Parse(yaml);
        Assert.Equal("Shared label", Assert.Single(parsed.Activities, item => item.Key == "contract.catalog").Outputs["Shared"].Label);
        var convention = Assert.Single(Assert.Single(registry.Packages).Activities, item => item.Key == "contract.convention");
        Assert.Equal("Value", Assert.Single(convention.Fields).Label);
        Assert.Contains("Value", convention.Outputs);
        Assert.Equal("Value", convention.Outputs["Value"].Label);
        Assert.Null(convention.Outputs["Value"].Help);
    }

    [Fact]
    public void ManifestStillRejectsUnsupportedControls()
    {
        var manifest = new ActivityPackageManifest
        {
            Package = new ActivityPackageIdentity { Name = "contracts", Version = "1.0" },
            Activities = [new ActivityManifestEntry { Key = "bad", Name = "Bad", Version = "1.0", Category = "Tests",
                Fields = [new ActivityManifestField { Key = "Value", Label = "Value", Control = "unknown" }] }]
        };
        Assert.Throws<InvalidOperationException>(manifest.Validate);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    private static async Task<WorkflowExecutionResult> Execute(WorkflowDefinition definition, Data data)
    {
        using var provider = Services();
        return await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, data,
            new WorkflowOptions { EnableTracing = true });
    }

    public sealed class Data
    {
        public int Number { get; set; }
        public string? Text { get; set; }
        public bool Executed { get; set; }
    }

    public class Empty : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class PrivateInput : Empty { [ActivityInput] private int Value { get; set; } }
    public sealed class InputWithoutSetter : Empty { [ActivityInput] public int Value { get; private set; } }
    public sealed class OutputWithoutGetter : Empty { [ActivityOutput] public int Value { private get; set; } }
    public sealed class InOutWithoutSetter : Empty { [ActivityInputOutput] public int Value { get; private set; } }
    public sealed class InOutWithoutGetter : Empty { [ActivityInputOutput] public int Value { private get; set; } }
    public sealed class RequiredOutput : Empty { [ActivityOutput, Required] public int Value { get; set; } }
    public sealed class RequiredConventionOutput : Empty { [Required] public int Value => 1; }
    public sealed class DefaultWithoutSetter : Empty { [DefaultValue(1)] public int Value { get; private set; } }
    public sealed class InconvertibleDefault : Empty { [ActivityInput, DefaultValue("no-number")] public int Value { get; set; } }
    public class InvalidTypedLiteral : Empty { [ActivityInput, DefaultValue(typeof(int), "invalid")] public virtual string? Value { get; set; } }
    public sealed class DerivedInvalidTypedLiteral : InvalidTypedLiteral { public override string? Value { get; set; } }
    public sealed class NullValueDefault : Empty { [ActivityInput, DefaultValue(null)] public int Value { get; set; } }

    public class InOut : IAsyncActivity
    {
        [ActivityInputOutput] public virtual int Value { get; set; }
        public int Unmarked { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    public sealed class RequiredInOut : InOut { [Required] public override int Value { get; set; } }

    [WorkflowActivity("contract.convention")]
    public sealed class Convention : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    public sealed class RequiredText : IAsyncActivity
    {
        [ActivityInput, Required] public string? Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Executed = true; return Task.CompletedTask; }
    }
    public sealed class Defaults : IAsyncActivity
    {
        [ActivityInput, Required, DefaultValue(7)] public int Typed { get; set; }
        [ActivityInput, DefaultValue("12")] public int Converted { get; set; }
        [ActivityInput, Required, DefaultValue("customer-db")] public string ConnectionRef { get; set; } = string.Empty;
        [ActivityOutput] public int Result { get; private set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Result = Typed + Converted; ((Data)context.WorkflowData).Text = ConnectionRef; return Task.CompletedTask; }
    }
    public sealed class RetryDefault : IAsyncActivity
    {
        [ActivityInputOutput, DefaultValue(7)] public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Data)context.WorkflowData).Text += Value + ",";
            if (context.Attempt.Number == 1) { Value = 99; throw new InvalidOperationException("Retry"); }
            return Task.CompletedTask;
        }
    }
    [WorkflowActivity("contract.catalog")]
    public sealed class CatalogActivity : Empty
    {
        [ActivityInput, Required, DefaultValue(3), Display(Name = "Input label", Description = "Input help"), UIHint("code")]
        public int Input { get; set; }
        [ActivityOutput, Display(Name = "Output label", Description = "Output help")]
        public int Output { get; private set; }
        [ActivityInputOutput, Display(Name = "Shared label", Description = "Shared help")]
        public int Shared { get; set; }
        public int Unmarked { get; set; }
    }
}
