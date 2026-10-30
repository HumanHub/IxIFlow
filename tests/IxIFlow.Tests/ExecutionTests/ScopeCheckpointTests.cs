using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeCheckpointTests
{
    [Fact]
    public void RootAndBranchPartsRoundTripOnlySavedScopeConfiguration()
    {
        var checkpoint = Checkpoint();
        checkpoint.Scopes.Add(Entry("root", "root-value"));
        checkpoint.Continuations.Add(new() { Id = "branch", ParentJoinId = "join", Sequence = 1, Stack = [new() { ScopeId = "root" }] });
        checkpoint.Scopes.Add(Entry("branch", "branch-value"));
        var split = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"), verify: true);
        Assert.DoesNotContain("branch-value", split.Trunk);
        Assert.Contains("branch-value", split.Parts[StoredCheckpoint.BranchKey("branch")].InlineText);
        Assert.DoesNotContain("NEVER-SAVE-LIVE-VALUE", split.Trunk + string.Join("", split.Parts.Values.Select(value => value.InlineText)));
        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(split.Trunk, split.Parts));
        Assert.Equal(2, restored.Scopes.Count);

        Assert.All(restored.Scopes, entry =>
        {
            Assert.Null(entry.Value);
            Assert.Null(entry.LiveActivity);
            Assert.Null(entry.Scheduler);
        });
    }

    [Fact]
    public void EveryErrorSlotKeepsItsOwnBoundaryAcrossTrunkAndBranchParts()
    {
        var state = Checkpoint();
        var root = state.Continuations[0];
        root.Stack[0].TryState = new() { Phase = TryPhase.Finally, Error = Failure("try") };
        root.Stack[0].SagaState = new() { Error = Failure("saga"), HandlerError = Failure("handler") };
        root.DeferredFailure = Failure("deferred");
        root.UnwindFailure = Failure("unwind");
        state.UnhandledError = Failure("unhandled");
        var entry = Entry("root", "root-value");
        entry.CloseError = Failure("close");
        state.Scopes.Add(entry);

        state.Continuations.Add(new()
        {
            Id = "branch", Sequence = 1, ParentJoinId = "join", DeferredFailure = Failure("branch"), Stack = [new() { ScopeId = "root" }]
        });

        var saved = StoredCheckpoint.Split(state, StoredJson.Inline("{}"));
        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(saved.Trunk, saved.Parts));
        var restoredRoot = restored.Continuations[0];
        Assert.Equal("try", restoredRoot.Stack[0].TryState!.Error!.Message);
        Assert.Equal("saga", restoredRoot.Stack[0].SagaState!.Error!.Message);
        Assert.Equal("deferred", restoredRoot.DeferredFailure!.Message);
        Assert.Equal("unwind", restoredRoot.UnwindFailure!.Message);
        Assert.Equal("handler", restoredRoot.Stack[0].SagaState!.HandlerError!.Message);
        Assert.Equal("branch", restored.Continuations[1].DeferredFailure!.Message);
        Assert.Equal("close", restored.Scopes[0].CloseError!.Message);
        Assert.Equal("body-close", restored.Scopes[0].CloseError!.ScopeBoundary!.BodyActivationId);
        Assert.Equal("owner-unhandled", restored.UnhandledError!.ScopeBoundary!.OwnerContinuationId);

        foreach (var failure in new[]
                 {
                     restoredRoot.Stack[0].TryState!.Error!, restoredRoot.Stack[0].SagaState!.Error!,
                     restoredRoot.Stack[0].SagaState!.HandlerError!, restoredRoot.DeferredFailure!, restoredRoot.UnwindFailure!,
                     restored.UnhandledError!, restored.Scopes[0].CloseError!, restored.Continuations[1].DeferredFailure!
                 })
        {
            Assert.Equal("owner-" + failure.Message, failure.ScopeBoundary!.OwnerContinuationId);
            Assert.Equal("body-" + failure.Message, failure.ScopeBoundary.BodyActivationId);
        }
    }

    [Theory, InlineData("OwnerContinuationId", ""), InlineData("BodyActivationId", " ")]
    public void BlankBoundaryIdentifiersAreInvalidCheckpointState(string property, string value)
    {
        var state = Checkpoint();
        state.UnhandledError = Failure("bad");
        var json = JsonSerializer.SerializeToNode(state)!;
        json["UnhandledError"]!["ScopeBoundary"]![property] = value;
        Assert.Throws<JsonException>(() => ExecutionCheckpoint.Read(json.ToJsonString()));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void MissingOrNullErrorIsInvalidCheckpointState(bool missing)
    {
        var state = Checkpoint();
        state.UnhandledError = Failure("bad");
        var json = JsonSerializer.SerializeToNode(state)!;

        if (missing)
            ((JsonObject)json["UnhandledError"]!).Remove("Error");
        else
            json["UnhandledError"]!["Error"] = null;

        Assert.Throws<JsonException>(() => ExecutionCheckpoint.Read(json.ToJsonString()));
    }

    [Fact]
    public void BoundaryCanOutliveItsBodyFrame()
    {
        var state = Checkpoint();
        state.UnhandledError = Failure("ended-body");
        var restored = ExecutionCheckpoint.Read(JsonSerializer.Serialize(state));
        Assert.False(restored.UnhandledError!.ScopeBoundary!.Contains(restored.Continuations[0], 0));
    }

    [Fact]
    public void PreScopeCheckpointContractIsRefused()
    {
        var json = JsonSerializer.SerializeToNode(Checkpoint())!;
        json["SchemaVersion"] = 10;
        var error = Assert.Throws<WorkflowEngineException>(() => ExecutionCheckpoint.Read(json.ToJsonString()));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
    }

    [Fact]
    public void AuthorizationMatchesOnlyItsSavedOccurrenceAndInvocation()
    {
        var continuation = Checkpoint().Continuations[0];
        var authorized = DispatchAuthorization.For(continuation, "root/0");
        Assert.True(authorized.Matches(continuation, "root/0"));
        continuation.Stack[0].LoopIterationCount++;
        Assert.False(authorized.Matches(continuation, "root/0"));
        continuation.Stack[0].LoopIterationCount--;
        authorized.InvocationId = "claimed";
        continuation.PendingActivity = new() { Id = "different" };
        Assert.False(authorized.Matches(continuation, "root/0"));
    }

    [Fact]
    public void RootRequirementAndScopeMetadataRoundTripInCanonicalContract()
    {
        var definition = Workflow.Create<ScopeBuildRuleTests.Data>("contract").RequiresScope<ScopeBuildRuleTests.Value>().Build();
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Equal(contract.Hash, WorkflowContract.Parse(contract.ToJson()).Hash);
        Assert.Contains("RequiredScopes", contract.ToJson());
    }

    [Fact]
    public void RejectedMarksRewriteTheScopeOwnersPartInsteadOfTheNeedingBranch()
    {
        var state = Checkpoint();
        var owner = new ContinuationState { Id = "owner", ParentJoinId = "join", Sequence = 1, Stack = [new() { ScopeId = "root" }] };
        state.Continuations.Add(owner);
        var entry = Entry(owner.Id, "scope");
        state.Scopes.Add(entry);

        state.Continuations.Add(new()
            { Id = "needing", ParentJoinId = "nested", Sequence = 2, InheritedScopes = [entry.Id], Stack = [new() { ScopeId = "root" }] });

        var first = StoredCheckpoint.Split(state, StoredJson.Inline("{}"), verify: true);
        state.TakeMarks();
        entry.RecordedOpens++;
        state.TouchOwner(entry.ContinuationId);
        var rejected = state.TakeMarks();
        state.RestoreMarks(rejected);
        var second = StoredCheckpoint.Split(state, StoredJson.Inline("{}"), first.Parts, state.TakeMarks(), verify: true);
        Assert.NotEqual(first.Parts[StoredCheckpoint.BranchKey(owner.Id)], second.Parts[StoredCheckpoint.BranchKey(owner.Id)]);
        Assert.Same(first.Parts[StoredCheckpoint.BranchKey("needing")], second.Parts[StoredCheckpoint.BranchKey("needing")]);
        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(second.Trunk, second.Parts));
        Assert.Equal(1, Assert.Single(restored.Scopes).RecordedOpens);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ScopeSavedStateUsesClaimCheckAndRetainsItsPayloadDependencies(bool branchOwner)
    {
        using var services = ScopeActivityTests.Services(new());
        var state = Checkpoint();

        if (branchOwner)
            state.Continuations.Add(new() { Id = "branch", ParentJoinId = "join", Sequence = 1, Stack = [new() { ScopeId = "root" }] });

        var entry = Entry(branchOwner ? "branch" : "root", "large");
        entry.Activity = SerializedValue.From(new ScopeActivityTests.Copy { Text = new string('x', 4096) });
        state.Scopes.Add(entry);
        var payloads = new CheckpointPayloads(ExecutionLimits.Default with { PayloadThresholdBytes = 128 }, new PayloadBudget(1_000_000));
        var saved = StoredCheckpoint.Split(state, StoredJson.Inline("{}"), payloads: payloads);
        var bodies = payloads.Pending.ToDictionary(value => value.Identity, value => value.Encoded);
        Assert.NotEmpty(saved.PayloadReferences);
        Assert.DoesNotContain(new string('x', 100), saved.Trunk);

        var parts = saved.Parts.ToDictionary(pair => pair.Key,
            pair => pair.Value.Reference is { } reference ? StoredJson.FromReference(reference) : pair.Value);

        var reader = new PayloadReader(reference => Task.FromResult(bodies[reference]), new PayloadBudget(1_000_000));

        foreach (var (key, value) in parts)
            if (key.StartsWith("c/", StringComparison.Ordinal))
                await reader.ReadAsync(value);

        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(saved.Trunk, parts));
        await reader.HydrateAsync(Assert.Single(restored.Scopes).Activity);
        Assert.Equal(new string('x', 4096), Assert.IsType<ScopeActivityTests.Copy>(restored.Scopes[0].Activity!.Read(services)).Text);
    }

    private static ExecutionCheckpoint Checkpoint() => new()
        { StructureHash = "test", Continuations = [new() { Id = "root", Stack = [new() { ScopeId = "root" }] }] };

    private static ScopeFrameState Entry(string owner, string id) => new()
    {
        ContinuationId = owner, Id = id, StepId = "root/0", InvocationId = "invocation-" + id,
        Phase = ScopePhase.Open, Activity = SerializedValue.From(new ScopeBuildRuleTests.PlainScope()),
        Value = "NEVER-SAVE-LIVE-VALUE", LiveActivity = new object()
    };

    private static PendingFailure Failure(string message) => new()
    {
        Error = SerializedException.Capture(new InvalidOperationException(message), new WorkflowScopeCatalog(new WorkflowDefinition()),
            new ExecutionCheckpoint(), null, newOccurrence: true),
        ScopeBoundary = new() { OwnerContinuationId = "owner-" + message, BodyActivationId = "body-" + message }
    };
}