using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class InlineDebuggerFixture
{
    public const string Name = "InlineDebugCaller";
    public const string Secret = "inline-secret-never-in-inspection";
    public const string Condition = "context.WorkflowData.Secret != \"\"";

    public static WorkflowDefinition Definition(bool baseline = false)
    {
        var builder = new WorkflowBuilder<FirstData, object>([], Name).WaitFor<Signal>("start");
        if (baseline)
            return builder.Use<BaselineBody, DebugData>(u => u.Input(x => x.Value).From(c => c.WorkflowData.Value)
                    .Output(x => x.Value).To(c => c.WorkflowData.Value).Output(x => x.Result).To(c => c.WorkflowData.Result))
                .WithId("first").Build();
        return builder.Use<Body, DebugData>(u => u.Input(x => x.Value).From(c => c.WorkflowData.Value)
                .Input(x => x.Secret).From(c => Secret).Output(x => x.Value).To(c => c.WorkflowData.Result)).WithId("first")
            .Use<Middle, DebugData>(u => u.Input(x => x.Value).From(c => c.WorkflowData.Result)
                .Input(x => x.Secret).From(c => Secret).Output(x => x.Value).To(c => c.WorkflowData.Result)).WithId("second")
            .Assign(c => c.WorkflowData.Value, c => c.WorkflowData.Result).WithId("after").Build();
    }

    [Workflow("InlineDebugLeaf")]
    public sealed class Body : IWorkflow<DebugData>
    {
        public void Build(IWorkflowBuilder<DebugData> b) => b
            .Assign(c => c.WorkflowData.Value, c => c.WorkflowData.Value + "-entered").WithId("enter", "workflow.yaml", 5)
            .Parallel(p => p.Do(s => s.Assign(c => c.WorkflowData.Result, c => "joined"))
                .Do(s => s.Assign(c => c.WorkflowData.Secret, c => "changed-secret"))).WithId("join")
            .Assign(c => c.WorkflowData.Value, c => c.WorkflowData.Value + c.WorkflowData.Result).WithId("exit");
    }

    [Workflow("InlineDebugMiddle")]
    public sealed class Middle : IWorkflow<DebugData>
    {
        public void Build(IWorkflowBuilder<DebugData> b) => b.Use<Body, DebugData>(u => u
            .Input(x => x.Value).From(c => c.WorkflowData.Value).Input(x => x.Secret).From(c => c.WorkflowData.Secret)
            .Output(x => x.Value).To(c => c.WorkflowData.Value)).WithId("nested");
    }

    [Workflow("InlineDebugBaseline")]
    public sealed class BaselineBody : IWorkflow<DebugData>
    {
        public void Build(IWorkflowBuilder<DebugData> b) => b.Assign(c => c.WorkflowData.Value, c => c.WorkflowData.Value).Parallel(p => p
            .Do(s => s.Assign(c => c.WorkflowData.Value, c => "branch").WaitFor<Signal>("left"))
            .Do(s => s.WaitFor<Signal>("right"))).WithId("join");
    }

    public static async Task Publish(IWorkflowStateRepository store, bool baseline = false)
    {
        var options = PublishedInlineScenario.Options();
        options.SourceOptions.Types["Callee"] = "global::" + typeof(DebugData).FullName;
        await PublishedInlineScenario.Publish(store, "InlineDebugLeaf", baseline ? BaselineYaml : BodyYaml, options: options);
        if (!baseline)
            await PublishedInlineScenario.Publish(store, "InlineDebugMiddle", """
                workflow: {name: InlineDebugMiddle, dataType: Callee}
                definitions:
                  - kind: use
                    id: nested
                    workflow: {name: InlineDebugLeaf, version: 1}
                    input:
                      - {target: Value, from: {path: workflow.Value}}
                      - {target: Secret, from: {path: workflow.Secret}}
                    output: [{source: Value, to: {path: workflow.Value}}]
                """, options: options);
        var caller = """
            workflow: {name: InlineDebugCaller, dataType: Data}
            definitions:
              - kind: suspend
                event: Signal
                reason: start
              - kind: use
                id: first
                workflow: {name: InlineDebugLeaf, version: 1}
                input:
                  - {target: Value, from: {path: workflow.Value}}
                  - {target: Secret, from: {kind: constant, value: inline-secret-never-in-inspection}}
                output:
            """ + "\n" + (baseline ? """
                    - {source: Value, to: {path: workflow.Value}}
                    - {source: Result, to: {path: workflow.Result}}
            """ : """
                    - {source: Value, to: {path: workflow.Result}}
              - kind: use
                id: second
                workflow: {name: InlineDebugMiddle, version: 1}
                input:
                  - {target: Value, from: {path: workflow.Result}}
                  - {target: Secret, from: {kind: constant, value: inline-secret-never-in-inspection}}
                output: [{source: Value, to: {path: workflow.Result}}]
              - kind: assign
                id: after
                to: {path: workflow.Value}
                value: {path: workflow.Result}
            """);
        await PublishedInlineScenario.Publish(store, Name, caller, options: options);
    }

    private const string BodyYaml = """
        workflow: {name: InlineDebugLeaf, dataType: Callee}
        definitions:
          - kind: assign
            id: enter
            to: {path: workflow.Value}
            value: {kind: binary, operator: add, left: {path: workflow.Value}, right: {kind: constant, value: '-entered'}}
          - kind: parallel
            id: join
            branches:
              - steps:
                  - kind: assign
                    to: {path: workflow.Result}
                    value: {kind: constant, value: joined}
              - steps:
                  - kind: assign
                    to: {path: workflow.Secret}
                    value: {kind: constant, value: changed-secret}
          - kind: assign
            id: exit
            to: {path: workflow.Value}
            value: {kind: binary, operator: add, left: {path: workflow.Value}, right: {path: workflow.Result}}
        """;

    private const string BaselineYaml = """
        workflow: {name: InlineDebugLeaf, dataType: Callee}
        definitions:
          - kind: parallel
            id: join
            branches:
              - steps:
                  - kind: assign
                    to: {path: workflow.Value}
                    value: {kind: constant, value: branch}
                  - kind: suspend
                    event: Signal
                    reason: left
              - steps:
                  - kind: suspend
                    event: Signal
                    reason: right
        """;

    public static ServiceProvider Host(IWorkflowStateRepository store, WorkflowCatalog catalog, string? cache,
        Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddIxIFlow(o => { o.Debugging = true; o.VerifyCheckpointParts = true; });
        if (cache != null) builder.UseWorkflowBundles(cache);
        else services.AddSingleton<IWorkflowCatalog>(catalog);
        services.AddSingleton(store);
        configure(services);
        return services.BuildServiceProvider();
    }

    public static async Task<string> Start(ServiceProvider host, bool published, bool baseline = false)
    {
        var client = host.GetRequiredService<IWorkflowClient>();
        if (!published) return (await client.RunAsync(Definition(baseline), new FirstData { Value = "initial" })).InstanceId;
        var id = await client.StartAsync(Name, 1, new FirstData { Value = "initial" });
        await PublishedBundleScenario.UntilAsync(host.GetRequiredService<IWorkflowStateRepository>(), id, WorkflowStatus.Suspended);
        return id;
    }
}
