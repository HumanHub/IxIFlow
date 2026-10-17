using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace IxIFlow.Templates.Razor;

/// <summary>
/// Razor templates, compiled with Roslyn against the template's model type (H-03). The template is a class deriving
/// from <see cref="RazorTemplate{TModel}"/>; it reads the model through <c>Model</c>, and publishing compiles it, so a
/// template that does not compile against its model is never published. A template is C# code: it can call anything
/// its model's assemblies reference, so Razor templates come from trusted authors, and keeping them free of effects,
/// clocks included, is the author's part of keeping <see cref="RenderTemplate{TModel}"/> safe to run again.
/// Each template is compiled once per model type and kept while the model type is loaded.
/// </summary>
public sealed class RazorTemplateEngine : ITemplateEngine
{
    public const string EngineName = "razor";

    private readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, Lazy<RazorCompilation>>> _compiled = new();

    public string Name => EngineName;

    public ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource template, Type modelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(modelType);
        return ValueTask.FromResult(Compile(template, modelType).Errors);
    }

    public async ValueTask<string> RenderAsync<TModel>(TemplateSource template, TModel model, CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(culture);
        if (model is null)
            throw new ArgumentNullException(nameof(model));
        var compiled = Compile(template, typeof(TModel));
        if (compiled.Template == null)
            throw new TemplateRenderException(template, string.Join("; ", compiled.Errors));
        cancellationToken.ThrowIfCancellationRequested();
        var instance = (RazorTemplate<TModel>)Activator.CreateInstance(compiled.Template)!;
        return await instance.RenderAsync(model, culture, template.Format);
    }

    private RazorCompilation Compile(TemplateSource template, Type model) =>
        _compiled.GetValue(model, _ => new ConcurrentDictionary<string, Lazy<RazorCompilation>>(StringComparer.Ordinal))
            .GetOrAdd(template.Digest, _ => new Lazy<RazorCompilation>(() => RazorCompiler.Compile(template, model)))
            .Value;
}
