using System.Collections.ObjectModel;

namespace IxIFlow.Templates;

/// <summary>The immutable template content carried by one verified workflow bundle.</summary>
internal sealed class TemplateSet
{
    private readonly IReadOnlyDictionary<TemplateReference, TemplateVersion> _versions;

    public TemplateSet(IReadOnlyList<TemplateVersion> versions)
    {
        Versions = Array.AsReadOnly(versions.OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray());
        _versions = new ReadOnlyDictionary<TemplateReference, TemplateVersion>(Versions.ToDictionary(item => item.Reference));
    }

    public IReadOnlyList<TemplateVersion> Versions { get; }
    public TemplateVersion? Find(TemplateReference reference) => _versions.GetValueOrDefault(reference);
}