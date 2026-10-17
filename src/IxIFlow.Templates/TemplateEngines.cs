namespace IxIFlow.Templates;

/// <summary>The template engines this host has, by name.</summary>
public sealed class TemplateEngines
{
    private readonly Dictionary<string, ITemplateEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    public TemplateEngines(IEnumerable<ITemplateEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        foreach (var engine in engines)
        {
            if (!_engines.TryAdd(engine.Name, engine) && !ReferenceEquals(_engines[engine.Name], engine))
                throw new InvalidOperationException($"Two template engines are named '{engine.Name}'");
        }
    }

    /// <summary>The engine named <paramref name="name"/>; throws <see cref="TemplateNotFoundException"/> without it.</summary>
    public ITemplateEngine Get(string name) =>
        _engines.TryGetValue(name, out var engine)
            ? engine
            : throw new TemplateNotFoundException($"No template engine named '{name}' is registered in this host");
}
