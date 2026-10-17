namespace IxIFlow.Templates;

/// <summary>
/// Publishes template versions (H-01): each variant is validated by the version's engine against its model type
/// (H-03), and only a version whose every variant is valid reaches the store.
/// </summary>
public sealed class TemplatePublisher(ITemplateStore store, TemplateEngines engines)
{
    /// <summary>
    /// Validates <paramref name="version"/> against <typeparamref name="TModel"/> and stores it. Throws
    /// <see cref="TemplateValidationException"/> for the first invalid variant and
    /// <see cref="TemplateVersionConflictException"/> when the version is already published with other content.
    /// </summary>
    public Task PublishAsync<TModel>(TemplateVersion version, CancellationToken cancellationToken = default) =>
        PublishAsync(version, typeof(TModel), cancellationToken);

    /// <inheritdoc cref="PublishAsync{TModel}"/>
    public async Task PublishAsync(TemplateVersion version, Type modelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(modelType);
        if (version.ModelType != TemplateVersion.ModelTypeName(modelType))
            throw new ArgumentException(
                $"Template {version.Reference} is written against '{version.ModelType}', not " +
                $"'{TemplateVersion.ModelTypeName(modelType)}'", nameof(modelType));
        var engine = engines.Get(version.Engine);
        foreach (var variant in version.Variants)
        {
            var source = TemplateSource.Of(version, variant);
            var diagnostics = await engine.ValidateAsync(source, modelType, cancellationToken);
            if (diagnostics.Count > 0)
                throw new TemplateValidationException(source, diagnostics);
        }
        await store.AddAsync(version, cancellationToken);
    }
}
