using System.Diagnostics;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>One batch poller per publisher. Shutdown finishes only deliveries already in flight.</summary>
internal sealed class WorkflowOutboxService(IWorkflowStateRepository repository, IEnumerable<IOutboxPublisher> publishers,
    HostSettings settings, OutboxSettings options, TimeProvider time, ILogger<WorkflowOutboxService> logger,
    IEnumerable<OutboxPublisherStart>? starts = null, IEnumerable<OutboxPublisherRemoval>? removals = null) : IHostedService, IDisposable
{
    private readonly IOutboxPublisher[] _publishers = publishers.ToArray();
    private readonly CancellationTokenSource _stopPolling = new();
    private readonly CancellationTokenSource _stopDelivery = new();
    private readonly SemaphoreSlim _slots = new(options.Concurrency);
    private Task _workers = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        if (_publishers.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != _publishers.Length)
            throw new InvalidOperationException("Outbox publisher names must be unique on a host");
        foreach (var removal in removals ?? [])
        {
            if (_publishers.Any(p => p.Name == removal.Name))
                throw new InvalidOperationException("An outbox publisher cannot be registered and unregistered on the same host");
            await repository.UnregisterPublisherAsync(removal.Name);
        }
        foreach (var publisher in _publishers)
            await repository.RegisterPublisherAsync(publisher.Name, publisher.EventTypes,
                starts?.Any(s => s.Name == publisher.Name && s.ReplayRetained) == true);
        _workers = Task.WhenAll(_publishers.Select(PollAsync));
    }

    private async Task PollAsync(IOutboxPublisher publisher)
    {
        var active = new List<Task>();
        var share = Math.Max(1, options.Concurrency / _publishers.Length);
        try
        {
            while (!_stopPolling.IsCancellationRequested)
            {
                active.RemoveAll(t => t.IsCompleted);
                if (active.Count == share)
                {
                    await Task.WhenAny(active).WaitAsync(_stopPolling.Token);
                    continue;
                }
                var reserved = 0;
                try
                {
                    while (reserved < share - active.Count && _slots.Wait(0)) reserved++;
                    if (reserved > 0 && !_stopPolling.IsCancellationRequested)
                    {
                        var claims = await repository.ClaimDeliveriesAsync(publisher.Name, Guid.NewGuid().ToString("N"),
                            options.LeaseDuration, reserved);
                        foreach (var delivery in claims)
                        {
                            reserved--;
                            active.Add(RunDeliveryAsync(publisher, delivery));
                        }
                        if (claims.Count > 0) continue;
                    }
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Could not claim workflow outbox deliveries for {PublisherName}", publisher.Name);
                }
                finally { if (reserved > 0) _slots.Release(reserved); }
                await Task.Delay(settings.PollInterval, time, _stopPolling.Token);
            }
        }
        catch (OperationCanceledException) when (_stopPolling.IsCancellationRequested) { }
        finally { await Task.WhenAll(active); }
    }

    private async Task RunDeliveryAsync(IOutboxPublisher publisher, OutboxDelivery delivery)
    {
        try { await DeliverAsync(publisher, delivery, _stopDelivery.Token); }
        catch (OperationCanceledException) when (_stopDelivery.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Could not deliver workflow outbox event {EventId}", delivery.Event.Id); }
        finally { _slots.Release(); }
    }

    internal async Task DeliverAsync(IOutboxPublisher publisher, OutboxDelivery delivery, CancellationToken token)
    {
        var item = delivery.Event;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [LogScope.InstanceId] = item.InstanceId, [LogScope.WorkflowName] = item.WorkflowName,
            ["PublisherName"] = publisher.Name, ["EventId"] = item.Id, ["AttemptNumber"] = delivery.Attempts
        });
        var tags = new TagList { { Telemetry.WorkflowName, item.WorkflowName },
            { Telemetry.WorkflowVersion, item.WorkflowVersion }, { Telemetry.PublisherName, publisher.Name } };
        Telemetry.OutboxAge.Record(Math.Max(0, (delivery.StoreNowUtc - item.OccurredAtUtc).TotalSeconds), tags);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(token);
        var renew = RenewAsync(delivery, attempt, renewal.Token);
        var started = time.GetTimestamp();
        var remaining = options.RetryHorizon - (delivery.StoreNowUtc - (delivery.FirstAttemptAtUtc ?? delivery.StoreNowUtc));
        try
        {
            OutboxPublishResult result;
            try
            {
                result = remaining <= TimeSpan.Zero ? OutboxPublishResult.GiveUp("Retry horizon elapsed")
                    : await publisher.PublishAsync(new(item, delivery.Attempts, delivery.StoreNowUtc), attempt.Token)
                        .WaitAsync(attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Outbox publisher failed; delivery will retry");
                result = OutboxPublishResult.RetryLater(error.Message);
            }
            // A completed renewal cannot race settlement or report a lease lost to our own settle.
            renewal.Cancel();
            await renew;
            token.ThrowIfCancellationRequested();
            attempt.Token.ThrowIfCancellationRequested();
            result = ClampRetry(result, delivery.Attempts, remaining - time.GetElapsedTime(started));
            bool settled;
            try { settled = await repository.SettleDeliveryAsync(item.Id, publisher.Name, delivery.LeaseToken!, result); }
            catch (Exception error)
            {
                if (result.Outcome != OutboxPublishOutcome.Delivered) throw;
                Telemetry.OutboxSettleFailures.Add(1, tags);
                logger.LogWarning(error, "Published outbox event {EventId}, but settlement failed; it may be delivered again", item.Id);
                return;
            }
            if (!settled)
            {
                if (result.Outcome == OutboxPublishOutcome.Delivered)
                {
                    Telemetry.OutboxSettleFailures.Add(1, tags);
                    logger.LogWarning("Published outbox event {EventId}, but settlement was rejected; it may be delivered again", item.Id);
                }
                else logger.LogWarning("Lost outbox delivery lease for {EventId} before settlement", item.Id);
                return;
            }
            tags.Add(Telemetry.Outcome, result.Outcome.ToString());
            Telemetry.OutboxDeliveries.Add(1, tags);
            if (result.Outcome != OutboxPublishOutcome.Delivered)
                logger.LogWarning("Outbox delivery {Outcome}: {Error}", result.Outcome, result.Error);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt.IsCancellationRequested)
        {
            // Renewal already logged the lost lease; it is an expected fencing outcome.
        }
        finally
        {
            renewal.Cancel();
            attempt.Cancel();
            await renew;
        }
    }

    internal static OutboxPublishResult ClampRetry(OutboxPublishResult result, int attempts, TimeSpan remaining)
    {
        if (result.Outcome != OutboxPublishOutcome.RetryLater) return result;
        if (remaining <= TimeSpan.Zero) return OutboxPublishResult.GiveUp(result.Error);
        var delay = result.RetryAfter ?? OutboxRetry.Delay(attempts);
        delay = TimeSpan.FromTicks(Math.Clamp(delay.Ticks, 0, Math.Min(TimeSpan.FromDays(30).Ticks, remaining.Ticks)));
        return result with { RetryAfter = delay };
    }

    private async Task RenewAsync(OutboxDelivery delivery, CancellationTokenSource attempt, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(options.LeaseDuration / 3, time);
            while (await timer.WaitForNextTickAsync(token))
                if (!await repository.RenewDeliveryLeaseAsync(delivery.Event.Id, delivery.PublisherName,
                        delivery.LeaseToken!, options.LeaseDuration))
                {
                    logger.LogWarning("Lost outbox delivery lease for {EventId}", delivery.Event.Id);
                    attempt.Cancel();
                    return;
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not renew outbox delivery lease for {EventId}", delivery.Event.Id);
            attempt.Cancel();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopPolling.Cancel();
        try { await _workers.WaitAsync(settings.DrainTimeout, time, cancellationToken); }
        catch (TimeoutException)
        {
            logger.LogWarning("Outbox drain timed out; unfinished deliveries remain leased for recovery");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { _stopDelivery.Cancel(); }
    }

    public void Dispose()
    {
        _stopPolling.Cancel();
        _stopDelivery.Cancel();
    }
}

/// <summary>Concurrency is independent of workflow slots; retry age starts at the first claim on the store clock.</summary>
public sealed record OutboxSettings
{
    public int Concurrency { get; init; }
    public TimeSpan LeaseDuration { get; init; }
    public TimeSpan RetryHorizon { get; init; }

    public OutboxSettings(int concurrency = 8, TimeSpan? leaseDuration = null, TimeSpan? retryHorizon = null)
    {
        Concurrency = concurrency;
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        RetryHorizon = retryHorizon ?? TimeSpan.FromDays(30);
        Validate();
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Concurrency, 1000);
        if (LeaseDuration < TimeSpan.FromMilliseconds(3) || LeaseDuration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (RetryHorizon <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryHorizon));
    }
}

internal sealed record OutboxPublisherStart(string Name, bool ReplayRetained);
internal sealed record OutboxPublisherRemoval(string Name);
