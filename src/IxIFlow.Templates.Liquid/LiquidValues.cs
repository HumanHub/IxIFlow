using System.Collections.Concurrent;
using System.Globalization;
using Fluid.Values;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// How a render turns .NET values into Liquid values, for one model type. An object whose type the model does not
/// reach becomes nil, so nothing of it can be read or written, and an object the model does reach writes no text of
/// its own, so its <c>ToString</c> cannot show members the template could not read. Dates are written as they are,
/// never shifted by the host's time zone, and formattable values are formatted for the culture the render is for.
/// </summary>
internal sealed class LiquidValues(IReadOnlyList<Type> reachable)
{
    /// <summary>
    /// The culture of the render in progress. Fluid formats values when it converts them, with its options' culture,
    /// and the options are shared by every render of the model type.
    /// </summary>
    public static readonly AsyncLocal<CultureInfo?> Culture = new();

    private readonly ConcurrentDictionary<Type, bool> _allowed = new();

    public object? Convert(object value) => value switch
    {
        // A local time is saved in a checkpoint with the host's offset and read back on another host as that host's
        // local time, so the same step would render another time there.
        DateTime { Kind: DateTimeKind.Local } => throw new InvalidOperationException(
            "A DateTime of kind Local depends on the host's time zone; give the model UTC times or DateTimeOffsets"),
        // Fluid shifts a DateTime that is not UTC by the host's offset; its clock time is what the model says.
        DateTime dateTime => new DateTimeValue(new DateTimeOffset(dateTime.Ticks, TimeSpan.Zero)),
        // Fluid would write a DateOnly as text that the date filters read back by culture.
        DateOnly date => new DateTimeValue(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
        // Fluid writes a TimeSpan as a date in 1970.
        TimeSpan timeSpan => new StringValue(timeSpan.ToString("c", CultureInfo.InvariantCulture)),
        TimeOnly time => new StringValue(time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        _ when IsFormattedByFluid(value.GetType()) => value switch
        {
            IFormattable formattable => new StringValue(formattable.ToString(null, CurrentCulture)),
            IConvertible convertible => new StringValue(convertible.ToString(CurrentCulture)),
            _ => null
        },
        // A collection or dictionary of values that could be anything is unreadable, as a member declared so is.
        _ when !LiquidModel.HoldsReadable(value.GetType()) => NilValue.Instance,
        _ when !LiquidModel.IsObject(value.GetType()) => null,
        _ => _allowed.GetOrAdd(value.GetType(), type => reachable.Any(allowed => allowed.IsAssignableFrom(type)))
            ? new ModelValue(value)
            : NilValue.Instance
    };

    private static CultureInfo CurrentCulture => Culture.Value ?? CultureInfo.InvariantCulture;

    // The values Fluid turns into text with its options' culture: formattable or convertible types that are not
    // numbers, strings, booleans, dates or enums, which it converts by their type code.
    private static bool IsFormattedByFluid(Type type) =>
        !type.IsEnum && Type.GetTypeCode(type) == TypeCode.Object && type != typeof(DateTimeOffset) &&
        (typeof(IFormattable).IsAssignableFrom(type) || typeof(IConvertible).IsAssignableFrom(type));

    /// <summary>
    /// An object of the model, read only through its members. Filters that unwrap a value, such as format_string, get
    /// its empty text rather than the object, and arithmetic gets zero, as Liquid treats what is not a number.
    /// </summary>
    private sealed class ModelValue(object value) : ObjectValueBase(value)
    {
        public override string ToStringValue() => "";

        public override object ToObjectValue() => "";

        public override decimal ToNumberValue() => 0;
    }
}
