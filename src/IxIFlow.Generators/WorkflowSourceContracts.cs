using IxIFlow.Dsl.Compilation;

namespace IxIFlow.Generators;

public sealed class WorkflowSourceContracts(WorkflowSourceOptions options) : IWorkflowValidationContext
{
    public Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) =>
        Task.FromResult(KnownType(key) ? options.Activities.TryGetValue(key, out var activity)
            ? activity : new ActivityDocumentContract(key, "") : null);
    public Task<bool> HasDataTypeAsync(string key, CancellationToken token) => Task.FromResult(KnownType(key));
    public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(KnownType(key));
    public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) =>
        Task.FromResult(options.Workflows.TryGetValue(name + (version == null ? "" : "@" + version), out var workflow) &&
            workflow.Version > 0 && (version == null || version == workflow.Version));
    private bool KnownType(string key) => options.Types.ContainsKey(key) || !options.RequireResolvedTypes &&
        !Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseTypeName(key).ContainsDiagnostics;
    public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(false);
}

