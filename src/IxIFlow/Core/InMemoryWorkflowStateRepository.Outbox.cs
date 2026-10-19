namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private sealed record Publisher(string[] Types, DateTime? DisabledAtUtc = null, string? Error = null,
        DateTime? EnabledAtUtc = null, string? EnabledBy = null,
        DateTime? UnregisteredAtUtc = null, string? UnregisteredBy = null)
    {
        public OutboxPublisherState Read(string name) => new(name, DisabledAtUtc, Error,
            EnabledAtUtc, EnabledBy, UnregisteredAtUtc, UnregisteredBy);
    }
    private readonly Dictionary<string, string> _completionEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboxEvent> _outbox = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Publisher> _publishers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string EventId, string Publisher), OutboxDelivery> _deliveries = new();

    private void RecordCompletion(WorkflowInstance instance, DateTime now)
    {
        if (WorkflowStoreRules.IsTerminal(instance.Status))
            _endedAt.TryAdd(instance.InstanceId, now);
        if (!WorkflowStoreRules.IsTerminal(instance.Status) || _completionEvents.ContainsKey(instance.InstanceId))
            return;
        var item = OutboxEvent.Completion(instance, now);
        _outbox.Add(item.Id, item);
        _completionEvents.Add(item.InstanceId, item.Id);
        foreach (var publisher in _publishers)
            AddDelivery(item, publisher.Key, publisher.Value, now);
    }

    private void AddDelivery(OutboxEvent item, string name, Publisher publisher, DateTime now)
    {
        if (publisher.UnregisteredAtUtc != null || !publisher.Types.Contains(item.Type, StringComparer.Ordinal))
            return;
        _deliveries.TryAdd((item.Id, name), new OutboxDelivery(item, name, 0, now,
            publisher.DisabledAtUtc == null ? OutboxDeliveryStatus.Pending : OutboxDeliveryStatus.GivenUp,
            publisher.Error, null, null, now, GivenUpReason: publisher.DisabledAtUtc == null ? null : OutboxGivenUpReason.Disabled));
    }

    private void DeleteOutbox(string instanceId)
    {
        // Pending deliveries survive instance deletion (D-41), but a new instance under this ID
        // gets its own completion. Existing completion IDs continue to identify the old event.
        _completionEvents.Remove(instanceId);
        foreach (var item in _outbox.Values.Where(e => e.InstanceId == instanceId).ToArray())
        {
            if (_deliveries.Values.Any(d => d.Event.Id == item.Id && d.Status == OutboxDeliveryStatus.Pending))
                continue;
            foreach (var key in _deliveries.Keys.Where(k => k.EventId == item.Id).ToArray())
                _deliveries.Remove(key);
            _outbox.Remove(item.Id);
        }
    }

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(eventTypes);
        if (name.Length > 100 || eventTypes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Publisher names are at most 100 characters; event types cannot be blank");
        lock (_claimLock)
        {
            var now = Now;
            var old = _publishers.GetValueOrDefault(name);
            var publisher = (old ?? new Publisher([])) with { Types = eventTypes.Distinct(StringComparer.Ordinal).ToArray() };
            _publishers[name] = publisher;
            if (replayRetained)
                foreach (var item in _outbox.Values)
                    AddDelivery(item, name, publisher, now);
        }
        return Task.CompletedTask;
    }

    public Task UnregisterPublisherAsync(string name, string actor = "host")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        lock (_claimLock)
        {
            _publishers[name] = (_publishers.GetValueOrDefault(name) ?? new Publisher([])) with
                { UnregisteredAtUtc = Now, UnregisteredBy = actor };
            foreach (var key in _deliveries.Keys.Where(k => k.Publisher == name).ToArray())
                if (_deliveries[key].Status == OutboxDeliveryStatus.Pending)
                    _deliveries[key] = _deliveries[key] with { Status = OutboxDeliveryStatus.GivenUp,
                        GivenUpReason = OutboxGivenUpReason.Unregistered, LeaseToken = null, LeaseUntilUtc = null };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token,
        TimeSpan leaseDuration, int limit)
    {
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, limit);
        lock (_claimLock)
        {
            var now = Now;
            if (!_publishers.TryGetValue(publisherName, out var publisher) || publisher.DisabledAtUtc != null || publisher.UnregisteredAtUtc != null)
                return Task.FromResult<IReadOnlyList<OutboxDelivery>>([]);
            var claimed = _deliveries.Values.Where(d => d.PublisherName == publisherName &&
                    d.Status == OutboxDeliveryStatus.Pending && d.NextAttemptAtUtc <= now &&
                    (d.LeaseUntilUtc == null || d.LeaseUntilUtc <= now))
                .OrderBy(d => d.NextAttemptAtUtc).ThenBy(d => d.Event.Id, StringComparer.Ordinal).Take(limit)
                .Select(d => d with { Attempts = checked(d.Attempts + 1), LeaseToken = token,
                    LeaseUntilUtc = now + leaseDuration, StoreNowUtc = now, FirstAttemptAtUtc = d.FirstAttemptAtUtc ?? now }).ToArray();
            foreach (var delivery in claimed)
                _deliveries[(delivery.Event.Id, publisherName)] = delivery;
            return Task.FromResult<IReadOnlyList<OutboxDelivery>>(claimed);
        }
    }

    private bool OwnsDelivery(string eventId, string publisherName, string token, DateTime now, out OutboxDelivery delivery) =>
        _deliveries.TryGetValue((eventId, publisherName), out delivery!) &&
        delivery.Status == OutboxDeliveryStatus.Pending && delivery.LeaseToken == token && delivery.LeaseUntilUtc > now;

    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration)
    {
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, 1);
        lock (_claimLock)
        {
            var now = Now;
            if (!OwnsDelivery(eventId, publisherName, token, now, out var delivery))
                return Task.FromResult(false);
            _deliveries[(eventId, publisherName)] = delivery with { LeaseUntilUtc = now + leaseDuration };
            return Task.FromResult(true);
        }
    }

    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_claimLock)
        {
            var now = Now;
            if (!OwnsDelivery(eventId, publisherName, token, now, out var delivery))
                return Task.FromResult(false);
            var delay = OutboxRetry.Delay(delivery.Attempts, result);
            _deliveries[(eventId, publisherName)] = delivery with
            {
                Status = result.DisablePublisher ? OutboxDeliveryStatus.GivenUp : result.Outcome == OutboxPublishOutcome.Delivered ? OutboxDeliveryStatus.Delivered
                    : result.Outcome == OutboxPublishOutcome.GiveUp || result.DisablePublisher
                        ? OutboxDeliveryStatus.GivenUp : OutboxDeliveryStatus.Pending,
                GivenUpReason = result.DisablePublisher ? OutboxGivenUpReason.Disabled : result.Outcome == OutboxPublishOutcome.GiveUp
                    ? result.GivenUpReason ?? OutboxGivenUpReason.Exhausted : null,
                NextAttemptAtUtc = now + delay, LastError = result.Error, LeaseToken = null, LeaseUntilUtc = null
            };
            if (result.DisablePublisher)
            {
                _publishers[publisherName] = _publishers[publisherName] with { DisabledAtUtc = now, Error = result.Error };
                foreach (var key in _deliveries.Keys.Where(k => k.Publisher == publisherName).ToArray())
                    if (_deliveries[key].Status == OutboxDeliveryStatus.Pending)
                        _deliveries[key] = _deliveries[key] with { Status = OutboxDeliveryStatus.GivenUp,
                            GivenUpReason = OutboxGivenUpReason.Disabled, LastError = result.Error, LeaseToken = null, LeaseUntilUtc = null };
            }
            return Task.FromResult(true);
        }
    }

    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName)
    {
        lock (_claimLock)
            return Task.FromResult(_deliveries.GetValueOrDefault((eventId, publisherName)) is { } d
                ? d with { StoreNowUtc = Now } : null);
    }

    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName)
    {
        lock (_claimLock)
            return Task.FromResult(_publishers.TryGetValue(publisherName, out var p)
                ? p.Read(publisherName) : null);
    }

    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Validate();
        lock (_claimLock)
            return Task.FromResult(query.Page(_deliveries.Values.Where(d =>
                    (query.PublisherName == null || d.PublisherName == query.PublisherName) &&
                    (query.Status == null || d.Status == query.Status) &&
                    (query.InstanceId == null || d.Event.InstanceId == query.InstanceId) &&
                    (!query.NeverAttempted || d.Attempts == 0 && d.FirstAttemptAtUtc == null) &&
                    (cursor == null || string.CompareOrdinal(d.Event.Id, cursor.Value.EventId) > 0 ||
                        d.Event.Id == cursor.Value.EventId && string.CompareOrdinal(d.PublisherName, cursor.Value.Publisher) > 0))
                .OrderBy(d => d.Event.Id, StringComparer.Ordinal).ThenBy(d => d.PublisherName, StringComparer.Ordinal)
                .Take(query.Limit + 1).Select(DeliverySummary.From).ToArray()));
    }

    private static OutboxDelivery Retry(OutboxDelivery delivery, string actor, DateTime now) => delivery with
    {
        Status = OutboxDeliveryStatus.Pending, Attempts = 0, FirstAttemptAtUtc = null, NextAttemptAtUtc = now,
        GivenUpReason = null, LastError = null, LeaseToken = null, LeaseUntilUtc = null,
        RetriedAtUtc = now, RetriedBy = actor
    };

    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        // The same lock protects purge, disable, unregister and completion fan-out.
        lock (_claimLock)
        {
            if (!_publishers.TryGetValue(publisherName, out var publisher) || publisher.DisabledAtUtc != null ||
                publisher.UnregisteredAtUtc != null || !_deliveries.TryGetValue((eventId, publisherName), out var delivery) ||
                delivery.Status != OutboxDeliveryStatus.GivenUp) return Task.FromResult(false);
            _deliveries[(eventId, publisherName)] = Retry(delivery, actor, Now);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync()
    {
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<OutboxPublisherState>>(_publishers.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Value.Read(p.Key)).ToArray());
    }

    public Task<bool> EnablePublisherAsync(string publisherName, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        lock (_claimLock)
        {
            if (!_publishers.TryGetValue(publisherName, out var publisher) || publisher.UnregisteredAtUtc != null ||
                publisher.DisabledAtUtc == null) return Task.FromResult(false);
            var now = Now;
            _publishers[publisherName] = publisher with { DisabledAtUtc = null, Error = null, EnabledAtUtc = now, EnabledBy = actor };
            foreach (var key in _deliveries.Keys.Where(k => k.Publisher == publisherName).ToArray())
                if (_deliveries[key].Status == OutboxDeliveryStatus.GivenUp && _deliveries[key].GivenUpReason == OutboxGivenUpReason.Disabled)
                    _deliveries[key] = Retry(_deliveries[key], actor, now);
            return Task.FromResult(true);
        }
    }
}
