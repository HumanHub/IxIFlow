namespace IxIFlow.Dsl.Compilation;

public sealed record WorkflowValidationContext(
    IActivityRegistry ActivityRegistry,
    IWorkflowCatalog WorkflowCatalog,
    IEventRegistry EventRegistry,
    IDataTypeRegistry DataTypeRegistry,
    IStepTemplateRegistry StepTemplateRegistry) : IWorkflowValidationContext
{
    public async Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token)
    {
        var activity = await ActivityRegistry.FindAsync(key, token);
        return activity == null ? null : new(activity.Key, activity.Version, activity.PackageName, activity.PackageVersion);
    }

    public async Task<bool> HasDataTypeAsync(string key, CancellationToken token) =>
        await DataTypeRegistry.FindAsync(key, token) != null;
    public async Task<bool> HasEventAsync(string key, CancellationToken token) =>
        await EventRegistry.FindAsync(key, token) != null;
    public async Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) =>
        await WorkflowCatalog.FindAsync(name, version, token) != null;
    public async Task<bool> HasStepTemplateAsync(string key, CancellationToken token) =>
        await StepTemplateRegistry.FindAsync(key, token) != null;
}
