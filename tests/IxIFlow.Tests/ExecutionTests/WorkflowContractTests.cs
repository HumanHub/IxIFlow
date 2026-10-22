using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
public sealed class WorkflowContractTests
{
    [Fact]
    [Trait("Feature", "authoring")]
    public void DerivationIncludesDirectionsDefaultsAndMetadata()
    {
        var contract = WorkflowContract.OfType(typeof(Marked));
        Assert.True(contract.Declared);
        Assert.Equal(new[] { "Count", "Text" }, contract.Inputs.Select(member => member.Name));
        Assert.Equal(new[] { "Count", "Result" }, contract.Outputs.Select(member => member.Name));
        var count = contract.Inputs[0];
        Assert.True(count.Required);
        Assert.True(count.HasDefault);
        Assert.Equal("7", count.DefaultJson);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(int)), count.Type);
        Assert.Equal(("Count label", "Count help", "code"), (count.DisplayName, count.Description, count.UIHint));
        Assert.Equal(count, contract.Outputs[0]);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void ListsMergeAndEmptyListsOverrideTheirSide()
    {
        var builder = Workflow.Create<Marked>("Merged").Input(data => data.Extra).Output(data => data.Extra);
        var definition = builder.Step<Noop>().Try(body => body.Step<Noop>()).Catch(_ => { }).Finally(_ => { })
            .Saga(body => body.Step<Noop>()).Step<Noop>().Build();
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Equal(new[] { "Count", "Extra", "Text" }, contract.Inputs.Select(member => member.Name));
        Assert.Equal(new[] { "Count", "Extra", "Result" }, contract.Outputs.Select(member => member.Name));
        var empty = Workflow.Create<OnlyInput>("Empty").Input().Output().Step<Noop>().Build();
        Assert.Empty(WorkflowDefinitionValidator.GetContract(empty).Inputs);
        Assert.Empty(WorkflowDefinitionValidator.GetContract(empty).Outputs);
        Assert.NotNull(empty.DeclaredInputs);
        Assert.Empty(empty.DeclaredInputs);
        Assert.Null(Workflow.Create<Plain>("Omitted").Build().DeclaredInputs);
        Assert.True(WorkflowDefinitionValidator.GetContract(empty).Declared);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void ConventionUsesMetadataAsActivitiesDo()
    {
        var contract = WorkflowContract.OfType(typeof(Plain));
        Assert.False(contract.Declared);
        var input = Assert.Single(contract.Inputs);
        Assert.Equal("Value", input.Name);
        Assert.True(input.Required);
        Assert.Equal("5", input.DefaultJson);
        Assert.Equal("plain", input.DisplayName);
        Assert.Equal(new[] { "ReadOnly", "Value" }, contract.Outputs.Select(member => member.Name));
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void CanonicalJsonAndHashHaveAGoldenRepresentation()
    {
        var contract = WorkflowContract.OfType(typeof(Golden));
        const string expected = "{\"Inputs\":[{\"Name\":\"Value\",\"Type\":\"ixi-type:1:N22:System.Private.CoreLib12:System.Int320:\",\"Required\":false,\"HasDefault\":true,\"DefaultJson\":\"3\",\"DisplayName\":null,\"Description\":null,\"UIHint\":null}],\"Outputs\":[],\"Declared\":true}";
        Assert.Equal(expected, contract.ToJson());
        Assert.Equal("57CC467ADB4E8412DEE98FB6D92C4B26DAA2A4E9DCCA7C1C2B8A2FECC4139DFA", contract.Hash);
        Assert.Equal(expected, WorkflowContract.Parse(expected).ToJson());
        Assert.Equal(contract.Hash, WorkflowContract.Parse(" \n" + expected).Hash);
        var two = WorkflowContract.OfType(typeof(Marked));
        Assert.Equal(two.ToJson(), new WorkflowContract(two.Inputs.Reverse().ToArray(), two.Outputs.Reverse().ToArray(), true).ToJson());
        Assert.Equal(two.Hash, WorkflowContract.Parse(two.ToJson()).Hash);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void ResourceDisplayMetadataUsesInvariantUICultureAndRestoresTheCaller()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var german = WorkflowContract.OfType(typeof(LocalizedDisplay));
            Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = WorkflowContract.OfType(typeof(LocalizedDisplay));
            Assert.Equal("tr-TR", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(german.ToJson(), turkish.ToJson());
            Assert.Equal(german.Hash, turkish.Hash);
            Assert.Equal("invariant label", Assert.Single(turkish.Inputs).DisplayName);
            Assert.Equal("invariant description", Assert.Single(turkish.Inputs).Description);
            Assert.Throws<WorkflowEngineException>(() => WorkflowContract.OfType(typeof(BadInput)));
            Assert.Equal("tr-TR", CultureInfo.CurrentUICulture.Name);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void JsonWithDoubleAndDateTimeOffsetDefaultsIsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = WorkflowContract.OfType(typeof(CultureDefaults));
            var json = invariant.ToJson();
            var hash = invariant.Hash;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var localized = WorkflowContract.OfType(typeof(CultureDefaults));
            Assert.Equal(json, localized.ToJson());
            Assert.Equal(hash, localized.Hash);
            Assert.Equal("1.25", localized.Inputs.Single(member => member.Name == "Number").DefaultJson);
            Assert.Equal("\"2026-10-20T12:34:56+05:30\"", localized.Inputs.Single(member => member.Name == "When").DefaultJson);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void ConventionSkipsIndexersButDeclarationsRefuseThem()
    {
        var child = Workflow.Create<IndexedData>("Indexed").Build();
        Assert.Equal(new[] { "Value" }, WorkflowDefinitionValidator.GetContract(child).Inputs.Select(member => member.Name));
        Parent().Invoke<IndexedData>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => 1)).Build();
        Workflow.Create<List<ParentData>>("ListData").Build();
        foreach (var input in new[] { true, false })
        {
            var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(new WorkflowDefinition
            {
                Name = "ListedIndexer", Version = 1, WorkflowDataType = typeof(IndexedData),
                DeclaredInputs = input ? ["Item"] : null, DeclaredOutputs = input ? null : ["Item"]
            }));
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.Contains("Item' is an indexer", error.Message);
        }
        Assert.Contains("Item' is an indexer", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<MarkedIndexer>("MarkedIndexer").Build()).Message);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void HiddenBasePropertiesAreRefusedOnlyWhenTheNameIsExposed()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<HiddenProperty>("Hidden").Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("HiddenProperty.Value' hides a base property and cannot be declared", error.Message);
        var excluded = Workflow.Create<HiddenProperty>("Excluded").Input(data => data.Other).Build();
        Assert.Equal(new[] { "Other" }, WorkflowDefinitionValidator.GetContract(excluded).Inputs.Select(member => member.Name));
        Assert.Empty(WorkflowDefinitionValidator.GetContract(excluded).Outputs);
        error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<HiddenProperty>("ListedHidden").Input(data => data.Value).Build());
        Assert.Contains("hides a base property", error.Message);
    }

    [Theory]
    [InlineData("type", "no invoked data type")]
    [InlineData("contract", "no invoked contract")]
    [InlineData("hash", "invalid invoked contract hash")]
    [InlineData("wrong-hash", "invalid invoked contract hash")]
    [Trait("Feature", "authoring")]
    public void WholeDefinitionValidationRequiresEveryInvocationContractPart(string missing, string message)
    {
        var contract = WorkflowContract.OfType(typeof(Golden));
        var step = new WorkflowStep
        {
            Id = "hand-built", Name = "HandBuiltChild", StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowName = "Child", WorkflowVersion = 1, WorkflowDataType = typeof(ParentData),
            Runtime = WorkflowStepDelegates.Create<ParentData>(),
            InvokedDataType = missing == "type" ? null : typeof(Golden),
            InvokedContract = missing == "contract" ? null : contract,
            InvokedContractHash = missing == "hash" ? null : missing == "wrong-hash" ? "wrong" : contract.Hash
        };
        var definition = new WorkflowDefinition { Name = "HandBuilt", Version = 1, WorkflowDataType = typeof(ParentData), Steps = [step] };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("HandBuiltChild", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Inputs\":[],\"Outputs\":[],\"Declared\":true,\"Declared\":false}")]
    [InlineData("{\"Inputs\":null,\"Outputs\":[],\"Declared\":true}")]
    [InlineData("{\"Inputs\":[],\"Outputs\":[],\"Declared\":\"false\"}")]
    [InlineData("{\"Inputs\":[{}],\"Outputs\":[],\"Declared\":true}")]
    [Trait("Feature", "authoring")]
    public void ParseRefusesMalformedDescriptors(string json) => Assert.ThrowsAny<Exception>(() => WorkflowContract.Parse(json));

    [Fact]
    [Trait("Feature", "authoring")]
    public void DescriptorRefusesDuplicateMembersBadTypesAndInvalidDefaults()
    {
        var member = Assert.Single(WorkflowContract.OfType(typeof(Golden)).Inputs);
        Assert.Throws<ArgumentException>(() => new WorkflowContract([member, member], [], true));
        Assert.Throws<ArgumentException>(() => new WorkflowContract([member with { Type = "System.Int32" }], [], true));
        Assert.Throws<ArgumentException>(() => new WorkflowContract([member with { HasDefault = false }], [], true));
        Assert.ThrowsAny<Exception>(() => new WorkflowContract([member with { DefaultJson = "{" }], [], true));
        Assert.Throws<ArgumentException>(() => new WorkflowContract([member], [member with { Required = true }], true));
        Assert.Throws<ArgumentException>(() => new WorkflowContract([], [member with { Required = true }], true));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("default")]
    [InlineData("default-presence")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [Trait("Feature", "authoring")]
    public void ParseRefusesInvalidMemberDescriptors(string defect)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(WorkflowContract.OfType(typeof(Golden)).ToJson())!;
        var inputs = node["Inputs"]!.AsArray();
        switch (defect)
        {
            case "name": inputs[0]!["Name"] = " "; break;
            case "type": inputs[0]!["Type"] = "ixi-type:1:N0:0:0:"; break;
            case "default": inputs[0]!["DefaultJson"] = "{"; break;
            case "default-presence": inputs[0]!["HasDefault"] = false; break;
            case "duplicate": inputs.Add(inputs[0]!.DeepClone()); break;
            default: inputs[0]!["Unknown"] = true; break;
        }
        Assert.ThrowsAny<Exception>(() => WorkflowContract.Parse(node.ToJsonString()));
    }

    [Theory]
    [InlineData(typeof(BadInput), "is marked as an input but has no public setter")]
    [InlineData(typeof(BadOutput), "is marked as an output but has no public getter")]
    [InlineData(typeof(BadRequired), "has [Required] but is output-only")]
    [InlineData(typeof(BadConventionRequired), "has [Required] but is output-only")]
    [InlineData(typeof(BadDefaultSetter), "has [DefaultValue] but has no public setter")]
    [InlineData(typeof(BadDefault), "has a [DefaultValue] that cannot be converted to Int32")]
    [InlineData(typeof(BadTypedDefault), "has a [DefaultValue] that cannot be converted to Int32")]
    [InlineData(typeof(BadNullDefault), "has a [DefaultValue] that cannot be converted to Int32")]
    [Trait("Feature", "authoring")]
    public void InvalidDeclarationsFailEvenWithoutMappings(Type type, string message)
    {
        var definition = new WorkflowDefinition { Name = "Bad", Version = 1, WorkflowDataType = type };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains($"Workflow property '{type.Name}.Value' {message}", error.Message);
    }

    [Theory]
    [InlineData("Missing", true, "has no public property")]
    [InlineData("ReadOnly", true, "has no public setter")]
    [InlineData("WriteOnly", false, "has no public getter")]
    [InlineData("Out", true, "is marked output-only")]
    [InlineData("In", false, "is marked input-only")]
    [Trait("Feature", "authoring")]
    public void InvalidListsFailAtTheCalleeBuild(string member, bool input, string reason)
    {
        var definition = new WorkflowDefinition { Name = "BadList", Version = 1, WorkflowDataType = typeof(ListData),
            DeclaredInputs = input ? [member] : null, DeclaredOutputs = input ? null : [member] };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains(member, error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void DeclarationsRequireDirectPropertyAccessAndAreRootOnly()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Marked>("Call").Input(data => data.Text!.ToUpperInvariant()));
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Marked>("Nested").Output(data => data.Text!.Length));
        Assert.DoesNotContain(typeof(IWorkflowBuilder<Marked, Noop>).GetMethods(), method => method.Name is "Input" or "Output");
        Assert.DoesNotContain(typeof(ISequenceBuilder<Marked, Noop>).GetMethods(), method => method.Name is "Input" or "Output");
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void InvocationChecksMembershipRequiredInputsAndExplicitContractTypes()
    {
        Assert.Contains("is not an input", Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<Marked>("Child", 1,
            setup => setup.Input(data => data.Extra).From(_ => 1)).Build()).Message);
        Assert.Contains("is not an output", Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<Marked>("Child", 1,
            setup => setup.Output(data => data.Extra).To(ctx => ctx.WorkflowData.Number)).Build()).Message);
        Assert.Contains("required input", Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<OnlyInput>("Child", 1).Build()).Message);
        var effective = WorkflowDefinitionValidator.GetContract(Workflow.Create<Marked>("Child").Input(data => data.Extra).Build());
        var definition = Parent().Invoke<Marked>("Child", 1, effective,
            setup => setup.Input(data => data.Extra).From(_ => 1)).Build();
        var invocation = definition.Steps[^1];
        Assert.Same(effective, invocation.InvokedContract);
        Assert.Equal(effective.Hash, invocation.InvokedContractHash);
        Assert.Same(effective, invocation.Clone().InvokedContract);
        Assert.Equal(effective.Hash, invocation.Clone().InvokedContractHash);
        Assert.Equal(WorkflowInvocationContractKind.ExactPin, invocation.Clone().InvokedContractKind);
        var member = Assert.Single(WorkflowContract.OfType(typeof(Golden)).Inputs);
        Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<Golden>("Child", 1,
            new WorkflowContract([member with { Type = WorkflowTypeIdentity.SavedName(typeof(string)) }], [], true)).Build());
        Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<Golden>("Child", 1,
            new WorkflowContract([member with { Name = "Missing" }], [], true)).Build());
        Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<BadInput>("Child", 1,
            new WorkflowContract([member], [], true)).Build());
        Assert.Throws<WorkflowEngineException>(() => Parent().Invoke<BadOutput>("Child", 1,
            new WorkflowContract([], [member], true)).Build());
        Assert.Equal(WorkflowContract.OfType(typeof(Marked)).ToJson(), Parent().Invoke<Marked>("Child", 1).Build().Steps[^1].InvokedContract!.ToJson());
        Assert.Equal(WorkflowContract.OfType(typeof(Marked)).ToJson(), Parent().Invoke<TypedChild, Marked>().Build().Steps[^1].InvokedContract!.ToJson());
        Assert.Equal(WorkflowInvocationContractKind.CallerView, Parent().Invoke<TypedChild, Marked>().Build().Steps[^1].Clone().InvokedContractKind);
        Assert.Equal(WorkflowInvocationContractKind.CallerView, Parent().Invoke<Marked>("Child", 1).Build().Steps[^1].Clone().InvokedContractKind);
        Parent().Try(_ => { }).Catch(body => body.Invoke<Marked>("Child", 1, effective)).Build();
    }

    [Fact]
    [Trait("Feature", "children")]
    public void ChildConstructionUsesThePinAndMappingDefaultInitializerPrecedence()
    {
        var child = Workflow.Create<Marked>("Child").Build();
        var step = Parent().Invoke<Marked>("Child", 1).Build().Steps[^1];
        var data = Assert.IsType<Marked>(ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(7, data.Count);
        Assert.Equal("initializer", data.Text);
        step = Parent().Invoke<Marked>("Child", 1, setup => setup.Input(data => data.Count).From(_ => 11)).Build().Steps[^1];
        data = Assert.IsType<Marked>(ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(11, data.Count);
        // Construction always takes defaults from the resolved callee, after the executor's contract check.
        var pin = WorkflowContract.OfType(typeof(Marked));
        var count = pin.Inputs[0] with { DefaultJson = "19" };
        var explicitPin = new WorkflowContract([count, pin.Inputs[1]], [count, pin.Outputs[1]], true);
        step = Parent().Invoke<Marked>("Child", 1, explicitPin).Build().Steps[^1];
        Assert.Equal(7, Assert.IsType<Marked>(ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>())).Count);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task ChildDefaultsRunThroughTheHostWithAnEffectiveExplicitPin()
    {
        using var provider = Services();
        var child = Workflow.Create<Marked>("DefaultChild").Input(data => data.Extra).Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Invoke<Marked>(child.Name, 1, WorkflowDefinitionValidator.GetContract(child), setup => setup
            .Input(data => data.Extra).From(_ => 42)
            .Output(data => data.Count).To(ctx => ctx.WorkflowData.Number)).Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var result = await client.FinishedAsync(await client.RunAsync(parent, new ParentData()));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(7, Assert.IsType<ParentData>(result.WorkflowData).Number);
    }

    [Fact]
    [Trait("Feature", "children")]
    public void EmptyStringsFailAsWorkflowCodeAndConversionErrorsStayDefinitions()
    {
        var child = Workflow.Create<OnlyInput>("EmptyString").Build();
        var step = Parent().Invoke<OnlyInput>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => "")).Build().Steps[^1];
        var error = Assert.Throws<WorkflowEngineException>(() => ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Contains("InvalidInput", error.Message);
        Assert.Contains("OnlyInput.Value", error.Message);
        step = Parent().Invoke<Marked>("Conversion", 1).Build().Steps[^1];
        step.InputMappings.Add(new PropertyMapping { TargetProperty = "Count", Direction = PropertyMappingDirection.Input,
            SourceFunction = _ => "not a number" });
        error = Assert.Throws<WorkflowEngineException>(() => ChildInputs.NewData(Workflow.Create<Marked>("Conversion").Build(), step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    [Trait("Feature", "authoring")]
    public void EffectiveContractsAreCachedAfterApplyAndScopedToTheDefinition()
    {
        var definition = new WorkflowDefinition { Name = "BeforeApply", Version = 1, WorkflowDataType = typeof(Marked) };
        Assert.NotEmpty(WorkflowDefinitionValidator.GetContract(definition).Inputs);
        definition.DeclaredInputs = [];
        definition.DeclaredOutputs = [];
        WorkflowDefinitionValidator.Validate(definition);
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Empty(contract.Inputs);
        Assert.Same(contract, WorkflowDefinitionValidator.GetContract(definition));
        var other = Workflow.Create<Marked>(definition.Name).Build();
        Assert.NotEmpty(WorkflowDefinitionValidator.GetContract(other).Inputs);
        Assert.NotSame(contract, WorkflowDefinitionValidator.GetContract(other));
    }

    [Fact]
    [Trait("Feature", "children")]
    public void ChildDefaultsRoundTripEnumsAndDateTimeOffsetsThroughThePin()
    {
        var child = Workflow.Create<TypedDefaults>("TypedDefaults").Build();
        var step = Parent().Invoke<TypedDefaults>(child.Name, 1).Build().Steps[^1];
        var data = Assert.IsType<TypedDefaults>(ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(DayOfWeek.Friday, data.Day);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T12:34:56+05:30", System.Globalization.CultureInfo.InvariantCulture), data.When);
        Assert.Equal(TimeSpan.FromHours(5.5), data.When.Offset);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task EmptyCollectionsPassRequiredValidationForWorkflowsAndActivities()
    {
        using var provider = Services();
        var child = Workflow.Create<RootData>("Collections").Build();
        var step = Parent().Invoke<RootData>(child.Name, 1, setup => setup.Input(data => data.Items).From(_ => new List<int>())).Build().Steps[^1];
        Assert.Empty(Assert.IsType<RootData>(ChildInputs.NewData(child, step, new WorkflowContext<ParentData, Noop>())).Items);
        ActivityContract.ValidateRequired(new CollectionActivity(), new ActivityInputSnapshot(), provider);
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(child, new RootData { Value = "ok", Items = [] });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Empty(Assert.IsType<RootData>(result.WorkflowData).Items);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task RootStartsKeepInitializersAndNeverApplyAttributeDefaults()
    {
        using var provider = Services();
        var definition = Workflow.Create<RootInitializer>("Initializer").Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new RootInitializer());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("initializer", Assert.IsType<RootInitializer>(result.WorkflowData).Value);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task HostRootStartChecksTheRegisteredEffectiveContractIndependently()
    {
        using var provider = Services();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        var target = Workflow.Create<RootData>("HostRoot").Build();
        await catalog.RegisterWorkflowAsync(target);
        var supplied = Workflow.Create<RootData>(target.Name).Input().Output().Build();
        await using var renewer = new ExecutionLeaseRenewer(repository);
        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(),
            repository, catalog, new InProcessInstanceGate(), renewer);
        var id = Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.StartAsync(supplied,
            new RootData(), new WorkflowOptions { InstanceId = id }, CancellationToken.None));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Contains("RootData.Value", error.Message);
        Assert.Null(await repository.GetWorkflowInstanceAsync(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [Trait("Feature", "children")]
    public async Task InvalidChildInputIsCaughtByTheCaller(string? value)
    {
        using var provider = Services();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Workflow.Create<OnlyInput>("RequiredChild").Step<Noop>().Build());
        var parent = Parent().Try(body => body.Invoke<OnlyInput>("RequiredChild", 1, setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Text)))
            .Catch<WorkflowEngineException>(body => body.Step<Caught>()).Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(parent, new ParentData { Text = value });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<ParentData>(result.WorkflowData).Caught);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("name")]
    [InlineData("explicit")]
    [Trait("Feature", "children")]
    public async Task InvocationsRefuseMappingsOrPinsThatDifferFromTheEffectiveContract(string form)
    {
        using var provider = Services();
        var child = Workflow.Create<Marked>("TypedChild").Output().Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Try(body =>
        {
            if (form == "typed") body.Invoke<TypedChild, Marked>(setup => setup.Output(data => data.Result).To(ctx => ctx.WorkflowData.Number));
            else if (form == "name") body.Invoke<Marked>(child.Name, 1, setup => setup.Output(data => data.Result).To(ctx => ctx.WorkflowData.Number));
            else body.Invoke<Marked>(child.Name, 1, WorkflowContract.OfType(typeof(Marked)));
        }).Catch<Exception>(body => body.Step<Caught>()).Build();
        if (form == "explicit")
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowClient>()
                .RunAsync(parent, new ParentData()));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.Contains(WorkflowContract.OfType(typeof(Marked)).Hash, error.Message);
            Assert.Contains(WorkflowDefinitionValidator.GetContract(child).Hash, error.Message);
            await AssertInterruptedBeforeChild(provider, error);
        }
        else
        {
            var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());
            await AssertFaultedBeforeChild(provider, child, parent, result,
                "Child workflow output 'Marked.Result' is not an output in its workflow contract");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Feature", "children")]
    public async Task ExactPinsWithMissingOrDifferentlyTypedDefaultMembersCannotBeCaught(bool differentType)
    {
        using var provider = Services();
        var child = differentType ? Workflow.Create<StringDefault>("Drifted").Build() : Workflow.Create<NoMembers>("Drifted").Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Try(body => body.Invoke<Golden>(child.Name, 1, WorkflowContract.OfType(typeof(Golden))))
            .Catch<Exception>(body => body.Step<Caught>()).Build();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData()));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.Contains(WorkflowDefinitionValidator.GetContract(child).Hash, error.Message);
        Assert.Contains(WorkflowContract.OfType(typeof(Golden)).Hash, error.Message);
        await AssertInterruptedBeforeChild(provider, error);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task ByNameViewWithAMissingDefaultedMappedMemberCannotBeCaught()
    {
        using var provider = Services();
        var child = Workflow.Create<NoMembers>("MissingDefault").Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Try(body => body.Invoke<Golden>("MissingDefault", 1, setup => setup.Input(data => data.Value).From(_ => 3)))
            .Catch<Exception>(body => body.Step<Caught>()).Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());
        await AssertFaultedBeforeChild(provider, child, parent, result,
            "Child workflow input 'NoMembers.Value' is not a property of its data");
    }

    [Theory]
    [InlineData("input")]
    [InlineData("required")]
    [InlineData("output")]
    [Trait("Feature", "children")]
    public async Task CallerViewsCheckResolvedInputMembershipAndRequiredMappings(string mismatch)
    {
        using var provider = Services();
        var child = mismatch switch
        {
            "input" => Workflow.Create<Golden>("Resolved").Input().Build(),
            "output" => Workflow.Create<NoMembers>("Resolved").Build(),
            _ => Workflow.Create<OnlyInput>("Resolved").Build()
        };
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Try(body =>
        {
            if (mismatch == "input") body.Invoke<Golden>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => 1));
            else if (mismatch == "output") body.Invoke<AlternateDefaultView>(child.Name, 1, setup => setup.Output(data => data.Count).To(ctx => ctx.WorkflowData.Number));
            else body.Invoke<NoMembers>(child.Name, 1);
        }).Catch<Exception>(body => body.Step<Caught>()).Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());
        await AssertFaultedBeforeChild(provider, child, parent, result, mismatch switch
        {
            "input" => "Child workflow input 'Golden.Value' is not an input in its workflow contract",
            "output" => "Child workflow output 'NoMembers.Count' is not an output in its workflow contract",
            _ => "Child workflow input 'OnlyInput.Value' is a required input in its workflow contract and is not mapped"
        });
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task CallerViewMappingAnExcludedHiddenPropertyGetsTheNamedContractRefusal()
    {
        using var provider = Services();
        var child = Workflow.Create<HiddenProperty>("HiddenChild").Input(data => data.Other).Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = Parent().Try(body => body.Invoke<Golden>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => 1)))
            .Catch<Exception>(body => body.Step<Caught>()).Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());
        await AssertFaultedBeforeChild(provider, child, parent, result,
            "Child workflow input 'HiddenProperty.Value' is not an input in its workflow contract");
    }

    [Fact]
    [Trait("Feature", "children")]
    public void HiddenPropertyMappingsDefaultsAndRequiredChecksUseTheMostDerivedDeclaration()
    {
        var contract = new WorkflowContract(WorkflowContract.OfType(typeof(Golden)).Inputs
            .Select(member => member with { Required = true }).ToArray(), [], true);
        var mapped = Parent().Invoke<HiddenProperty>("Hidden", 1, contract,
            setup => setup.Input(data => data.Value).From(_ => 9)).Build().Steps[^1];
        var defaulted = Parent().Invoke<HiddenProperty>("Hidden", 1, contract).Build().Steps[^1];
        // Retain a validated contract while simulating a resolved data type with a hidden property.
        var definition = Workflow.Create<Golden>("Hidden").Build();
        definition.WorkflowDataType = typeof(HiddenProperty);
        var context = new WorkflowContext<ParentData, Noop>();
        var data = Assert.IsType<HiddenProperty>(ChildInputs.NewData(definition, defaulted, context));
        Assert.Equal(3, data.Value);
        Assert.Null(((BaseProperty)data).Value);
        contract.ValidateRequired(data, typeof(HiddenProperty));
        data = Assert.IsType<HiddenProperty>(ChildInputs.NewData(definition, mapped, context));
        Assert.Equal(9, data.Value);
        Assert.Null(((BaseProperty)data).Value);
        contract.ValidateRequired(data, typeof(HiddenProperty));
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("name")]
    [Trait("Feature", "children")]
    public async Task CallerViewsAcceptFluentListsAndUseTheCalleesDefaults(string form)
    {
        using var provider = Services();
        var child = Workflow.Create<Marked>("ListedChild").Input(data => data.Extra).Output(data => data.Extra).Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = form == "typed" ? Parent().Invoke<ListedChild, Marked>(setup => setup.Output(data => data.Count).To(ctx => ctx.WorkflowData.Number)).Build()
            : Parent().Invoke<AlternateDefaultView>(child.Name, 1, setup => setup.Output(data => data.Count).To(ctx => ctx.WorkflowData.Number)).Build();
        Assert.NotEqual(parent.Steps[^1].InvokedContractHash, WorkflowDefinitionValidator.GetContract(child).Hash);
        var client = provider.GetRequiredService<IWorkflowClient>();
        var result = await client.FinishedAsync(await client.RunAsync(parent, new ParentData()));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(7, Assert.IsType<ParentData>(result.WorkflowData).Number);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task AnUncaughtInvalidInputFailsTheParentAsWorkflowCode()
    {
        using var provider = Services();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Workflow.Create<OnlyInput>("UncaughtChild").Build());
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(Parent().Invoke<OnlyInput>("UncaughtChild", 1,
            setup => setup.Input(data => data.Value).From(_ => "")).Build(), new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(typeof(WorkflowEngineException).FullName, result.ErrorType);
        Assert.Contains("InvalidInput", result.ErrorMessage);
        var saved = (await provider.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId))!;
        Assert.Equal(WorkflowStatus.Failed, saved.Status);
        var error = ExecutionCheckpoint.Read(saved.ExecutionStateJson).UnhandledError!;
        Assert.Equal(WorkflowErrorKind.InvalidRequest, Assert.IsType<WorkflowEngineException>(error.ForPropagation()).Kind);
    }

    [Fact]
    [Trait("Feature", "children")]
    public async Task RootStartWithAnotherRegisteredDataTypeKeepsTheRegistrationRefusal()
    {
        using var provider = Services();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        await catalog.RegisterWorkflowAsync(Workflow.Create<OnlyInput>("OtherType").Build());
        var supplied = Workflow.Create<Golden>("OtherType").Build();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        await using var renewer = new ExecutionLeaseRenewer(repository);
        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), repository,
            catalog, new InProcessInstanceGate(), renewer);
        var id = Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.StartAsync(supplied, new Golden(), new() { InstanceId = id }, CancellationToken.None));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Contains("already registered with structure hash", error.Message);
        Assert.Null(await repository.GetWorkflowInstanceAsync(id));
    }

    [Theory]
    [InlineData("run", false)]
    [InlineData("start", false)]
    [InlineData("host", false)]
    [InlineData("run", true)]
    [InlineData("start", true)]
    [InlineData("host", true)]
    [Trait("Feature", "children")]
    public async Task DefinitionRootStartsRefuseIncompatibleDataBeforeSavingAnInstance(string path, bool registered)
    {
        using var provider = Services();
        var definition = Workflow.Create<OnlyInput>("WrongRootData").Build();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        if (registered) await catalog.RegisterWorkflowAsync(definition);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var options = new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") };
        var client = provider.GetRequiredService<IWorkflowClient>();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
        {
            if (path == "run") await client.RunAsync(definition, new NoMembers(), options);
            else if (path == "start") await client.StartAsync(definition, new NoMembers(), options);
            else
            {
                await using var renewer = new ExecutionLeaseRenewer(repository);
                var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), repository,
                    catalog, new InProcessInstanceGate(), renewer);
                await new WorkflowHost.SlotClient(operations).RunAsync(definition, new NoMembers(), options);
            }
        });
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Contains(typeof(NoMembers).FullName!, error.Message);
        Assert.Contains(typeof(OnlyInput).FullName!, error.Message);
        Assert.Null(await repository.GetWorkflowInstanceAsync(options.InstanceId));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("start")]
    [InlineData("host")]
    [Trait("Feature", "children")]
    public async Task DefinitionRootStartsAcceptInstancesOfDerivedDataTypes(string path)
    {
        using var provider = Services();
        var definition = Workflow.Create<BaseProperty>("DerivedRootData").Build();
        var data = new DerivedRootData();
        var client = provider.GetRequiredService<IWorkflowClient>();
        WorkflowExecutionResult result;
        if (path == "run") result = await client.RunAsync(definition, data);
        else if (path == "start") result = await client.AwaitAsync(await client.StartAsync(definition, data));
        else
        {
            var repository = provider.GetRequiredService<IWorkflowStateRepository>();
            await using var renewer = new ExecutionLeaseRenewer(repository);
            var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), repository,
                provider.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(), renewer);
            result = await new WorkflowHost.SlotClient(operations).RunAsync(definition, data);
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Fact]
    [Trait("Feature", "children")]
    public void MissingDefaultAndRequiredPropertiesProduceNamedDefinitionErrors()
    {
        var definition = Workflow.Create<Golden>("ChangedData").Build();
        definition.WorkflowDataType = typeof(NoMembers);
        var step = Parent().Invoke<Golden>(definition.Name, 1).Build().Steps[^1];
        var error = Assert.Throws<WorkflowEngineException>(() => ChildInputs.NewData(definition, step, new WorkflowContext<ParentData, Noop>()));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("NoMembers.Value' is not a property of its data", error.Message);
        error = Assert.Throws<WorkflowEngineException>(() => WorkflowContract.OfType(typeof(OnlyInput)).ValidateRequired(new NoMembers(), typeof(NoMembers)));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("NoMembers.Value' is not a property of its data", error.Message);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("hash")]
    [InlineData("missing")]
    [Trait("Feature", "children")]
    public async Task HostChecksTheCatalogContractWithOneRowRead(string variant)
    {
        using var provider = Services();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var store = DispatchProxy.Create<IWorkflowCatalogStore, CatalogReadProbe>();
        var probe = (CatalogReadProbe)store;
        probe.Inner = (IWorkflowCatalogStore)repository;
        using var catalog = new WorkflowCatalog(store);
        var parent = Parent().Build();
        var definition = Workflow.Create<Golden>("CatalogChild").Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(parent);
        await catalog.RegisterWorkflowAsync(parent);
        await catalog.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(parent, new ParentData(), new(), DateTime.UtcNow);
        instance.ExecutionStateJson = WorkflowJson.Serialize(checkpoint);
        await repository.SeedAsync(instance);
        instance = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
        await using var watch = new CancellationWatch(repository, TimeProvider.System);
        await using var renewer = new ExecutionLeaseRenewer(repository);
        await using var owner = (await WorkflowExecutionLease.TryAcquireAsync(repository, instance,
            new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)), watch, renewer, await StoreTestSupport.ClaimAsync(provider),
            _ => { }, _ => { }, _ => { }, TimeProvider.System))!;
        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), repository,
            catalog, new InProcessInstanceGate(), renewer);
        var identity = Guid.NewGuid();
        using var admission = operations.Admit(instance, owner, identity);
        var child = WorkflowInstances.Start(definition, new Golden(), instance, "catalog-child", "wait", DateTime.UtcNow);
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        probe.Reads = 0;
        probe.Missing = variant == "missing";
        var reply = await new WorkflowHost.InstanceOperations.ExecutionHost(operations).StartChildAsync(new(identity, instance.InstanceId,
            owner.Epoch, "root/1", child, WorkflowDefinitionValidator.ForExecution(definition).StructureHash,
            variant == "hash" ? "executor-hash" : contract.Hash));
        Assert.Equal(1, probe.Reads);
        if (variant == "accepted") Assert.Equal(child, reply.Start);
        else
        {
            Assert.Null(reply.Start);
            Assert.Equal(variant == "hash" ? WorkflowErrorKind.ArtifactMismatch : WorkflowErrorKind.ArtifactUnavailable, reply.Refusal);
            if (variant == "hash")
            {
                Assert.Contains(contract.Hash, reply.Message);
                Assert.Contains("executor-hash", reply.Message);
            }
            else Assert.Contains("unavailable for new starts (missing)", reply.Message);
        }
        Assert.Null(await repository.GetWorkflowInstanceAsync(child.ChildInstanceId));
    }

    private static async Task AssertFaultedBeforeChild(ServiceProvider provider, WorkflowDefinition child, WorkflowDefinition parent,
        WorkflowExecutionResult result, string message)
    {
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains(message, result.ErrorMessage);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var instance = (await repository.GetWorkflowInstanceAsync(result.InstanceId))!;
        Assert.Equal(WorkflowStatus.Failed, instance.Status);
        Assert.False(WorkflowJson.Deserialize<ParentData>(instance.WorkflowDataJson.InlineText)!.Caught);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(WorkflowEngineException)), ExecutionCheckpoint.Read(instance.ExecutionStateJson).UnhandledError!.Type);
        var step = Assert.Single(WorkflowDefinitionValidator.ForExecution(parent).AllSteps.Select(pair => pair.Value),
            step => step.StepType == WorkflowStepType.WorkflowInvocation);
        var error = Assert.Throws<WorkflowEngineException>(() => ChildInputs.CheckContract(child, step, WorkflowDefinitionValidator.GetContract(child)));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Equal(message, error.Message);
        Assert.False(error.IsInterruption);
        Assert.Empty((await repository.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: instance.InstanceId))).Items);
    }

    private static async Task AssertInterruptedBeforeChild(ServiceProvider provider, WorkflowEngineException error)
    {
        Assert.True(error.IsInterruption);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var instance = (await repository.GetWorkflowInstanceAsync(error.InstanceId!))!;
        Assert.Equal(WorkflowStatus.Running, instance.Status);
        Assert.False(WorkflowJson.Deserialize<ParentData>(instance.WorkflowDataJson.InlineText)!.Caught);
        Assert.NotEmpty(ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations);
        Assert.Empty((await repository.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: instance.InstanceId))).Items);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("definition")]
    [InlineData("run")]
    [InlineData("host")]
    [Trait("Feature", "children")]
    public async Task NullRootDataIsAnArgumentError(string path)
    {
        using var provider = Services();
        var child = Workflow.Create<RootData>("NullRoot").Build();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        await catalog.RegisterWorkflowAsync(child);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var id = Guid.NewGuid().ToString("N");
        var options = new WorkflowOptions { InstanceId = id };
        var error = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            switch (path)
            {
                case "name": await client.StartAsync<RootData>(child.Name, null!, options); break;
                case "version": await client.StartAsync<RootData>(child.Name, 1, null!, options); break;
                case "definition": await client.StartAsync<RootData>(child, null!, options); break;
                case "run": await client.RunAsync<RootData>(child, null!, options); break;
                default:
                    await using (var renewer = new ExecutionLeaseRenewer(repository))
                    {
                        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(),
                            repository, catalog, new InProcessInstanceGate(), renewer);
                        await operations.StartAsync<RootData>(child, null!, options, CancellationToken.None);
                    }
                    break;
            }
        });
        Assert.Equal("workflowData", error.ParamName);
        Assert.Null(await repository.GetWorkflowInstanceAsync(id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("text")]
    [Trait("Feature", "children")]
    public async Task RootStartsRejectRequiredValuesWithoutCreatingAnInstance(string missing)
    {
        using var provider = Services();
        var child = Workflow.Create<RootData>("RootRequired").Step<Noop>().Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var data = new RootData { Value = missing == "null" ? null : "" };
        var client = provider.GetRequiredService<IWorkflowClient>();
        foreach (var path in new[] { "name", "version", "definition", "run" })
        {
            var id = Guid.NewGuid().ToString("N");
            var options = new WorkflowOptions { InstanceId = id };
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
            {
                switch (path)
                {
                    case "name": await client.StartAsync(child.Name, data!, options); break;
                    case "version": await client.StartAsync(child.Name, 1, data!, options); break;
                    case "definition": await client.StartAsync(child, data!, options); break;
                    default: await client.RunAsync(child, data!, options); break;
                }
            });
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Contains("RootData.Value", error.Message);
            Assert.Null(await provider.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(id));
        }
    }

    [Theory]
    [InlineData("run", false)]
    [InlineData("start", false)]
    [InlineData("host", false)]
    [InlineData("run", true)]
    [InlineData("start", true)]
    [InlineData("host", true)]
    [Trait("Feature", "children")]
    public async Task RootRequiredInputsUseTheDefinitionsMemberWhenDerivedDataHidesIt(string path, bool fillBase)
    {
        using var provider = Services();
        var definition = Workflow.Create<RequiredBaseData>("RequiredBase").Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var derived = new HidingRequiredData { Value = fillBase ? null : "derived" };
        RequiredBaseData data = derived;
        data.Value = fillBase ? "base" : null;
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var options = new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") };
        async Task<WorkflowExecutionResult> Start()
        {
            if (path == "run") return await client.RunAsync(definition, data, options);
            if (path == "start") return await client.AwaitAsync(await client.StartAsync(definition, data, options));
            await using var renewer = new ExecutionLeaseRenewer(repository);
            var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), repository,
                provider.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(), renewer);
            return await new WorkflowHost.SlotClient(operations).RunAsync(definition, data, options);
        }
        if (fillBase)
        {
            Assert.Equal(WorkflowExecutionStatus.Success, (await Start()).Status);
            Assert.NotNull(await repository.GetWorkflowInstanceAsync(options.InstanceId));
        }
        else
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(Start);
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Contains("RequiredBaseData.Value", error.Message);
            Assert.Null(await repository.GetWorkflowInstanceAsync(options.InstanceId));
        }
        Assert.Equal(fillBase ? "base" : null, data.Value);
        Assert.Equal(fillBase ? null : "derived", derived.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Feature", "authoring")]
    public void ActivityAndEventMappingsKeepMainsAmbiguousPropertyRefusal(bool activity, bool output)
    {
        Assert.Throws<AmbiguousMatchException>(() =>
        {
            if (activity)
                Parent().Step<HiddenMappingActivity>(setup =>
                {
                    if (output) setup.Output(data => data.Value).To(ctx => ctx.WorkflowData.Number);
                    else setup.Input(data => data.Value).From(_ => 7);
                }).Build();
            else
                Parent().WaitFor<HiddenProperty>("review", configure: setup =>
                {
                    if (output) setup.Output(data => data.Value).To(ctx => ctx.WorkflowData.Number);
                    else setup.Input(data => data.Value).From(_ => 7);
                }).Build();
        });
    }

    [Fact]
    [Trait("Feature", "waits")]
    public async Task ActivityAndEventMappingsExecuteWithTheirExistingPropertyLookup()
    {
        using var provider = Services();
        var definition = Parent().Step<MappingActivity>(setup => setup
                .Input(data => data.Value).From(_ => 3)
                .Output(data => data.Value).To(ctx => ctx.WorkflowData.Number))
            .WaitFor<MappingEvent>("review", configure: setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Number)
                .Output(data => data.Value).To(ctx => ctx.WorkflowData.Number)).Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(definition, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(3, Assert.IsType<ParentData>(started.WorkflowData).Number);
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        Assert.Equal(3, WorkflowJson.Deserialize<MappingEvent>(wait.DefaultPayloadJson!)!.Value);
        var finished = await client.ReplyAsync(wait.Token, new MappingEvent { Value = 7 });
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(7, Assert.IsType<ParentData>(finished.WorkflowData).Number);
    }

    public class RequiredBaseData { [WorkflowInput, Required] public string? Value { get; set; } }
    public sealed class HidingRequiredData : RequiredBaseData { public new string? Value { get; set; } }
    public class MappingActivity : Noop { [ActivityInputOutput] public int Value { get; set; } }
    public class BaseMappingActivity : Noop { public string? Value { get; set; } }
    public sealed class HiddenMappingActivity : BaseMappingActivity { public new int Value { get; set; } }
    public sealed class MappingEvent { public int Value { get; set; } }

    private static IWorkflowBuilder<ParentData, Noop> Parent() => Workflow.Create<ParentData>("Parent").Step<Noop>();
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        DiagnosticTestAccess.Allow(services);
        services.AddIxIFlow();
        services.RegisterWorkflow<TypedChild>();
        services.RegisterWorkflow<ListedChild>();
        return services.BuildServiceProvider();
    }

    public class Noop : IAsyncActivity { public virtual Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class Caught : Noop
    {
        public override Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((ParentData)context.WorkflowData!).Caught = true; return Task.CompletedTask; }
    }
    public sealed class ParentData { public int Number { get; set; } public string? Text { get; set; } public bool Caught { get; set; } }
    public sealed class Golden { [WorkflowInput, DefaultValue(3)] public int Value { get; set; } }
    public sealed class NoMembers { }
    public class CatalogReadProbe : DispatchProxy
    {
        public IWorkflowCatalogStore Inner = null!;
        public int Reads;
        public bool Missing;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowCatalogStore.GetWorkflowVersionAsync))
            {
                Reads++;
                if (Missing) return Task.FromResult<WorkflowVersion?>(null);
            }
            return method.Invoke(Inner, args);
        }
    }
    public sealed class StringDefault { [WorkflowInput, DefaultValue("text")] public string? Value { get; set; } }
    public sealed class AlternateDefaultView { [WorkflowInputOutput, DefaultValue(99)] public int Count { get; set; } }
    public sealed class LocalizedDisplay
    {
        [WorkflowInput, Display(Name = nameof(DisplayResources.Label), Description = nameof(DisplayResources.Description), ResourceType = typeof(DisplayResources))]
        public int Value { get; set; }
    }
    public static class DisplayResources
    {
        public static string Label => CultureInfo.CurrentUICulture.Name.Length == 0 ? "invariant label" : CultureInfo.CurrentUICulture.Name + " label";
        public static string Description => CultureInfo.CurrentUICulture.Name.Length == 0 ? "invariant description" : CultureInfo.CurrentUICulture.Name + " description";
    }
    public sealed class CultureDefaults
    {
        [WorkflowInput, DefaultValue(1.25)] public double Number { get; set; }
        [WorkflowInput, DefaultValue(typeof(DateTimeOffset), "2026-10-20T12:34:56+05:30")] public DateTimeOffset When { get; set; }
    }
    public class IndexedData { public int Value { get; set; } public string this[string key] { get => key; set { } } }
    public sealed class MarkedIndexer { [WorkflowInputOutput] public string this[string key] { get => key; set { } } }
    public class BaseProperty { public string? Value { get; set; } }
    public sealed class DerivedRootData : BaseProperty { }
    public sealed class HiddenProperty : BaseProperty { public new int Value { get; set; } public int Other { get; set; } }
    public sealed class OnlyInput { [WorkflowInput, Required] public string? Value { get; set; } }
    public sealed class RootData
    {
        [WorkflowInput, Required, DefaultValue("child-only")] public string? Value { get; set; }
        [WorkflowInput, Required] public List<int> Items { get; set; } = [1];
    }
    public sealed class RootInitializer { [WorkflowInput, Required, DefaultValue("attribute")] public string Value { get; set; } = "initializer"; }
    public sealed class CollectionActivity : Noop { [ActivityInput, Required] public List<int> Items { get; set; } = []; }
    public sealed class TypedDefaults
    {
        [WorkflowInput, DefaultValue(DayOfWeek.Friday)] public DayOfWeek Day { get; set; }
        [WorkflowInput, DefaultValue(typeof(DateTimeOffset), "2026-10-20T12:34:56+05:30")] public DateTimeOffset When { get; set; }
    }
    public sealed class Marked
    {
        [WorkflowInputOutput, Required, DefaultValue("7"), Display(Name = "Count label", Description = "Count help"), UIHint("code")]
        public int Count { get; set; } = 2;
        [WorkflowInput] public string? Text { get; set; } = "initializer";
        [WorkflowOutput] public int Result { get; set; }
        public int Extra { get; set; }
    }
    public sealed class Plain { [Required, DefaultValue("5"), Display(Name = "plain")] public int Value { get; set; } public int ReadOnly => 1; }
    public sealed class ListData
    {
        public int ReadOnly => 1;
        public int WriteOnly { set { } }
        [WorkflowInput] public int In { get; set; }
        [WorkflowOutput] public int Out { get; set; }
    }
    public sealed class BadInput { [WorkflowInput] public int Value { get; private set; } }
    public sealed class BadOutput { [WorkflowOutput] public int Value { private get; set; } }
    public sealed class BadRequired { [WorkflowOutput, Required] public int Value { get; set; } }
    public sealed class BadConventionRequired { [Required] public int Value => 1; }
    public sealed class BadDefaultSetter { [DefaultValue(1)] public int Value { get; private set; } }
    public sealed class BadDefault { [DefaultValue("bad")] public int Value { get; set; } }
    public sealed class BadTypedDefault { [DefaultValue(typeof(int), "bad")] public int Value { get; set; } }
    public sealed class BadNullDefault { [DefaultValue(null)] public int Value { get; set; } }
    [Workflow("TypedChild")]
    public sealed class TypedChild : IWorkflow<Marked> { public void Build(IWorkflowBuilder<Marked> builder) => builder.Output(); }
    [Workflow("ListedChild")]
    public sealed class ListedChild : IWorkflow<Marked>
    { public void Build(IWorkflowBuilder<Marked> builder) => builder.Input(data => data.Extra).Output(data => data.Extra); }
}
