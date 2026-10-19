using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        public async Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId,
            IReadOnlyList<Breakpoint> breakpoints, string actor, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);
            cancellationToken.ThrowIfCancellationRequested();
            if (!(hostSettings ?? HostSettings.Default).Debugging)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Debugging is disabled on this host");
            var instance = await _store.GetWorkflowInstanceAsync(instanceId)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow instance does not exist");
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow definition is unavailable");
            if (WorkflowDefinitionValidator.ForExecution(definition).StructureHash !=
                ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson).StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Workflow structure differs");
            var resolved = BreakpointResolver.Resolve(definition, breakpoints);
            var state = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            var generation = state.PauseGeneration + (instance.Status != WorkflowStatus.Paused &&
                state.Commands.Any(c => c.AcknowledgedRevision == null && c.Kind == DebugCommandKind.Pause) ? 1 : 0);
            var result = await _store.RequestDebugCommandAsync(instanceId, new(Guid.NewGuid().ToString("N"),
                DebugCommandKind.SetBreakpoints, generation, actor, resolved));
            if (result.Status != DebugCommandStatus.Accepted)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, $"Debug command refused: {result.Status}");
            return resolved;
        }

        public async Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string instanceId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceAsync(instanceId)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow instance does not exist");
            var state = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            return WorkflowDebugRules.Breakpoints(instance, state);
        }
    }
}
