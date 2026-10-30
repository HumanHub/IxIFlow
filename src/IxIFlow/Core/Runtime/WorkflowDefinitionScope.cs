using IxIFlow.Templates;

namespace IxIFlow.Core.Runtime;

/// <summary>Engine-owned definition and dispatch authorization, flowing with each asynchronous callback.</summary>
internal sealed class WorkflowDefinitionScope : IDisposable
{
    private static readonly AsyncLocal<WorkflowDefinitionScope?> Current = new();
    private readonly WorkflowDefinitionScope? _previous = Current.Value;
    private readonly WorkflowDefinition _definition;
    private readonly HashSet<TemplateReference>? _permitted;
    private readonly string? _site;
    private string? _violation;

    private WorkflowDefinitionScope(WorkflowDefinition definition, string? site)
    {
        _definition = definition;
        _site = site;

        _permitted = site == null
            ? null
            : definition.TemplateBindings.FirstOrDefault(item => item.Step == site)?.Templates.ToHashSet() ?? [];

        Current.Value = this;
    }

    internal static WorkflowDefinitionScope Enter(WorkflowDefinition definition) => new(definition, null);
    internal static WorkflowDefinitionScope Dispatch(WorkflowDefinition definition, string site) => new(definition, site);
    public void Dispose() => Current.Value = _previous;

    // Settlement can run in a different execution context. Re-enter the same authorization
    // and failure slot, restoring that context's frame when the settlement leaves.
    internal IDisposable Resume() => new Activation(this);
    internal static void ThrowIfDispatchFailed() => Current.Value?.ThrowIfFailed();

    private sealed class Activation : IDisposable
    {
        private readonly WorkflowDefinitionScope? _previous = Current.Value;
        internal Activation(WorkflowDefinitionScope frame) => Current.Value = frame;
        public void Dispose() => Current.Value = _previous;
    }

    // Only this core guard can write the failure slot. The Templates assembly can request authorization,
    // but cannot manufacture a trusted failure or reset a violation swallowed by authored code.
    internal static TemplateVersion? Resolve(TemplateReference? reference, Type model, bool hasModel)
    {
        var frame = Current.Value;

        if (frame?._definition.BundleDigest == null)
            return null;

        if (reference == null || !hasModel)
            frame.Fail("A bundled render needs a pinned reference and a model");

        var version = frame.Permit(reference!);

        if (version.ModelTypeId != WorkflowTypeIdentity.SavedName(model) || version.ModelType != TemplateVersion.ModelTypeName(model))
            frame.Fail($"Template '{reference}' model differs from the bundled model");

        return version;
    }

    internal void ValidateInputs(ActivityInputSnapshot snapshot, object activity)
    {
        if (_definition.BundleDigest == null)
            return;

        foreach (var (name, saved) in snapshot.Properties)
        {
            var property = activity.GetType().GetProperty(name);

            if (property?.PropertyType == typeof(TemplateReference))
            {
                // Read the engine's saved copy, never a user getter or a mutable activity property.
                if (saved.RestoredTemplate is { } reference)
                    Permit(reference);
                else
                    Fail($"Template input '{name}' at '{_site}' has no pinned reference");
            }
        }
    }

    private TemplateVersion Permit(TemplateReference reference)
    {
        if (_site == null || _permitted?.Contains(reference) != true)
            Fail($"Template '{reference}' is not permitted at bundle site '{_site}'");

        var version = _definition.Templates?.Find(reference);

        if (version == null)
            Fail($"Template '{reference}' is absent from the verified bundle");

        return version!;
    }

    private void Fail(string message)
    {
        Interlocked.CompareExchange(ref _violation, message, null);
        ThrowIfFailed();
    }

    internal void ThrowIfFailed()
    {
        if (Volatile.Read(ref _violation) is { } violation)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, violation);
    }
}