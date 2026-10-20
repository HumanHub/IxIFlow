using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>Retained deliveries, ordered by event ID then publisher name using ordinal comparison.</summary>
public sealed record DeliveryQuery(string? PublisherName = null, OutboxDeliveryStatus? Status = null,
    string? InstanceId = null, bool NeverAttempted = false, string? After = null, int Limit = 100)
{
    internal (string EventId, string Publisher)? Validate()
    {
        if (InstanceId != null) InstanceIds.Validate(InstanceId);
        if (Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Limit));
        if (Status is { } status && !Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(Status));
        if (After == null) return null;
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(After));
            if (parts is not { Length: 2 } || parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            return (parts[0], parts[1]);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("Invalid delivery cursor", nameof(After), error); }
    }

    internal DeliveryPage Page(IReadOnlyList<DeliverySummary> rows)
    {
        var items = rows.Take(Limit).ToArray();
        var next = rows.Count > Limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { items[^1].Event.Id, items[^1].PublisherName }))) : null;
        return new(items, next);
    }
}

/// <summary>Administrative delivery projection. Contains no lease credentials.</summary>
public sealed record DeliverySummary(OutboxEvent Event, string PublisherName, OutboxDeliveryStatus Status,
    int Attempts, DateTime? FirstAttemptAtUtc, DateTime NextAttemptAtUtc, string? LastError,
    OutboxGivenUpReason? GivenUpReason, DateTime? RetriedAtUtc, string? RetriedBy)
{
    internal static DeliverySummary From(OutboxDelivery delivery) => new(delivery.Event, delivery.PublisherName,
        delivery.Status, delivery.Attempts, delivery.FirstAttemptAtUtc, delivery.NextAttemptAtUtc,
        delivery.LastError, delivery.GivenUpReason, delivery.RetriedAtUtc, delivery.RetriedBy);
}

public sealed record DeliveryPage(IReadOnlyList<DeliverySummary> Items, string? NextCursor);
