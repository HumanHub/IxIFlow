using System.Globalization;
using System.Runtime.Loader;

namespace IxIFlow.Templates.Razor;

/// <summary>
/// Razor templates, compiled with Roslyn against the template's model type (H-03). The template is a class deriving
/// from <see cref="RazorTemplate{TModel}"/>; it reads the model through <c>Model</c>, and publishing compiles it, so a
/// template that does not compile against its model is never published. A template is C# code: it can call anything
/// its model's assemblies reference, so Razor templates come from trusted authors, and keeping them free of effects,
/// clocks and static state included, is the author's part of keeping <see cref="RenderTemplate{TModel}"/> safe to run
/// again. Validating compiles without loading anything. Rendering keeps the templates it compiled loaded, up to
/// <see cref="RazorEngineOptions.MaxLoadedTemplates"/>, and unloads the least recently rendered beyond that.
/// </summary>
public sealed class RazorTemplateEngine : ITemplateEngine
{
    public const string EngineName = "razor";

    private readonly RazorEngineOptions _settings;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Type Model, string Digest), Loaded> _loaded = new();
    private long _uses;

    public RazorTemplateEngine()
        : this(new RazorEngineOptions())
    {
    }

    public RazorTemplateEngine(RazorEngineOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxLoadedTemplates < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "MaxLoadedTemplates must be at least 1");
        _settings = settings;
    }

    public string Name => EngineName;

    public ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource template, Type modelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(modelType);
        return ValueTask.FromResult(RazorCompiler.Compile(template, modelType, load: false).Errors);
    }

    public async ValueTask<string> RenderAsync<TModel>(TemplateSource template, TModel model, CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(culture);
        if (model is null)
            throw new ArgumentNullException(nameof(model));
        var compiled = Load(template, typeof(TModel));
        if (compiled.Template == null)
            throw new TemplateRenderException(template, string.Join("; ", compiled.Errors));
        cancellationToken.ThrowIfCancellationRequested();
        // The culture is current from the template's construction on, so its field initializers format with it too.
        var current = CultureInfo.CurrentCulture;
        var currentUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            var instance = (RazorTemplate<TModel>)Activator.CreateInstance(compiled.Template)!;
            return await instance.RenderAsync(model, culture, template.Format);
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = currentUi;
        }
    }

    private RazorCompilation Load(TemplateSource template, Type model)
    {
        Loaded loaded;
        lock (_gate)
        {
            if (!_loaded.TryGetValue((model, template.Digest), out loaded!))
            {
                loaded = new Loaded(new Lazy<RazorCompilation>(() =>
                    RazorCompiler.Compile(template, model, load: true)));
                _loaded[(model, template.Digest)] = loaded;
                if (_loaded.Count > _settings.MaxLoadedTemplates)
                {
                    var oldest = _loaded.MinBy(entry => entry.Value.LastUse);
                    _loaded.Remove(oldest.Key);
                    oldest.Value.Evict();
                }
            }
            loaded.LastUse = ++_uses;
        }
        var compilation = loaded.Compilation.Value;
        // Evicted while it compiled: this render still runs it, and its context unloads once nothing uses it.
        if (loaded.Evicted)
            loaded.Unload();
        return compilation;
    }

    private sealed class Loaded(Lazy<RazorCompilation> compilation)
    {
        private int _unloaded;
        private volatile bool _evicted;

        public Lazy<RazorCompilation> Compilation { get; } = compilation;

        public long LastUse { get; set; }

        public bool Evicted => _evicted;

        public void Evict()
        {
            _evicted = true;
            if (Compilation.IsValueCreated)
                Unload();
        }

        // Unloading waits for the template's code to stop running and every reference to it to go.
        public void Unload()
        {
            if (Compilation.Value.Template is { } template && Interlocked.Exchange(ref _unloaded, 1) == 0)
                AssemblyLoadContext.GetLoadContext(template.Assembly)?.Unload();
        }
    }
}

/// <summary>How many compiled Razor templates a host keeps loaded.</summary>
public sealed class RazorEngineOptions
{
    /// <summary>
    /// The most compiled templates kept loaded, each a template version's variant for one model type. Beyond it,
    /// the least recently rendered is unloaded and compiled again when next rendered.
    /// </summary>
    public int MaxLoadedTemplates { get; set; } = 256;
}
