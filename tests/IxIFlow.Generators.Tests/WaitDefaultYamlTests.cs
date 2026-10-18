using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "D-14")]
public sealed class WaitDefaultYamlTests
{
    private const string Yaml = """
        workflow:
          name: WaitDefault
          dataType: IxIFlow.Generators.Tests.FlowData
        definitions:
          - kind: suspend
            id: approval
            reason: approve
            event: IxIFlow.Generators.Tests.WaitDefaultAnswer
            defaultPayload:
              Value: 7
              Detail: {Note: suggested}
              Choices: [1, 2]
            output:
              - source: Value
                sourceContext: event
                to: {path: workflow.Result}
        """;

    [Theory]
    [InlineData("suspend", false)]
    [InlineData("suspend", true)]
    [InlineData("waitFor", false)]
    [InlineData("waitFor", true)]
    public async Task YamlDefaultIsListedAndValidatesAgainstTheEventSchema(string kind, bool jsonRoundTrip)
    {
        var document = WorkflowYamlSerializer.Read(WaitYaml(kind));
        if (jsonRoundTrip) document = WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document));
        var definition = GeneratorRegressionTests.Build(WorkflowYamlSerializer.Write(document));
        using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(definition, new FlowData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Assert.IsType<FlowData>(started.WorkflowData).Result);
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        var payload = JsonSerializer.Deserialize<WaitDefaultAnswer>(wait.DefaultPayloadJson!)!;
        Assert.Equal(7, payload.Value);
        Assert.Equal("suggested", payload.Detail!.Note);
        Assert.Equal([1, 2], payload.Choices);
        var schema = JsonSchema.FromText(wait.EventSchema!);
        Assert.True(schema.Evaluate(JsonSerializer.Deserialize<JsonElement>(wait.DefaultPayloadJson!)).IsValid);
        Assert.False(schema.Evaluate(JsonSerializer.Deserialize<JsonElement>("{\"Value\":\"wrong\"}")).IsValid);

        var answered = await client.ReplyAsync(wait.Token, new WaitDefaultAnswer { Value = 3 });
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);
        Assert.Equal(3, Assert.IsType<FlowData>(answered.WorkflowData).Result);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("waitFor")]
    public void YamlDefaultsMatchFluentMetadataAndDoNotChangeTheFingerprint(string kind)
    {
        var yaml = WaitYaml(kind);
        var generated = GeneratorRegressionTests.Build(yaml);
        var builder = new WorkflowBuilder<FlowData, object>([], "WaitDefault");
        Action<ISuspendSetupBuilder<FlowData, WaitDefaultAnswer, object>> configure = wait => wait
            .DefaultPayload(new WaitDefaultAnswer { Value = 7, Detail = new() { Note = "suggested" }, Choices = [1, 2] })
            .Output(answer => answer.Value).To(ctx => ctx.WorkflowData.Result);
        var fluent = (kind == "suspend"
            ? builder.Suspend<WaitDefaultAnswer>("approve", configure: configure)
            : builder.WaitFor<WaitDefaultAnswer>("approve", configure: configure))
            .WithId("approval").Build();
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(fluent.Steps).DefaultPayload),
            JsonSerializer.Serialize(Assert.Single(generated.Steps).DefaultPayload));
        var original = new WorkflowScopeCatalog(generated).Fingerprint;
        Assert.Equal(original, new WorkflowScopeCatalog(fluent).Fingerprint);
        Assert.Equal(original, new WorkflowScopeCatalog(GeneratorRegressionTests.Build(yaml.Replace("Value: 7", "Value: 9"))).Fingerprint);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("waitFor")]
    public void WrongYamlDefaultPropertyTypeHasAPositionedCompilerDiagnostic(string kind)
    {
        var source = WorkflowSourceTranslator.Translate(WaitYaml(kind).Replace("Value: 7", "Value: wrong"), "default.yaml");
        var error = Assert.Single(GeneratorRegressionTests.Compilation(source).GetDiagnostics(),
            diagnostic => diagnostic.Id == "CS0029");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("string", error.GetMessage());
        Assert.Contains("int", error.GetMessage());
        Assert.Equal("default.yaml", error.Location.GetMappedLineSpan().Path);
        Assert.Equal(9, error.Location.GetMappedLineSpan().StartLinePosition.Line);
    }

    private static string WaitYaml(string kind) => kind == "waitFor"
        ? Yaml.Replace("kind: suspend", "kind: waitFor").Replace("reason: approve", "key: approve")
        : Yaml;
}

public sealed class WaitDefaultAnswer
{
    public int Value { get; set; }
    public WaitDefaultDetail? Detail { get; set; }
    public List<int> Choices { get; set; } = [];
}

public sealed class WaitDefaultDetail
{
    public string? Note { get; set; }
}
