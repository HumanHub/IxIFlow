using System.Linq.Expressions;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests.StructureHashFixtures;

[Trait("Spec", "F-03")]
[Trait("Feature", "registration")]
public sealed class StructureHashReviewTests
{
    [Fact]
    public void RenamingACapturedLocalKeepsTheHash()
    {
        static WorkflowDefinition Original()
        {
            int limit = 7;
            return Workflow.Create<StructureHashData>("Capture")
                .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value)
                    .From(ctx => ctx.WorkflowData.Value + limit)).Build();
        }
        static WorkflowDefinition Renamed()
        {
            int threshold = 7;
            return Workflow.Create<StructureHashData>("Capture")
                .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value)
                    .From(ctx => ctx.WorkflowData.Value + threshold)).Build();
        }
        Assert.Equal(Hash(Original()), Hash(Renamed()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderingMappingsChangesTheHash(bool output)
    {
        var definition = Workflow.Create<StructureHashData>("Mappings")
            .Step<StructureHashMultiply>(step => step
                .Input(activity => activity.Value).From(ctx => 1)
                .Input(activity => activity.Value).From(ctx => 2)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Value)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result)).Build();
        var original = Hash(definition);
        var mappings = output ? definition.Steps[0].OutputMappings : definition.Steps[0].InputMappings;
        mappings.Reverse();
        Assert.NotEqual(original, Hash(definition));
    }

    [Theory]
    [InlineData("catch")]
    [InlineData("compensation-order")]
    [InlineData("compensation-removal")]
    [InlineData("then-else")]
    [InlineData("parallel")]
    [InlineData("foreach")]
    [InlineData("finally-order")]
    [InlineData("finally-insert")]
    [InlineData("timeout-order")]
    [InlineData("timeout-insert")]
    public void NestedScopeRepositioningChangesTheHash(string change)
    {
        var builder = Workflow.Create<StructureHashData>("Nested").Step<StructureHashMultiply>();
        var definition = change switch
        {
            "catch" => builder.Try(body => body.Step<StructureHashMultiply>())
                .Catch<InvalidOperationException>(body => body.Step<StructureHashMultiply>())
                .Catch<ArgumentException>(body => body.Step<StructureHashMultiply>()).Build(),
            "compensation-order" or "compensation-removal" => builder.Saga(saga => saga
                .Step<StructureHashMultiply>(step => step
                    .CompensateWith<StructureHashMultiply>(undo => undo.Input(activity => activity.Value).From(ctx => 1))
                    .CompensateWith<StructureHashMultiply>(undo => undo.Input(activity => activity.Value).From(ctx => 2))))
                .Build(),
            "then-else" => builder.If(ctx => ctx.WorkflowData.Value > 0,
                body => body.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1)),
                body => body.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 2)))
                .Build(),
            "parallel" => builder.Parallel(parallel => parallel
                .Do(body => body.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1)))
                .Do(body => body.Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 2))))
                .Build(),
            "foreach" => builder.ForEach(ctx => new[] { 1, 2 }, (body, _) => body
                .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1)))
                .Build(),
            "finally-order" or "finally-insert" => builder.Try(body => body.Step<StructureHashMultiply>())
                .Finally(body => body
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1))
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 2)))
                .Build(),
            _ => builder.WaitFor<StructureHashAnswer>("answer", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(body => body
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1))
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 2))))
                .Build()
        };
        var original = Hash(definition);
        var nested = definition.Steps[1];
        switch (change)
        {
            case "catch": nested.CatchBlocks.Reverse(); break;
            case "compensation-order":
            case "compensation-removal":
                var compensations = (List<CompensationActivityInfo>)nested.SequenceSteps[0].StepMetadata["CompensationActivities"];
                if (change == "compensation-order") compensations.Reverse();
                else compensations.RemoveAt(0);
                break;
            case "then-else": (nested.ThenSteps, nested.ElseSteps) = (nested.ElseSteps, nested.ThenSteps); break;
            case "parallel": nested.ParallelBranches.Reverse(); break;
            case "foreach": nested.ForEachBodySteps.Insert(0, definition.Steps[0].Clone()); break;
            case "finally-order": nested.FinallySteps.Reverse(); break;
            case "finally-insert": nested.FinallySteps.Insert(0, definition.Steps[0].Clone()); break;
            case "timeout-order": nested.TimeoutSteps.Reverse(); break;
            case "timeout-insert": nested.TimeoutSteps.Insert(0, definition.Steps[0].Clone()); break;
        }
        Assert.NotEqual(original, Hash(definition));
    }

    [Fact]
    public void AnonymousMemberNamesAndTypesContributeToTheHash()
    {
        Expression<Func<int, object>> first = value => new { a = value };
        Expression<Func<int, object>> renamed = value => new { b = value };
        Expression<Func<int, object>> retyped = value => new { a = (long)value };
        Assert.NotEqual(WorkflowExpressionSignature.Of(first), WorkflowExpressionSignature.Of(renamed));
        Assert.NotEqual(WorkflowExpressionSignature.Of(first), WorkflowExpressionSignature.Of(retyped));

        // Member access must retain the anonymous member contract too.
        Expression<Func<int, int>> a = value => new { a = value, b = value }.a;
        Expression<Func<int, int>> b = value => new { a = value, b = value }.b;
        Assert.NotEqual(WorkflowExpressionSignature.Of(a), WorkflowExpressionSignature.Of(b));
    }

    [Fact]
    public void RenamingAWorkflowClassWithDeclaredIdentityKeepsMemberAccessHashes()
    {
        const string source = """
            using IxIFlow.Builders;
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            using IxIFlow.Tests.ExecutionTests.StructureHashFixtures;
            [Workflow("Declared", Version = 2)]
            public sealed class Original : IWorkflow<StructureHashData>
            {
                public int Limit => 7;
                public void Build(IWorkflowBuilder<StructureHashData> builder) => builder
                    .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value)
                        .From(ctx => ctx.WorkflowData.Value + Limit));
            }
            public static class Definition
            {
                public static WorkflowDefinition Build()
                {
                    var builder = Workflow.Create<StructureHashData>("Declared", 2);
                    new Original().Build(builder);
                    return builder.Build();
                }
            }
            """;
        using var original = new Compiled(source);
        using var renamed = new Compiled(source.Replace("Original", "Renamed"));
        using var differentIdentity = new Compiled(source.Replace("[Workflow(\"Declared\"", "[Workflow(\"Different\""));
        Assert.Equal(Hash(original.Definition), Hash(renamed.Definition));
        Assert.NotEqual(Hash(original.Definition), Hash(differentIdentity.Definition));
    }

    [Fact]
    public async Task TypedChildUsesTheStoredRegistrationHashAfterPublishedDefinitionMutation()
    {
        var collection = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        collection.RegisterWorkflow<ReviewChild>();
        var registry = new WorkflowCatalog();
        var published = Workflow.Create<StructureHashData>("Review child")
            .Step<StructureHashMultiply>(step => step.Input(activity => activity.Value).From(ctx => 1)).Build();
        await registry.RegisterWorkflowAsync(published);
        var registeredHash = Hash(published);
        // Rebuilding a catalog from this object would now compare the wrong registration contract.
        published.Steps.Clear();
        collection.AddSingleton<IWorkflowCatalog>(registry);
        using var services = collection.BuildServiceProvider();
        var parent = Workflow.Create<StructureHashData>("Parent").Step<StructureHashMultiply>()
            .Invoke<ReviewChild, StructureHashData>().Build();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new StructureHashData()));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Contains(registeredHash, error.Message);
        Assert.Equal(registeredHash, await registry.GetWorkflowStructureHashAsync("Review child", 1));
    }

    [Workflow("Review child")]
    public sealed class ReviewChild : IWorkflow<StructureHashData>
    {
        public void Build(IWorkflowBuilder<StructureHashData> builder) => builder.Step<StructureHashMultiply>();
    }

    [Fact]
    public async Task ExecutorMismatchStopsForVerificationAndKeepsTheSavedCheckpoint()
    {
        var original = Workflow.Create<StructureHashData>("Stopped")
            .Step<StructureHashMultiply>().If(ctx => ctx.WorkflowData.Value > 0,
                body => body.Step<StructureHashMultiply>()).Build();
        var loaded = Workflow.Create<StructureHashData>("Stopped").Step<StructureHashMultiply>().Build();
        using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton<IWorkflowDefinitionSource>(new LoadedDefinitionSource(loaded)).BuildServiceProvider();
        var now = DateTime.UtcNow;
        var data = new StructureHashData { Value = 42 };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = Hash(original),
            Continuations = [new ContinuationState
            {
                Id = "root", Stack = [new ScopePosition { ScopeId = "root/1/then", NextStepIndex = 0 }]
            }]
        };
        var instance = new WorkflowInstance
        {
            InstanceId = "mismatch", WorkflowName = original.Name, WorkflowVersion = original.Version,
            WorkflowDataType = IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(StructureHashData)),
            WorkflowDataJson = StoredJson.Inline(JsonSerializer.Serialize(data)), ExecutionStateJson = JsonSerializer.Serialize(checkpoint),
            Status = WorkflowStatus.Running, StartedAt = now, CreatedAt = now
        };
        var host = new RecordingHost();
        var executor = new WorkflowExecutor(services, host);
        var ready = await executor.ExecuteAsync(new(Guid.NewGuid(), instance, null, checkpoint.StructureHash,
            1, 0, ExecutionLimits.Default, false, []), CancellationToken.None);

        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome.Kind);
        Assert.NotNull(host.Saved);
        instance = host.Saved;
        Assert.Equal(WorkflowStatus.NeedsResolution, instance.Status);
        var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));
        Assert.Equal(OperatorStopKind.Verification, saved.OperatorStop!.Kind);
        Assert.Equal(checkpoint.StructureHash, saved.StructureHash);
        Assert.Equal("root/1/then", saved.Continuations[0].Stack[0].ScopeId);
        Assert.Equal(0, saved.Continuations[0].Stack[0].NextStepIndex);
        Assert.Equal(42, JsonSerializer.Deserialize<StructureHashData>(instance.WorkflowDataJson.InlineText)!.Value);
        Assert.Contains(Hash(loaded), saved.OperatorStop.Reason);
        Assert.Equal(WorkflowStatus.NeedsResolution, Assert.Single(host.Commits).Instance.Status);
    }

    private static string Hash(WorkflowDefinition definition) => new WorkflowScopeCatalog(definition).StructureHash;

    private sealed class LoadedDefinitionSource(WorkflowDefinition loaded) : IWorkflowDefinitionSource
    {
        public async Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? digest, CancellationToken cancellationToken = default) =>
            new((await GetWorkflowDefinitionAsync(name, version))!);
        public Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version, CancellationToken cancellationToken = default) =>
            AcquireWorkflowDefinitionAsync(name, version, null);

        public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) =>
            Task.FromResult<WorkflowDefinition?>(loaded);
    }

    private sealed class RecordingHost : IExecutionHost
    {
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
            Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public List<CheckpointCommit> Commits { get; } = [];
        public WorkflowInstance? Saved { get; private set; }
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            var commit = request.Commit;
            Saved = commit.ReadInstance(Saved);
            Commits.Add(commit);
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, Commits.Count));
        }
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new InvalidOperationException();
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => throw new InvalidOperationException();
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => throw new InvalidOperationException();
    }

    private sealed class Compiled : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("review-hash", isCollectible: true);
        public WorkflowDefinition Definition { get; }
        public Compiled(string source)
        {
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                    typeof(StructureHashData).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
            var compilation = CSharpCompilation.Create("ReviewFixture",
                [CSharpSyntaxTree.ParseText(source)], paths.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            stream.Position = 0;
            Definition = (WorkflowDefinition)_context.LoadFromStream(stream).GetType("Definition")!
                .GetMethod("Build")!.Invoke(null, null)!;
        }
        public void Dispose() => _context.Unload();
    }
}
