using System.Runtime.Loader;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "W-03")]
public sealed class SwitchFinalReviewYamlTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CatalogInvokeMapsLexicalFaultInCatchAndCatchSwitchLikeFluent(bool switchCase)
    {
        var child = Workflow.Create<SwitchYamlData>("FaultChild").Input(d => d.Text).Output(d => d.Text)
            .Step<SwitchYamlMark>(s => s.Input(x => x.Text).From(ctx => ctx.WorkflowData.Text + "|child")
                .Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)).Build();
        var contract = WorkflowDefinitionValidator.GetContract(child);
        var options = new WorkflowSourceOptions();
        options.Workflows.Add("FaultChild@1", new(typeof(SwitchYamlData).FullName!, 1) { ContractJson = contract.ToJson() });
        var invoke = "{kind: invokeWorkflow, workflow: {name: FaultChild, version: 1}, input: [{target: Text, from: {path: fault.Message}}], output: [{source: Text, to: {path: workflow.Text}}]}";
        var body = switchCase ? "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: [" + invoke + "]}]}" : invoke;
        var yaml = "workflow: {name: FaultInvoke, dataType: IxIFlow.Generators.Tests.SwitchYamlData}\ndefinitions:\n" +
            "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlMark}\n" +
            "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [" + body + "]}]}";
        void Call(ICatchWorkflowBuilder<SwitchYamlData, SwitchYamlFault, SwitchYamlMark> b) => b.Invoke<SwitchYamlData>("FaultChild", 1, contract,
            s => s.Input(x => x.Text).From(ctx => ctx.Fault.Message).Output(x => x.Text).To(ctx => ctx.WorkflowData.Text));
        var fluent = Workflow.Create<SwitchYamlData>("FaultInvoke").Step<SwitchYamlMark>().Try(t => t.Step<SwitchYamlFail>())
            .Catch<InvalidOperationException, SwitchYamlFault>(c =>
            {
                if (switchCase) c.Switch(ctx => ctx.Fault.Message, cases => cases.Case("fault", Call));
                else Call(c);
            }).Build();
        var source = WorkflowSourceTranslator.Translate(yaml, "fault-invoke.yaml", options);
        var compilation = GeneratorRegressionTests.Compilation(source);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var generated = (WorkflowDefinition)assembly.GetTypes().Single(t => t.GetMethod("Build") != null).GetMethod("Build")!.Invoke(null, null)!;
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var step = Assert.Single(ParallelRules.Flatten(generated.Steps), s => s.StepType == WorkflowStepType.WorkflowInvocation);
        Assert.Equal(contract.ToJson(), step.InvokedContract!.ToJson());
        Assert.Equal(contract.Hash, step.InvokedContractHash);
        Assert.Equal(WorkflowInvocationContractKind.ExactPin, step.InvokedContractKind);
        foreach (var definition in new[] { fluent, generated })
        {
            var catalog = new WorkflowCatalog();
            await catalog.RegisterWorkflowAsync(child);
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow();
            services.AddSingleton<IWorkflowCatalog>(catalog);
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IWorkflowClient>();
            var run = await client.RunAsync(definition, new SwitchYamlData());
            if (run.Status == WorkflowExecutionStatus.Suspended)
                run = await client.AwaitAsync(run.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
            Assert.Equal("fault|child", Assert.IsType<SwitchYamlData>(run.WorkflowData).Text);
        }
    }
}
