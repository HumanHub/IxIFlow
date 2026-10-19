using System.Reflection;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.ContractTests;

[Trait("Feature", "guard")]
[Trait("Spec", "A-02")]
public sealed class PublicApiGuardTests
{
    [Fact]
    public void PublicContractContainsOnlyTheExecutorEntryPointsAndRequiredTypes()
    {
        Type[] publicContract =
        [
            typeof(WorkflowExecutor), typeof(IExecutionHost), typeof(IWorkflowDefinitionSource),
            typeof(Assign), typeof(Ready), typeof(Cancel), typeof(Arrivals), typeof(Recycle), typeof(DebugCommands),
            typeof(Checkpoint), typeof(CheckpointReply), typeof(StartChild), typeof(StartChildReply),
            typeof(ReadInbox), typeof(ReadInboxReply), typeof(ReadWait), typeof(ReadWaitReply),
            typeof(Breakpoint), typeof(RecoveryPreparation), typeof(AcknowledgedCommit), typeof(AssignmentOutcome),
            typeof(BreakpointSource), typeof(BreakpointStop), typeof(BreakpointHitTrace),
            typeof(DebugStepKind), typeof(DebugStepPosition), typeof(DebugStepping), typeof(BreakpointEvaluation), typeof(BreakpointConditionDiagnostic), typeof(IBreakpointConditionCompiler),
            typeof(BreakpointConditionRequest), typeof(BreakpointConditionCompilation), typeof(BreakpointConditionException),
            typeof(BreakpointConditionContext<,,>),
            typeof(CancelKind), typeof(AssignmentOutcomeKind), typeof(CheckpointReplyKind), typeof(ConflictReason),
            typeof(ExecutionContract), typeof(ExecutionMessageContext)
        ];
        Assert.All(publicContract, type => Assert.True(type.IsPublic, type.FullName));
        Assert.Equal([typeof(ExecutionLimits), typeof(WorkflowExecutor), typeof(WorkflowTypeIdentity)],
            typeof(WorkflowExecutor).Assembly.GetExportedTypes()
                .Where(type => type.Namespace == "IxIFlow.Core.Runtime").OrderBy(type => type.Name));
        Assert.Equal(["Deliver", "Deliver", "Deliver", "Deliver", "ExecuteAsync"], typeof(WorkflowExecutor)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Order());
        Assert.Single(typeof(WorkflowExecutor).GetConstructors());
        Assert.Equal([typeof(IServiceProvider), typeof(IExecutionHost)], typeof(WorkflowExecutor)
            .GetConstructors()[0].GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(["CheckpointAsync", "ReadInboxAsync", "ReadWaitAsync", "StartChildAsync"],
            typeof(IExecutionHost).GetMethods().Select(method => method.Name).Order());
        Assert.Equal(["GetWorkflowDefinitionAsync"], typeof(IWorkflowDefinitionSource).GetMethods().Select(method => method.Name));
        Assert.Empty(typeof(WorkflowExecutor).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        string[] internals = ["ExecutionSignals", "ExecutionSession", "CheckpointCommitFields", "CheckpointCommitContext",
            "CheckpointCommitJsonConverter", "ExecutionCheckpoint", "ExecutionCheckpointWriter", "ActivityAttempts",
            "ContinuationTransitions", "EventIntake", "StoredCheckpoint", "InboxProgress"];
        Assert.All(internals, name => Assert.False(typeof(WorkflowExecutor).Assembly.GetTypes().Single(type => type.Name == name).IsPublic));
    }
}
