using System.Collections.Concurrent;
using System.Globalization;
using Fluid.Values;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// How a render turns .NET values into Liquid values, for one model type. An object whose type the model does not
/// reach becomes nil, so nothing of it can be read or written, and an object the model does reach writes no text of
/// its own, so its <c>ToString</c> cannot show members the template could not read. Dates are written as they are,
/// never shifted by the host's time zone.
/// </summary>
internal sealed class LiquidValues(IReadOnlyList<Type> reachable)
{
    private readonly ConcurrentDictionary<Type, bool> _allowed = new();

    public object? Convert(object value) => value switch
    {
        // Fluid shifts a DateTime that is not UTC by the host's offset; its clock time is what the model says.
        DateTime dateTime => new DateTimeValue(new DateTimeOffset(dateTime.Ticks, TimeSpan.Zero)),
        // Fluid writes a TimeSpan as a date in 1970.
        TimeSpan timeSpan => new StringValue(timeSpan.ToString("c", CultureInfo.InvariantCulture)),
        _ when !LiquidModel.IsObject(value.GetType()) => null,
        _ => _allowed.GetOrAdd(value.GetType(), type => reachable.Any(allowed => allowed.IsAssignableFrom(type)))
            ? new ModelValue(value)
            : NilValue.Instance
    };

    /// <summary>
    /// An object of the model, read only through its members. Filters that unwrap a value, such as format_string, get
    /// its empty text rather than the object.
    /// </summary>
    private sealed class ModelValue(object value) : ObjectValueBase(value)
    {
        public override string ToStringValue() => "";

        public override object ToObjectValue() => "";
    }
}
