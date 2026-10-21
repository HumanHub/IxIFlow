using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Builders.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace IxIFlow.Tests.ExecutionTests.StructureHashFixtures;

[Trait("Spec", "F-03")]
[Trait("Feature", "registration")]
public sealed class DeclaredStructureHashTests
{
    [Fact]
    public void TypedChildIdentityChangesTheParentHashWithTheSameClrTypes()
    {
        const string source = """
            using IxIFlow.Builders;
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            using IxIFlow.Tests.ExecutionTests.StructureHashFixtures;
            [Workflow("Declared child", Version = 3)]
            public sealed class Child : IWorkflow<StructureHashData>
            {
                public Child() => throw new System.InvalidOperationException("Must not construct for identity");
                public void Build(IWorkflowBuilder<StructureHashData> builder) => builder.Step<StructureHashMultiply>();
            }
            public static class Definition
            {
                public static WorkflowDefinition Build() => Workflow.Create<StructureHashData>("Parent")
                    .Step<StructureHashMultiply>().Invoke<Child, StructureHashData>().Build();
            }
            """;
        using var original = new Compiled(source);
        using var version = new Compiled(source.Replace("Version = 3", "Version = 4"));
        using var name = new Compiled(source.Replace("Declared child", "Renamed child"));
        Assert.Equal(3, original.Definition.Steps[1].WorkflowVersion);
        Assert.Equal(4, version.Definition.Steps[1].WorkflowVersion);
        Assert.NotEqual(Hash(original.Definition), Hash(version.Definition));
        Assert.NotEqual(Hash(original.Definition), Hash(name.Definition));
    }

    [Fact]
    public void UndeclaredOrInvalidClassIdentityIsRefusedBeforeConstruction()
    {
        Refused<UndeclaredChild>();
        Refused<BlankNameChild>();
        Refused<InvalidVersionChild>();

        static void Refused<TWorkflow>() where TWorkflow : class, IWorkflow<StructureHashData>
        {
            var services = new ServiceCollection();
            AssertIdentityError(() => services.RegisterWorkflow<TWorkflow>());
            AssertIdentityError(() => services.RegisterWorkflow<TWorkflow, StructureHashData>());
            AssertIdentityError(() => Workflow.Create<StructureHashData>("Parent")
                .Step<StructureHashMultiply>().Invoke<TWorkflow, StructureHashData>());
        }

        static void AssertIdentityError(Action action)
        {
            var error = Assert.Throws<WorkflowEngineException>(action);
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.Contains("Workflow", error.Message);
            Assert.Contains("requires", error.Message);
        }
    }

    public class UndeclaredChild : IWorkflow<StructureHashData>
    {
        public UndeclaredChild() => throw new InvalidOperationException("Must not construct for identity");
        public void Build(IWorkflowBuilder<StructureHashData> builder) => throw new InvalidOperationException();
    }

    [Workflow(" ", Version = 1)]
    public sealed class BlankNameChild : UndeclaredChild;

    [Workflow("Invalid", Version = 0)]
    public sealed class InvalidVersionChild : UndeclaredChild;

    [Fact]
    public void DebugAndReleaseHaveEqualHashes()
    {
        using var debug = new Compiled(Source, OptimizationLevel.Debug);
        using var release = new Compiled(Source, OptimizationLevel.Release);
        Assert.Equal(Hash(debug.Definition), Hash(release.Definition));
    }

    [Fact]
    public void LanguageVersionsHaveEqualHashes()
    {
        using var first = new Compiled(Source, language: LanguageVersion.CSharp13);
        using var second = new Compiled(Source, language: LanguageVersion.CSharp14);
        Assert.Equal(Hash(first.Definition), Hash(second.Definition));
    }

    [Fact]
    public void DelegateBodyAndHelperEditsKeepTheHash()
    {
        using var first = new Compiled(Source);
        using var second = new Compiled(Source.Replace("return value > limit;", "return value < limit;"));
        using var helper = new Compiled(Source.Replace("value + 1", "value + 2"));
        Assert.Equal(Hash(first.Definition), Hash(second.Definition));
        Assert.Equal(Hash(first.Definition), Hash(helper.Definition));
    }

    [Fact]
    public void ClosureLayoutParameterNamesAndCapturedValuesKeepTheHash()
    {
        using var first = new Compiled(Source);
        using var second = new Compiled(Source.Replace("int limit = 7", "int limit = 70")
            .Replace("// additional closure", "Func<int, int> unrelated = n => n * limit;")
            .Replace("ctx => ctx.WorkflowData.Value + limit", "renamed => renamed.WorkflowData.Value + limit"));
        Assert.Equal(Hash(first.Definition), Hash(second.Definition));
    }

    [Fact]
    public void DataDeclaredCombinersChangeTheHashWithoutChangingTypeIdentity()
    {
        var source = Source.Replace("StructureHashData", "LocalData") + """

            public sealed class LocalData
            {
                [Combine<FirstWins<int>>]
                public int Value { get; set; }
            }
            """;
        using var first = new Compiled(source);
        using var second = new Compiled(source.Replace("FirstWins<int>", "LastWins<int>"));
        Assert.Equal(WorkflowTypeIdentity.StableName(first.Definition.WorkflowDataType),
            WorkflowTypeIdentity.StableName(second.Definition.WorkflowDataType));
        Assert.NotEqual(Hash(first.Definition), Hash(second.Definition));
    }

    [Fact]
    public void EmptyBranchesStillContributeToTopology()
    {
        var first = Workflow.Create<StructureHashData>("Topology").Step<StructureHashMultiply>(step => step
            .Input(activity => activity.Value).From(ctx => 1)).Parallel(parallel => parallel.Do(_ => { })).Build();
        var second = Workflow.Create<StructureHashData>("Topology").Step<StructureHashMultiply>(step => step
            .Input(activity => activity.Value).From(ctx => 1)).Parallel(parallel => parallel.Do(_ => { }).Do(_ => { })).Build();
        Assert.NotEqual(Hash(first), Hash(second));
    }

    [Fact]
    public async Task RegistrationUsesIdentityAndStructureAndKeepsTheFirstMetadata()
    {
        var registry = new WorkflowCatalog();
        var first = Waiting();
        first.Description = "Original description";
        var reworded = Waiting();
        reworded.Description = "New description";
        reworded.Steps[0].Name = "New label";
        await registry.RegisterWorkflowAsync(first);
        await registry.RegisterWorkflowAsync(reworded);
        Assert.Same(first, await registry.GetWorkflowDefinitionAsync(first.Name, first.Version));
        var changed = Waiting(insert: true);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => registry.RegisterWorkflowAsync(changed));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        AssertMismatch(error.Message, first, changed);
        changed.Version++;
        await registry.RegisterWorkflowAsync(changed);
        Assert.Same(changed, await registry.GetWorkflowDefinitionAsync(changed.Name, changed.Version));
    }

    [Fact]
    public async Task TypedChildrenPinTheirIdentityAndMatchTheRegistryHash()
    {
        WorkflowDefinition Parent() => Workflow.Create<StructureHashData>("Parent", 1)
            .Step<StructureHashMultiply>().Invoke<TypedChild, StructureHashData>().Build();
        var parent = Parent();
        Assert.Equal("Pinned child", parent.Steps[1].WorkflowName);
        Assert.Equal(3, parent.Steps[1].WorkflowVersion);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.RegisterWorkflow<TypedChild>();
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<IWorkflowCatalog>();
        var client = provider.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.RunToEndAsync(parent, new StructureHashData())).Status);
        var child = Assert.Single((await provider.GetRequiredService<IWorkflowStateRepository>()
            .QueryInstancesAsync(new(WorkflowName: "Pinned child", Limit: int.MaxValue))).Items);
        Assert.Equal(3, child.WorkflowVersion);
        var wrongParent = Parent();
        wrongParent.Name = "Wrong version parent";
        wrongParent.Steps[1].WorkflowVersion = 4;
        var wrongVersion = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            client.RunAsync(wrongParent, new StructureHashData()));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, wrongVersion.Kind);
        Assert.Contains("v3", wrongVersion.Message);
        Assert.Contains("v4", wrongVersion.Message);

        var replacement = Workflow.Create<StructureHashData>("Pinned child", 3).Step<StructureHashMultiply>().Step<StructureHashMultiply>().Build();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(replacement);
        services.AddSingleton<IWorkflowCatalog>(registry);
        using var mismatchedProvider = services.BuildServiceProvider();
        var mismatch = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            mismatchedProvider.GetRequiredService<IWorkflowClient>().RunAsync(Parent(), new StructureHashData()));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, mismatch.Kind);
        var original = (await registered.GetWorkflowDefinitionAsync("Pinned child", 3))!;
        Assert.Contains("differs from its registered definition: registered structure hash", mismatch.Message);
        AssertMismatch(mismatch.Message, original, replacement);
    }

    [Fact]
    public async Task RegistrationRemembersTheHashBeforeTheDefinitionWasMutated()
    {
        var registry = new WorkflowCatalog();
        var definition = Waiting();
        var savedHash = Hash(definition);
        await registry.RegisterWorkflowAsync(definition);
        definition.Steps[1].StepMetadata["WaitKey"] = "changed";
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => registry.RegisterWorkflowAsync(definition));
        Assert.Contains(savedHash, error.Message);
        Assert.Contains(Hash(definition), error.Message);
    }

    [IxIFlow.Core.Workflow("Pinned child", Version = 3)]
    public sealed class TypedChild : IWorkflow<StructureHashData>
    {
        public void Build(IWorkflowBuilder<StructureHashData> builder) => builder.Step<StructureHashMultiply>();
    }

    [Theory]
    [InlineData(false, "recover")]
    [InlineData(true, "recover")]
    [InlineData(false, "recover-idle")]
    [InlineData(true, "recover-idle")]
    [InlineData(false, "resume")]
    [InlineData(true, "resume")]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    public async Task PositionalChangesAreRefusedBeforeExecution(bool insert, string operation)
    {
        var store = new InMemoryWorkflowStateRepository();
        var original = Waiting();
        var changed = insert ? Waiting(insert: true) : Waiting(reverse: true);
        Assert.NotEqual(Hash(original), Hash(changed));
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string id;
        using (var first = Services(store))
        {
            var started = await first.GetRequiredService<IWorkflowClient>().RunAsync(original, new StructureHashData(),
                cancellationToken: patience.Token);
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            id = started.InstanceId;
        }
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        var noExecution = new Mock<IActivityExecutor>(MockBehavior.Strict);
        // This case models a host loading different code, independently of the durable catalog
        // rejection covered below. A host-local catalog supplies that capability snapshot.
        var secondServices = new ServiceCollection().AddLogging();
        secondServices.AddSingleton<IWorkflowStateRepository>(store);
        secondServices.AddIxIFlow();
        secondServices.AddSingleton<IWorkflowCatalog>(new WorkflowCatalog());
        secondServices.AddSingleton<IActivityExecutor>(noExecution.Object);
        using var second = secondServices.BuildServiceProvider();
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(changed);
        var client = second.GetRequiredService<IWorkflowClient>();
        if (operation == "start")
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(changed,
                new StructureHashData(), new WorkflowOptions { InstanceId = id }, patience.Token));
            AssertMismatch(error.Message, original, changed);
        }
        using (var scope = second.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
            WorkflowExecutionResult result;
            if (operation == "resume")
                result = await operations.ResumeAsync(id, "go", new StructureHashAnswer(), patience.Token, "ready")
                    .WaitAsync(patience.Token);
            else
            {
                if (operation == "recover")
                    await store.AppendEventAsync(new WorkflowEvent(id, "ready", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(StructureHashAnswer)), "{}", "go"));
                result = await client.RecoverAsync(id, patience.Token).WaitAsync(patience.Token);
            }
            Assert.Equal(WorkflowExecutionStatus.Scheduled, result.Status);
            if (operation is "resume" or "recover")
                Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(id, "ready"))!.Outcome);
        }
        noExecution.VerifyNoOtherCalls();
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(Hash(original), ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).StructureHash);
        Assert.Equal(WorkflowStatus.Suspended, saved.Status);
        Assert.Equal(before.ExecutionStateJson, saved.ExecutionStateJson);
        Assert.Equal(before.WorkflowDataJson, saved.WorkflowDataJson);
        Assert.Equal(before.Revision, saved.Revision);
        Assert.Equal(before.LastHostId, saved.LastHostId);

        using var correct = Services(store);
        await correct.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(original);
        var completed = await correct.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(id, "go", new StructureHashAnswer(), "ready", patience.Token).WaitAsync(patience.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(id, "ready"))!.Outcome);
    }

    [Theory]
    [InlineData(false, "recover")]
    [InlineData(true, "recover")]
    [InlineData(false, "recover-idle")]
    [InlineData(true, "recover-idle")]
    [InlineData(false, "resume")]
    [InlineData(true, "resume")]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    public async Task CatalogPositionalChangesAreRefusedBeforeExecution(bool insert, string operation)
    {
        var store = new InMemoryWorkflowStateRepository();
        var seedStore = new InMemoryWorkflowStateRepository();
        var original = Waiting();
        var changed = insert ? Waiting(insert: true) : Waiting(reverse: true);
        Assert.NotEqual(Hash(original), Hash(changed));
        string id;
        using (var first = Services(seedStore))
        {
            var started = await first.GetRequiredService<IWorkflowClient>().RunAsync(original, new StructureHashData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            id = started.InstanceId;
        }
        await store.RegisterWorkflowVersionAsync((await seedStore.GetWorkflowVersionAsync(original.Name, original.Version))!);
        using var second = Services(store);
        var catalog = second.GetRequiredService<IWorkflowCatalog>();
        var registration = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(changed));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, registration.Kind);
        AssertMismatch(registration.Message, original, changed);
        await catalog.RegisterWorkflowAsync(original);

        // Registration is immutable; exercise the execution safety net with a mismatched saved hash.
        var instance = (await seedStore.GetWorkflowInstanceAsync(id))!;
        // Import the corrupt snapshot as a first fixture commit; an idle row cannot be reclaimed
        // merely to edit its checkpoint under the filtered lease API.
        instance.Revision = 0;
        var checkpoint = JsonNode.Parse(instance.ExecutionStateJson)!;
        checkpoint["StructureHash"] = Hash(changed);
        instance.ExecutionStateJson = checkpoint.ToJsonString();
        var sourceWaits = await seedStore.GetOpenWaitsAsync(new WaitQuery(InstanceId: id));
        var owner = await StoreTestSupport.LeaseAsync(store, id);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, owner.Epoch, 0, openedWaits: sourceWaits.Select(wait =>
                new OpenedWait(wait.WaitId, wait.Kind, wait.Key, wait.EventType, null, wait.DefaultPayloadJson)).ToArray()))).Status);
        await StoreTestSupport.ReleaseAsync(store, id);
        var checkpointJson = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
        var client = second.GetRequiredService<IWorkflowClient>();
        if (operation == "start")
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(original,
                new StructureHashData(), new WorkflowOptions { InstanceId = id }));
            AssertMismatch(error.Message, original, changed);
        }
        else
        {
            WorkflowExecutionResult result;
            if (operation == "resume")
                result = await client.ResumeAsync(id, "go", new StructureHashAnswer());
            else
            {
                if (operation == "recover")
                    await store.AppendEventAsync(new WorkflowEvent(id, "ready", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(StructureHashAnswer)), "{}", "go"));
                result = await client.RecoverAsync(id);
            }
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            AssertMismatch(result.ErrorMessage!, original, changed);
        }
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(Hash(changed), ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).StructureHash);
        Assert.Equal(checkpointJson, saved.ExecutionStateJson);
        Assert.Equal(WorkflowStatus.Suspended, saved.Status);
    }

    [Fact]
    public void OldCheckpointSchemaIsRefusedWithoutMigration()
    {
        var current = JsonSerializer.Serialize(new ExecutionCheckpoint { StructureHash = "current" });
        var old = current.Replace("\"SchemaVersion\":4", "\"SchemaVersion\":3")
            .Replace("\"StructureHash\"", "\"DefinitionFingerprint\"");
        foreach (var read in new Func<string, ExecutionCheckpoint>[] { ExecutionCheckpoint.ReadTrunk, ExecutionCheckpoint.Read })
        {
            var error = Assert.Throws<WorkflowEngineException>(() => read(old));
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.Contains("version '3'", error.Message);
            Assert.Contains("expected 4", error.Message);
            Assert.Contains("no migration", error.Message);
        }
        Assert.Equal(4, ExecutionCheckpoint.ReadTrunk(current).SchemaVersion);
    }

    private static WorkflowDefinition Waiting(bool insert = false, bool reverse = false)
    {
        var builder = Workflow.Create<StructureHashData>("Positions", 3);
        var steps = builder.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1));
        if (insert) steps = steps.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 2));
        var definition = steps.WaitFor<StructureHashAnswer>("go").Step<StructureHashMultiply>(step => step
            .Input(activity => activity.Value).From(ctx => 3)).Build();
        if (reverse) (definition.Steps[0], definition.Steps[1]) = (definition.Steps[1], definition.Steps[0]);
        return definition;
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IActivityExecutor? executor = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        if (executor != null) services.AddSingleton(executor);
        return services.BuildServiceProvider();
    }

    private static void AssertMismatch(string message, WorkflowDefinition saved, WorkflowDefinition loaded)
    {
        Assert.Contains(saved.Name, message);
        Assert.Contains("v" + saved.Version, message);
        Assert.Contains(Hash(saved), message);
        Assert.Contains(Hash(loaded), message);
    }

    private static string Hash(WorkflowDefinition definition) => new WorkflowScopeCatalog(definition).StructureHash;

    private sealed class Compiled : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("structure-hash", isCollectible: true);
        public WorkflowDefinition Definition { get; }

        public Compiled(string source, OptimizationLevel optimization = OptimizationLevel.Release,
            LanguageVersion language = LanguageVersion.CSharp14)
        {
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                    typeof(StructureHashData).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
            var compilation = CSharpCompilation.Create("StructureHashFixture",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(language))],
                paths.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            stream.Position = 0;
            var assembly = _context.LoadFromStream(stream);
            Definition = (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build")?.IsStatic == true)
                .GetMethod("Build")!.Invoke(null, null)!;
        }

        public void Dispose() => _context.Unload();
    }

    private const string Source = """
        using System;
        using IxIFlow.Builders;
        using IxIFlow.Core;
        using IxIFlow.Tests.ExecutionTests.StructureHashFixtures;
        public static class Definition
        {
            public static WorkflowDefinition Build()
            {
                int limit = 7;
                // additional closure
                return Workflow.Create<StructureHashData>("Compiler independent", 3)
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value + limit))
                    .WhileDo((Func<WorkflowContext<StructureHashData, StructureHashMultiply>, bool>)(ctx => { int value = Adjust(ctx.WorkflowData.Value); return value > limit; }),
                        body => body.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)))
                    .Build();
            }
            private static int Adjust(int value) => value + 1;
        }
        """;
}
