using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>An immutable event committed with the workflow. Its ID and JSON stay the same on every delivery.</summary>
public sealed record OutboxEvent(string Id, string Type, string InstanceId, string WorkflowName,
    int WorkflowVersion, string CorrelationId, DateTime OccurredAtUtc, string DataJson)
{
    public const string WorkflowCompleted = "io.ixiflow.workflow.completed";

    /// <summary>Creates the completion envelope at the store's commit time, without checkpoint internals.</summary>
    internal static OutboxEvent Completion(WorkflowInstance instance, DateTime now) => new(
        Guid.NewGuid().ToString("N"), WorkflowCompleted, instance.InstanceId, instance.WorkflowName,
        instance.WorkflowVersion, instance.CorrelationId, now, JsonSerializer.Serialize(new
        {
            instanceId = instance.InstanceId, workflowName = instance.WorkflowName,
            workflowVersion = instance.WorkflowVersion, correlationId = instance.CorrelationId,
            status = instance.Status.ToString(), revision = instance.Revision, errorKind = instance.Status switch
            {
                WorkflowStatus.Failed => "WorkflowFailure",
                WorkflowStatus.Cancelled => "Cancelled",
                WorkflowStatus.Terminated => "Terminated",
                WorkflowStatus.TimedOut => "TimedOut",
                _ => (string?)null
            }
        }));
}

public enum OutboxDeliveryStatus { Pending, Delivered, GivenUp }
public enum OutboxPublishOutcome { Delivered, RetryLater, GiveUp }

/// <summary>A retry delay overrides the default exponential backoff. DisablePublisher persists endpoint shutdown.</summary>
public sealed record OutboxPublishResult(OutboxPublishOutcome Outcome, TimeSpan? RetryAfter = null,
    string? Error = null, bool DisablePublisher = false)
{
    public static OutboxPublishResult Delivered() => new(OutboxPublishOutcome.Delivered);
    public static OutboxPublishResult RetryLater(string? error = null, TimeSpan? delay = null)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(delay), "Retry delay must be between zero and 30 days");
        return new(OutboxPublishOutcome.RetryLater, delay, error);
    }
    public static OutboxPublishResult GiveUp(string? error = null, bool disablePublisher = false) =>
        new(OutboxPublishOutcome.GiveUp, Error: error, DisablePublisher: disablePublisher);
}

/// <summary>Applications register singleton publishers on the IxIFlow builder. Names must be stable across hosts.</summary>
public interface IOutboxPublisher
{
    string Name { get; }
    IReadOnlyCollection<string> EventTypes { get; }
    Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken);
}

/// <summary>The event and store time for this attempt, without delivery lease or settlement state.</summary>
public sealed record OutboxPublishContext(OutboxEvent Event, int Attempts, DateTime StoreNowUtc);

/// <summary>Attempts count claims, including a claim lost with its host. StoreNowUtc is sampled after claiming.</summary>
public sealed record OutboxDelivery(OutboxEvent Event, string PublisherName, int Attempts,
    DateTime NextAttemptAtUtc, OutboxDeliveryStatus Status, string? LastError, string? LeaseToken,
    DateTime? LeaseUntilUtc, DateTime StoreNowUtc, DateTime? FirstAttemptAtUtc = null);

public sealed record OutboxPublisherState(string Name, DateTime? DisabledAtUtc, string? LastError);

/// <summary>
/// Per-publisher, leased, at-least-once delivery. All deadlines use the store's clock. Registration starts with
/// future events unless replay is requested; it never resets a delivery or re-enables a disabled endpoint.
/// </summary>
public interface IWorkflowOutbox
{
    Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false);
    Task UnregisterPublisherAsync(string name);
    Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token,
        TimeSpan leaseDuration, int limit);
    Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration);
    Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result);
    Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName);
    Task<OutboxPublisherState?> GetPublisherAsync(string publisherName);
}

/// <summary>One bounded retry policy for custom publishers. Webhooks return their standard schedule explicitly.</summary>
internal static class OutboxRetry
{
    public static TimeSpan Delay(int attempts) => TimeSpan.FromSeconds(
        Math.Min(3600, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10))));

    public static TimeSpan Delay(int attempts, OutboxPublishResult result)
    {
        if (result.RetryAfter is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(30)))
            throw new ArgumentOutOfRangeException(nameof(result), "Retry delay must be between zero and 30 days");
        return result.RetryAfter ?? Delay(attempts);
    }

    internal static void ValidateClaim(string name, string token, TimeSpan duration, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
    }
}
