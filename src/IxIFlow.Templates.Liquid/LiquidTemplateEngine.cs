using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Fluid;
using Fluid.Ast;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// Liquid templates, rendered by Fluid (H-03). A template reads only its model's members: the public properties and
/// fields of the model type and of the types reachable through them; any other object is nil to it. Publishing checks
/// every variable, member and filter against the model type. A render has no clock, no file system, no host time zone
/// and no other template to include, so its text depends only on the template, the model and the culture.
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
        // Fluid 2.40 parses a {% liquid %} block into no statements at all, losing the rest of the template.
        if (LiquidTagPattern().IsMatch(template.Content))
        {
            return ValueTask.FromResult<IReadOnlyList<TemplateDiagnostic>>(
                [new TemplateDiagnostic("The {% liquid %} tag is not available; write each tag in its own {% %}")]);
        }
        // Drafts are parsed afresh, so validating them keeps nothing; only what is rendered is kept.
        var parsed = ParseOnce(template);
        IReadOnlyList<TemplateDiagnostic> diagnostics = parsed.Template is IStatementList statements
            ? LiquidModelChecker.Check(statements, modelType, OptionsFor(modelType))
            : [parsed.Error!];
        return ValueTask.FromResult(diagnostics);
    }

    [GeneratedRegex(@"\{%-?\s*liquid\b")]
    private static partial Regex LiquidTagPattern();

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
        var outer = LiquidValues.Culture.Value;
        LiquidValues.Culture.Value = culture;
        // Fluid compares and formats some values with the current culture, sort by a member among them.
        var current = CultureInfo.CurrentCulture;
        var currentUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            // The model's own members come through the member access strategy, like every other type's, so a model
            // of a derived type shows no more than the type the template was published against.
            var context = new TemplateContext(model, OptionsFor(typeof(TModel)), allowModelMembers: false)
            {
                CultureInfo = culture
            };
            return await parsed.Template.RenderAsync(context,
                template.Format == TemplateFormat.Html ? HtmlEncoder.Default : NullEncoder.Default);
        }
        catch (Exception error) when (error is FluidException or InvalidOperationException or FormatException
                                          or ArgumentException or InvalidCastException or OverflowException)
        {
            throw new TemplateRenderException(template, error.Message, error);
        }
        finally
        {
            LiquidValues.Culture.Value = outer;
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = currentUi;
        }
    }

    private Parsed Parse(TemplateSource template) =>
        _parsed.GetOrAdd(template.Digest, _ => new Lazy<Parsed>(() => ParseOnce(template))).Value;

    private static Parsed ParseOnce(TemplateSource template) =>
        Parser.TryParse(template.Content, out var parsed, out var error)
            ? new Parsed(parsed, null)
            : new Parsed(null, Diagnostic(error));

    private TemplateOptions OptionsFor(Type model) => _options.GetValue(model, type =>
    {
        var options = new TemplateOptions
        {
            // Undefined values are nil, as Liquid has them: a model's null members render as nothing. Publishing
            // checks every name against the model, and LiquidValues keeps everything outside the model unreadable.
            StrictFilters = true,
            MaxSteps = _settings.MaxSteps,
            MaxRecursion = _settings.MaxRecursion,
            FileProvider = new NullFileProvider(),
            TimeZone = TimeZoneInfo.Utc,
            CultureInfo = CultureInfo.InvariantCulture,
            // Fluid reads the clock before parsing any date; the date filters take no text it would complete from it.
            Now = () => DateTimeOffset.UnixEpoch,
            MemberAccessStrategy = new DefaultMemberAccessStrategy()
        };
        var reachable = LiquidModel.Reachable(type);
        foreach (var registered in reachable)
            options.MemberAccessStrategy.Register(registered, LiquidModel.Accessors(registered));
        options.ValueConverters.Add(new LiquidValues(reachable).Convert);
        // json serializes the object behind a value, every member of its runtime type included.
        options.Filters.Remove("json");
        // Fluid's sum reads a member only from its own object values, not from the model's.
        if (options.Filters.TryGetValue("sum", out var sum))
            options.Filters.AddFilter("sum", (input, arguments, context) =>
                arguments.Count == 0 ? sum(input, arguments, context) : SumOfMember(input, arguments, context));
        // Fluid sorts by a member with the comparer of the culture the process started with; these sort by the
        // render's culture.
        foreach (var (name, order) in new[]
                 { ("sort", CompareOptions.None), ("sort_natural", CompareOptions.IgnoreCase) })
        {
            if (!options.Filters.TryGetValue(name, out var sort))
                continue;
            options.Filters.AddFilter(name, (input, arguments, context) =>
                arguments.Count == 0 ? sort(input, arguments, context) : SortByMember(input, arguments, context, order));
        }
        foreach (var name in DateFilters)
        {
            if (!options.Filters.TryGetValue(name, out var filter))
                continue;
            options.Filters.AddFilter(name, (input, arguments, context) =>
            {
                if (input.Type != FluidValues.String)
                    return filter(input, arguments, context);
                var text = input.ToStringValue();
                return TryParseDate(text, out var date)
                    ? filter(new DateTimeValue(date), arguments, context)
                    : throw new InvalidOperationException(NotADate(text));
            });
        }
        return options;
    });

    private static async ValueTask<FluidValue> SortByMember(FluidValue input, FilterArguments arguments,
        TemplateContext context, CompareOptions compare)
    {
        var member = arguments.At(0).ToStringValue();
        var keyed = new List<(FluidValue Item, FluidValue Key)>();
        foreach (var item in input.Enumerate(context))
            keyed.Add((item, await item.GetValueAsync(member, context)));
        var culture = context.CultureInfo.CompareInfo;
        // Nil first, then numbers by value, then everything else by its text in the render's culture; a stable sort
        // keeps equal items in their order.
        return new ArrayValue(keyed
            .OrderBy(entry => entry.Key.IsNil() ? 0 : entry.Key is NumberValue ? 1 : 2)
            .ThenBy(entry => entry.Key is NumberValue number ? number.ToNumberValue() : 0)
            .ThenBy(entry => entry.Key is NumberValue || entry.Key.IsNil() ? "" : entry.Key.ToStringValue(),
                Comparer<string>.Create((left, right) => culture.Compare(left, right, compare)))
            .Select(entry => entry.Item)
            .ToArray());
    }

    private static async ValueTask<FluidValue> SumOfMember(FluidValue input, FilterArguments arguments,
        TemplateContext context)
    {
        var member = arguments.At(0).ToStringValue();
        var total = 0m;
        foreach (var item in input.Enumerate(context))
        {
            total += item switch
            {
                ArrayValue => (await SumOfMember(item, arguments, context)).ToNumberValue(),
                ObjectValueBase => (await item.GetValueAsync(member, context)).ToNumberValue(),
                _ => 0
            };
        }
        return NumberValue.Create(total);
    }

    /// <summary>The filters that read text as a date, completing what it leaves out from the clock.</summary>
    internal static readonly string[] DateFilters = ["date", "format_date", "time_zone"];

    // A date written as text must be a complete ISO 8601 date, since Fluid takes a missing date, year or time zone from
    // the host's clock. Without an offset it is read at offset zero, like a DateTime in the model.
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mmK", "yyyy-MM-dd HH:mm:ssK"
    ];

    internal static bool TryParseDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out date);

    internal static string NotADate(string text) =>
        $"'{text}' is not a date a template can read: a template has no clock, so a date written as text is a " +
        "complete ISO 8601 date such as 2026-03-04 or 2026-03-04T09:30:00Z; pass the time in the model";

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
