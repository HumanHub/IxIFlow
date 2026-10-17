using System.Collections.Concurrent;

namespace IxIFlow.Templates;

/// <summary>Template versions kept in this process, for templates published with the application.</summary>
public sealed class InMemoryTemplateStore : ITemplateStore
{
    private readonly ConcurrentDictionary<(string Key, int Version), TemplateVersion> _versions = new();

    public ValueTask<TemplateVersion?> FindAsync(TemplateReference template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        return ValueTask.FromResult(_versions.GetValueOrDefault((template.Key, template.Version)));
    }

    public ValueTask AddAsync(TemplateVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        var kept = _versions.GetOrAdd((version.Key, version.Version), version);
        if (!ReferenceEquals(kept, version) && !kept.IsSameAs(version))
            throw new TemplateVersionConflictException(version.Reference);
        return ValueTask.CompletedTask;
    }
}
