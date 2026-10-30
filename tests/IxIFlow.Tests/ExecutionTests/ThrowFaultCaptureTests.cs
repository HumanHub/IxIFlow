using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;
using Json = System.Text.Json.JsonSerializer;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "W-05")]
public sealed class ThrowFaultCaptureTests
{
    [Fact]
    public void ThrowFirstCaptureRetainsPropertyUnionForConstructorRestorableErrors()
    {
        var definition = new WorkflowBuilder<Data, object>([], "Union").Try(t => t.Throw<RestorableError>(ctx => new("first", 42)))
            .Catch<RestorableError, CodeFault>(c => c.Rethrow()).Catch<Exception>(c =>
            {
            }).Build();

        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint();
        var branch = new ContinuationState { Stack = [new ScopePosition { ScopeId = "root/0/sequence" }] };
        checkpoint.Continuations.Add(branch);
        var error = new RestorableError("first", 42);
        var captured = SerializedException.Capture(error, scopes, checkpoint, branch, newOccurrence: true);
        Assert.True(captured.IsRestorable);
        Assert.NotEmpty(captured.Properties);
        var restored = Json.Deserialize<SerializedException>(Json.Serialize(captured))!;

        var projected = SerializedException.FromForCatch(restored.ForPropagation(checkpoint), typeof(RestorableError), typeof(CodeFault),
            checkpoint: checkpoint);

        Assert.Equal(42, Json.Deserialize<CodeFault>(projected.Fault!.Json.InlineText)!.Code);
        Assert.Equal(captured.OriginalFaultLocation, projected.OriginalFaultLocation);
    }

    [Fact]
    public void ThrowMissingSavedPropertyAndUnavailableTypeAreEngineFailures()
    {
        var saved = CaptureFault(new BusinessError("private") { Code = 42 }, typeof(CodeFault));
        saved.Properties.Clear();

        var missing = Assert.Throws<WorkflowEngineException>(() =>
            SerializedException.FromForCatch(saved.ForPropagation(new ExecutionCheckpoint()), typeof(BusinessError), typeof(CodeFault),
                new ExecutionCheckpoint()));

        Assert.Equal(WorkflowErrorKind.InvalidState, missing.Kind);
        saved.Type = "Missing.Error, Missing.Artifact";

        var unavailable =
            Assert.Throws<WorkflowEngineException>(() => SerializedException.CatchType(saved.ForPropagation(new ExecutionCheckpoint())));

        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, unavailable.Kind);
    }

    [Theory]
    [InlineData(WorkflowErrorKind.ArtifactUnavailable)]
    [InlineData(WorkflowErrorKind.InvalidState)]
    [InlineData(WorkflowErrorKind.ResourceExhausted)]
    public void ThrowProjectionPreservesWrappedEngineFailure(WorkflowErrorKind kind)
    {
        var original = new WorkflowEngineException(kind, "engine");

        var result = Assert.Throws<WorkflowEngineException>(() => FaultCapture.ProjectionFailure(new Exception(),
            new TargetInvocationException(new TargetInvocationException(original))));

        Assert.Same(original, result);
    }

    [Fact]
    public void ThrowProjectionKeepsUserGetterFailureOnTheBusinessPath()
    {
        var saved = CaptureFault(new GetterError(), typeof(CodeFault));

        var error = Assert.Throws<InvalidOperationException>(() =>
            SerializedException.FromForCatch(saved.ForPropagation(new ExecutionCheckpoint()), typeof(GetterError), typeof(CodeFault),
                new ExecutionCheckpoint()));

        Assert.Contains("user getter", error.Message);
    }

    private static SerializedException CaptureFault(Exception error, Type faultType)
    {
        var definition = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStep
                {
                    StepType = WorkflowStepType.CatchBlock, ExceptionType = error.GetType(), FaultType = faultType
                }
            ]
        };

        return Json.Deserialize<SerializedException>(Json.Serialize(SerializedException.Capture(error, new WorkflowScopeCatalog(definition),
            new ExecutionCheckpoint(), null, newOccurrence: true)))!;
    }

    public sealed class RestorableError(string message, int code) : Exception(message)
    {
        public int Code { get; } = code;
    }

    public sealed class GetterError : Exception
    {
        public int Code => throw new InvalidOperationException("user getter");
    }
}