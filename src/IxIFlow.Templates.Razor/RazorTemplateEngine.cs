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
/// Validating compiles without loading anything. Rendering compiles each template once per model type and keeps it
/// loaded while the model type is, which bounds what is loaded by the template versions a host renders.
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
        return ValueTask.FromResult(RazorCompiler.Compile(template, modelType, load: false).Errors);
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

    private RazorCompilation Compile(TemplateSource template, Type model) =>
        _compiled.GetValue(model, _ => new ConcurrentDictionary<string, Lazy<RazorCompilation>>(StringComparer.Ordinal))
            .GetOrAdd(template.Digest,
                _ => new Lazy<RazorCompilation>(() => RazorCompiler.Compile(template, model, load: true)))
            .Value;
}
