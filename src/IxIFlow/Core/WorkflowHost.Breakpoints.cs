using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        public async Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId,
            IReadOnlyList<Breakpoint> breakpoints, string actor, CancellationToken cancellationToken, Guid? expectedIncarnation = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);
            cancellationToken.ThrowIfCancellationRequested();
            if (!(hostSettings ?? HostSettings.Default).Debugging)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Debugging is disabled on this host");
            var instance = await _store.GetWorkflowInstanceAsync(instanceId)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow instance does not exist");
            RequireIncarnation(instance, expectedIncarnation);
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow definition is unavailable");
            if (WorkflowDefinitionValidator.ForExecution(definition).StructureHash !=
                ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson).StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Workflow structure differs");
            var resolved = BreakpointResolver.Resolve(definition, breakpoints);
            foreach (var breakpoint in resolved.Where(b => b.Condition != null))
            {
                var compiler = services.GetService(typeof(IBreakpointConditionCompiler)) as IBreakpointConditionCompiler;
                if (compiler == null)
                    throw new BreakpointConditionException([new("BPC000", "No breakpoint condition compiler is registered", 0,
                        breakpoint.Condition!.Length, 1, 1)]);
                var compilation = compiler.Compile(BreakpointConditionInputs.Request(definition, breakpoint.StepId, breakpoint.Condition!));
                if (compilation.Evaluate == null) throw new BreakpointConditionException(compilation.Diagnostics);
            }
            var state = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            var generation = state.PauseGeneration + (instance.Status != WorkflowStatus.Paused &&
                state.Commands.Any(c => c.AcknowledgedRevision == null && c.Kind == DebugCommandKind.Pause) ? 1 : 0);
            var result = await _store.RequestDebugCommandAsync(instanceId, new(Guid.NewGuid().ToString("N"),
                DebugCommandKind.SetBreakpoints, generation, actor, resolved) { ExpectedIncarnation = instance.Incarnation });
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
