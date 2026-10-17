using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Fluid;
using Fluid.Ast;
using Microsoft.Extensions.FileProviders;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// Liquid templates, rendered by Fluid (H-03). A template reads only its model's members: the public properties and
/// fields of the model type and of the types reachable through them. Publishing checks every variable, member and
/// filter against the model type, and a render refuses anything undefined rather than writing nothing. A render has
/// no clock, no file system and no other template to include, so its text depends only on the template, the model and
/// the culture.
/// </summary>
public sealed partial class LiquidTemplateEngine : ITemplateEngine
{
    public const string EngineName = "liquid";

    private static readonly FluidParser Parser = new(new FluidParserOptions { AllowFunctions = false });

    private readonly LiquidEngineOptions _settings;
    private readonly ConcurrentDictionary<string, Lazy<Parsed>> _parsed = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Type, TemplateOptions> _options = new();

    public LiquidTemplateEngine()
        : this(new LiquidEngineOptions())
    {
    }

    public LiquidTemplateEngine(LiquidEngineOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxSteps < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "MaxSteps must be at least 1");
        if (settings.MaxRecursion < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "MaxRecursion must be at least 1");
        _settings = settings;
    }

    public string Name => EngineName;

    public ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource template, Type modelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(modelType);
        var parsed = Parse(template);
        IReadOnlyList<TemplateDiagnostic> diagnostics = parsed.Template is IStatementList statements
            ? LiquidModelChecker.Check(statements, modelType, OptionsFor(modelType))
            : [parsed.Error!];
        return ValueTask.FromResult(diagnostics);
    }

    public async ValueTask<string> RenderAsync<TModel>(TemplateSource template, TModel model, CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(culture);
        if (model is null)
            throw new ArgumentNullException(nameof(model));
        var parsed = Parse(template);
        if (parsed.Template == null)
            throw new TemplateRenderException(template, parsed.Error!.ToString());
        cancellationToken.ThrowIfCancellationRequested();
        // The model's own members come through the member access strategy, like every other type's, so a model of a
        // derived type shows no more than the type the template was published against.
        var context = new TemplateContext(model, OptionsFor(typeof(TModel)), allowModelMembers: false)
        {
            CultureInfo = culture
        };
        try
        {
            return await parsed.Template.RenderAsync(context,
                template.Format == TemplateFormat.Html ? HtmlEncoder.Default : NullEncoder.Default);
        }
        catch (Exception error) when (error is FluidException or InvalidOperationException or FormatException
                                          or ArgumentException)
        {
            throw new TemplateRenderException(template, error.Message, error);
        }
    }

    private Parsed Parse(TemplateSource template) =>
        _parsed.GetOrAdd(template.Digest, _ => new Lazy<Parsed>(() =>
            Parser.TryParse(template.Content, out var parsed, out var error)
                ? new Parsed(parsed, null)
                : new Parsed(null, Diagnostic(error)))).Value;

    private TemplateOptions OptionsFor(Type model) => _options.GetValue(model, type =>
    {
        var options = new TemplateOptions
        {
            StrictVariables = true,
            StrictFilters = true,
            MaxSteps = _settings.MaxSteps,
            MaxRecursion = _settings.MaxRecursion,
            FileProvider = new NullFileProvider(),
            TimeZone = TimeZoneInfo.Utc,
            CultureInfo = CultureInfo.InvariantCulture,
            Now = () => throw new InvalidOperationException(
                "A template has no clock: 'now' and 'today' are not available; pass the time in the model"),
            MemberAccessStrategy = new DefaultMemberAccessStrategy()
        };
        foreach (var reachable in LiquidModel.Reachable(type))
            options.MemberAccessStrategy.Register(reachable);
        return options;
    });

    private static TemplateDiagnostic Diagnostic(string error)
    {
        var position = PositionPattern().Match(error);
        // Fluid appends the source text after the position; the message ends at the position.
        var message = position.Success ? error[..(position.Index + position.Length)].Trim() : error.Trim();
        return position.Success
            ? new TemplateDiagnostic(message, int.Parse(position.Groups["line"].Value, CultureInfo.InvariantCulture),
                int.Parse(position.Groups["column"].Value, CultureInfo.InvariantCulture))
            : new TemplateDiagnostic(message);
    }

    [GeneratedRegex(@"\((?<line>\d+):(?<column>\d+)\)")]
    private static partial Regex PositionPattern();

    private sealed record Parsed(IFluidTemplate? Template, TemplateDiagnostic? Error);
}

/// <summary>Limits on a Liquid render, so a template cannot hold a host's slot indefinitely.</summary>
public sealed class LiquidEngineOptions
{
    /// <summary>The most statements one render may run; loops count every iteration.</summary>
    public int MaxSteps { get; set; } = 1_000_000;

    /// <summary>The deepest a render may nest.</summary>
    public int MaxRecursion { get; set; } = 100;
}
