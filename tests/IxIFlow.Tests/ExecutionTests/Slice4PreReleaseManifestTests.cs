using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-27")]
public sealed class Slice4PreReleaseManifestTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PreReleaseManifestRegeneratesTheHashesUsedForVersionRefusals()
    {
        // The archived final pre-release writer at 0cca65f1^, restricted to ReleaseFormatTests.Waiting().
        var definition = new WorkflowBuilder<object, object>([], "Release formats").WaitFor<string>("release").Build();
        var generated = GenerateManifest(definition);
        output.WriteLine("PRERELEASE_MANIFEST=" + Encoding.UTF8.GetString(generated));

        var fixture = File.ReadAllBytes(FixturePath());
        Assert.Equal(fixture, generated);

        Assert.Equal(
            "5A0D18A7978F0BDE06AC04A56C57169D4DA4BF12F50E417871C3E68D88CC2B9D",
            Convert.ToHexString(SHA256.HashData(fixture)));

        var seed = new InMemoryWorkflowStateRepository();
        await new WorkflowCatalog(seed).RegisterWorkflowAsync(definition);
        var current = (await seed.GetWorkflowVersionAsync(definition.Name, definition.Version))!;

        var namespaced = new JsonObject { ["Format"] = 1, ["Runtime"] = "ixiflow-1" };

        foreach (var (key, value) in JsonNode.Parse(fixture)!.AsObject())
            if (key != "Format")
                namespaced.Add(key, value?.DeepClone());

        Assert.Equal(
            current.StructureHash,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(namespaced.ToJsonString()))));

        foreach (var format in Enumerable.Range(1, 9))
        {
            var manifest = JsonNode.Parse(fixture)!;
            manifest["Format"] = format;
            var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            Assert.Equal((string)ReleaseFormatTests.PreReleaseHashes.Single(row => (int)row[0] == format)[1], hash);
            Assert.NotEqual(current.StructureHash, hash);
            var store = new InMemoryWorkflowStateRepository();
            await store.RegisterWorkflowVersionAsync(current with { StructureHash = hash });

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                new WorkflowCatalog(store).RegisterWorkflowAsync(definition));

            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains(hash, error.Message);
        }
    }

    private static string Shape(object? expression)
    {
        Assert.Null(expression);

        return "";
    }

    private static string RetrySignature(object? policy)
    {
        Assert.Null(policy);

        return "";
    }

    private static string FixturePath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "prerelease-manifest-format9.json");

    private static byte[] GenerateManifest(WorkflowDefinition definition)
    {
        // Both authoring routes lower to this executable model. Include empty scopes as well:
        // adding an empty branch changes the topology even when it adds no steps.
        var _steps = definition.Steps.Select((step, index) => new { Id = "root/" + index, Step = step })
            .ToDictionary(item => item.Id, item => item.Step);

        var _scopes = new Dictionary<string, int> { ["root"] = definition.Steps.Count };

        foreach (var (id, step) in _steps)
        foreach (var scope in step.ChildScopes())
            _scopes.Add(id + "/" + scope.Kind, scope.Steps.Count);

        var manifest = new
        {
            Format = 9,
            RequiredScopes = definition.RequiredScopes.Select(WorkflowTypeIdentity.StableName).Order(StringComparer.Ordinal),
            Data = WorkflowTypeIdentity.StableName(definition.WorkflowDataType),
            DataTypes = _steps.Values.Select(step => step.WorkflowDataType).Prepend(definition.WorkflowDataType)
                .OfType<Type>().Distinct().OrderBy(WorkflowTypeIdentity.StableName, StringComparer.Ordinal)
                .Select(type => new { Type = WorkflowTypeIdentity.StableName(type), Combiners = ParallelRules.CombinerSignature(type) }),
            Scopes = _scopes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { Id = pair.Key, Count = pair.Value }),
            Steps = _steps.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var step = pair.Value;

                return WorkflowBuildError.Check(
                    step, () => new
                    {
                        Id = pair.Key,
                        step.StepType,
                        Data = WorkflowTypeIdentity.StableName(step.WorkflowDataType),
                        Previous = WorkflowTypeIdentity.StableName(step.PreviousStepDataType),
                        Activity = WorkflowTypeIdentity.StableName(step.ActivityType),
                        Workflow = WorkflowTypeIdentity.StableName(step.WorkflowType),
                        InvokedData = WorkflowTypeIdentity.StableName(step.InvokedDataType),
                        ScopeValue = step.ScopeValueType == null ? null : WorkflowTypeIdentity.StableName(step.ScopeValueType),
                        ScopeRetry = RetrySignature(step.ScopeRetryPolicy), step.ScopeThreading, step.ScopeConcurrency,
                        step.WorkflowName, step.WorkflowVersion, step.CancelWithParent, step.InlineStructureHash,
                        Event = WorkflowTypeIdentity.StableName(step.ResumeEventType),
                        WaitKey = step.StepMetadata.GetValueOrDefault("WaitKey") as string,
                        Timeout = step.WaitTimeout?.Ticks, Delay = step.DelayDuration?.Ticks,
                        step.ParallelJoinMode, step.LoopType,
                        step.Target, step.IsDefault, step.Explicit, step.TriggerKind,
                        After = Shape(step.AfterExpression),
                        EdgeLabel = SwitchLabelSignature.Of(step.EdgeLabel),
                        Graph = step.StepType is WorkflowStepType.Flowchart or WorkflowStepType.StateMachine
                            ? new
                            {
                                Kind = step.StepType, Start = step.GraphStart,
                                Nodes = step.GraphNodes.Select(node => new { node.Key, Final = false })
                                    .Concat(step.GraphStates.Select(state => new { state.Key, state.Final }))
                                    .OrderBy(node => node.Key, StringComparer.Ordinal)
                            }
                            : null,
                        Exception = WorkflowTypeIdentity.StableName(step.ExceptionType),
                        Fault = WorkflowTypeIdentity.StableName(step.FaultType),
                        Factory = Shape(step.ExceptionExpression),
                        FactoryType = WorkflowTypeIdentity.StableName(step.ExceptionFactoryReturnType),
                        step.RethrowOwnerId,
                        Condition = Shape(step.ConditionExpression),
                        Outcome = Shape(step.OutcomeExpression),
                        SwitchValueType = WorkflowTypeIdentity.StableName(step.SwitchValueType),
                        Assignment = step.Assignment == null
                            ? null
                            : new
                            {
                                step.Assignment.TargetPath,
                                Type = WorkflowTypeIdentity.StableName(step.Assignment.ValueType),
                                Target = Shape(step.Assignment.Target), Value = Shape(step.Assignment.Value)
                            },
                        Completion = Shape(step.ParallelCompletionExpression),
                        Outcomes = step.OutcomeBranches.Select(_ => (object?)null),
                        Policy = "|",
                        Compensations = (step.StepMetadata.GetValueOrDefault("CompensationActivities") as List<CompensationActivityInfo> ??
                                [])
                            .Select(activity => new
                            {
                                Activity = WorkflowTypeIdentity.StableName(activity.ActivityType),
                                Previous = WorkflowTypeIdentity.StableName(activity.PreviousChainActivityType),
                                Reply = WorkflowTypeIdentity.StableName(activity.ReplyType)
                            }),
                        Inputs = step.InputMappings.Select(_ => (object?)null),
                        Outputs = step.OutputMappings.Select(_ => (object?)null),
                        step.RequestName,
                        Reply = WorkflowTypeIdentity.StableName(step.ReplyType), step.ReplyToRequest,
                        Subject = Shape(step.RequestSubjectExpression),
                        Combiners = step.ParallelCombiners.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => new { Path = pair.Key, Type = WorkflowTypeIdentity.StableName(pair.Value) }),
                        step.ForEachParallel, step.ForEachMaxConcurrency,
                        Item = WorkflowTypeIdentity.StableName(step.ForEachItem?.ItemType),
                        ItemCompletion = Shape(step.ForEachCompletionExpression),
                        Items = Shape(step.ForEachItemsExpression),
                        ResultType = WorkflowTypeIdentity.StableName(step.ResultType),
                        StoredResult = WorkflowTypeIdentity.StableName(step.ForEachStoredResultType),
                        ConditionResult = WorkflowTypeIdentity.StableName(step.ForEachConditionResultType),
                        Result = Shape(step.ResultExpression), Into = Shape(step.ForEachIntoExpression)
                    });
            })
        };

        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }
}