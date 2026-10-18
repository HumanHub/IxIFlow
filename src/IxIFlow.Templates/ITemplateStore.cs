namespace IxIFlow.Templates;

/// <summary>
/// Where published template versions are kept. A version, once added, never changes: adding it again with the
/// same content changes nothing, and adding it with different content throws
/// <see cref="TemplateVersionConflictException"/>. Published bundles will carry the versions their definitions pin
///  in the artifact store; until then <see cref="InMemoryTemplateStore"/> holds them.
/// </summary>
public interface ITemplateStore
{
    /// <summary>The version <paramref name="template"/> names, or null when it was never published here.</summary>
    ValueTask<TemplateVersion?> FindAsync(TemplateReference template, CancellationToken cancellationToken = default);

    /// <summary>Keeps <paramref name="version"/>, or does nothing when the same content is already kept.</summary>
    ValueTask AddAsync(TemplateVersion version, CancellationToken cancellationToken = default);
}
