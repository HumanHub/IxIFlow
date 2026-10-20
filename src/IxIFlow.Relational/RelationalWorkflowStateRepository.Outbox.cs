using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private sealed class PublisherRow
    {
        public string Name { get; set; } = "";
        public string TypesJson { get; set; } = "[]";
        public DateTime? DisabledAtUtc { get; set; }
        public string? LastError { get; set; }
        public DateTime? EnabledAtUtc { get; set; }
        public string? EnabledBy { get; set; }
        public DateTime? UnregisteredAtUtc { get; set; }
        public string? UnregisteredBy { get; set; }
        public OutboxPublisherState Read() => new(Name, DisabledAtUtc is { } disabled ? Utc(disabled) : null,
            LastError, EnabledAtUtc is { } enabled ? Utc(enabled) : null, EnabledBy,
            UnregisteredAtUtc is { } removed ? Utc(removed) : null, UnregisteredBy);
    }

    private sealed class DeliveryRow
    {
        public string EventId { get; set; } = "";
        public string EventJson { get; set; } = "";
        public string PublisherName { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime? FirstAttemptAtUtc { get; set; }
        public DateTime NextAttemptAtUtc { get; set; }
        public int Status { get; set; }
        public string? LastError { get; set; }
        public string? LeaseToken { get; set; }
        public OutboxGivenUpReason? GivenUpReason { get; set; }
        public DateTime? RetriedAtUtc { get; set; }
        public string? RetriedBy { get; set; }
        public DateTime? LeaseUntilUtc { get; set; }
        public OutboxDelivery Read(DateTime now) => new(JsonSerializer.Deserialize<OutboxEvent>(EventJson)!,
            PublisherName, Attempts, Utc(NextAttemptAtUtc), (OutboxDeliveryStatus)Status, LastError,
            LeaseToken, LeaseUntilUtc is { } until ? Utc(until) : null, now, FirstAttemptAtUtc is { } first ? Utc(first) : null,
            GivenUpReason, RetriedAtUtc is { } retry ? Utc(retry) : null, RetriedBy);
    }

    private Task<DateTime> OutboxNowAsync(DbConnection connection, DbTransaction? transaction) =>
        UsesLocalClock ? Task.FromResult(LocalNow()) : ServerNowAsync(connection, transaction);

    private async Task<DateTime> ServerNowAsync(DbConnection connection, DbTransaction? transaction) =>
        ReadTime(await connection.ExecuteScalarAsync<DateTime>(RequiredSql(Statement.OutboxClock), transaction: transaction));

    public async Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(eventTypes);
        if (name.Length > 100 || eventTypes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Publisher names are at most 100 characters; event types cannot be blank");
        await EnsureSchemaAsync();
        await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxMark);
            await LockPublishersAsync(connection, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.PublisherUpsert),
                new { Name = name, TypesJson = JsonSerializer.Serialize(eventTypes.Distinct(StringComparer.Ordinal)) }, transaction);
            var now = await OutboxNowAsync(connection, transaction);
            foreach (var type in replayRetained ? eventTypes.Distinct(StringComparer.Ordinal) : [])
                await connection.ExecuteAsync(RequiredSql(Statement.BackfillDeliveries),
                    WithTimes(new { Name = name, EventType = type }, ("Now", now)), transaction);
            await CompleteAsync(transaction);
            return true;
        });
    }

    // SQLite already holds its immediate writer transaction; no extra round trip is needed.
    private Task LockPublishersAsync(DbConnection connection, DbTransaction? transaction, bool shared = false) =>
        Sql(shared ? Statement.PublisherSharedLock : Statement.PublisherLock) is { } sql
            ? connection.ExecuteAsync(sql, transaction: transaction) : Task.CompletedTask;

    public async Task UnregisterPublisherAsync(string name, string actor = "host")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync();
        await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxMark);
            await LockPublishersAsync(connection, transaction);
            var now = await OutboxNowAsync(connection, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.UnregisterPublisher),
                WithTimes(new { Name = name, Actor = actor }, ("Now", now)), transaction);
            await CompleteAsync(transaction);
            return true;
        });
    }

    private async Task RecordCompletionAsync(DbConnection connection, DbTransaction? transaction,
        WorkflowInstance? instance, long revision, DateTime now)
    {
        if (instance == null || !WorkflowStoreRules.IsTerminal(instance.Status))
            return;
        await connection.ExecuteAsync(RequiredSql(Statement.StampEnd),
            WithTimes(new { instance.InstanceId }, ("Now", now)), transaction);
        instance.Revision = revision;
        await LockPublishersAsync(connection, transaction, shared: true);
        var item = OutboxEvent.Completion(instance, now);
        var changed = await connection.ExecuteAsync(RequiredSql(Statement.InsertOutboxEvent), WithTimes(new
        {
            EventId = item.Id, item.InstanceId, EventType = item.Type, EventJson = JsonSerializer.Serialize(item)
        }, ("Now", now)), transaction);
        if (changed != 1)
            return;
        var publishers = await connection.QueryAsync<PublisherRow>(RequiredSql(Statement.Publishers), transaction: transaction);
        foreach (var publisher in publishers)
            if (publisher.UnregisteredAtUtc == null && JsonSerializer.Deserialize<string[]>(publisher.TypesJson)!.Contains(item.Type, StringComparer.Ordinal))
                await connection.ExecuteAsync(RequiredSql(Statement.InsertDelivery), WithTimes(new
                {
                    EventId = item.Id, Name = publisher.Name,
                    Status = publisher.DisabledAtUtc == null ? 0 : 2, publisher.LastError,
                    GivenUpReason = publisher.DisabledAtUtc == null ? (int?)null : (int)OutboxGivenUpReason.Disabled
                }, ("Now", now)), transaction);
    }

    public async Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token,
        TimeSpan leaseDuration, int limit)
    {
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, limit);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync<IReadOnlyList<OutboxDelivery>>(Operation.OutboxClaim, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxClaim);
            var rows = (await connection.QueryAsync<DeliveryRow>(RequiredSql(Statement.ClaimDeliveries),
                WithLocalTime(new { Name = publisherName, Limit = limit }), transaction)).ToArray();
            // Candidate locks are held before the clock is read; hosts send durations only (D-35).
            var now = await OutboxNowAsync(connection, transaction);
            foreach (var row in rows)
            {
                row.Attempts = checked(row.Attempts + 1);
                row.FirstAttemptAtUtc ??= now;
                row.LeaseToken = token;
                row.LeaseUntilUtc = now + leaseDuration;
                await UpdateDeliveryAsync(connection, transaction, row);
            }
            await CompleteAsync(transaction);
            return rows.Select(row => row.Read(now)).ToArray();
        });
    }

    private Task UpdateDeliveryAsync(DbConnection connection, DbTransaction? transaction, DeliveryRow row) =>
        connection.ExecuteAsync(RequiredSql(Statement.DeliveryUpdate), WithTimes(new
        {
            row.EventId, Name = row.PublisherName, row.Attempts, row.Status, row.LastError, row.LeaseToken,
            row.GivenUpReason, row.RetriedBy
        }, ("RetriedAtUtc", row.RetriedAtUtc), ("FirstAttemptAtUtc", row.FirstAttemptAtUtc), ("NextAttemptAtUtc", row.NextAttemptAtUtc), ("LeaseUntilUtc", row.LeaseUntilUtc)), transaction);

    private async Task<bool> ChangeDeliveryAsync(string eventId, string name, string token,
        Func<DeliveryRow, DateTime, Task> change, bool disable = false, string? error = null)
    {
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxMark);
            if (disable)
                await LockPublishersAsync(connection, transaction);
            var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(RequiredSql(Statement.DeliveryLock),
                new { EventId = eventId, Name = name }, transaction);
            var now = await OutboxNowAsync(connection, transaction);
            if (row == null || row.Status != 0 || row.LeaseToken != token || row.LeaseUntilUtc == null ||
                Utc(row.LeaseUntilUtc.Value) <= now)
                return false;
            await change(row, now);
            await UpdateDeliveryAsync(connection, transaction, row);
            if (disable)
                await connection.ExecuteAsync(RequiredSql(Statement.DisablePublisher),
                    WithTimes(new { Name = name, Error = error }, ("Now", now)), transaction);
            await CompleteAsync(transaction);
            return true;
        });
    }

    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration)
    {
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, 1);
        return ChangeDeliveryAsync(eventId, publisherName, token, (row, now) =>
        {
            row.LeaseUntilUtc = now + leaseDuration;
            return Task.CompletedTask;
        });
    }

    public async Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var changed = await ChangeDeliveryAsync(eventId, publisherName, token, (row, now) =>
        {
            row.NextAttemptAtUtc = now + OutboxRetry.Delay(row.Attempts, result);
            row.Status = result.DisablePublisher ? 2 : result.Outcome == OutboxPublishOutcome.Delivered ? 1
                : result.Outcome == OutboxPublishOutcome.GiveUp || result.DisablePublisher ? 2 : 0;
            row.GivenUpReason = result.DisablePublisher ? OutboxGivenUpReason.Disabled : row.Status == 2
                ? result.GivenUpReason ?? OutboxGivenUpReason.Exhausted : null;
            row.LastError = result.Error;
            row.LeaseToken = null;
            row.LeaseUntilUtc = null;
            return Task.CompletedTask;
        }, result.DisablePublisher, result.Error);
        return changed;
    }

    public async Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxRead);
        var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(RequiredSql(Statement.DeliveryRead),
            new { EventId = eventId, Name = publisherName }, transaction);
        return row?.Read(await OutboxNowAsync(connection, transaction));
    }

    public async Task<OutboxPublisherState?> GetPublisherAsync(string publisherName)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<PublisherRow>(RequiredSql(Statement.PublisherRead),
            new { Name = publisherName });
        return row?.Read();
    }

    public async Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        return (await connection.QueryAsync<PublisherRow>(RequiredSql(Statement.Publishers)))
            .OrderBy(row => row.Name, StringComparer.Ordinal).Select(row => row.Read()).ToArray();
    }

    public async Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxMark);
            // Retention rechecks eligibility under this same lock before deleting outbox rows.
            await LockPublishersAsync(connection, transaction);
            var publisher = await connection.QuerySingleOrDefaultAsync<PublisherRow>(RequiredSql(Statement.PublisherRead),
                new { Name = publisherName }, transaction);
            if (publisher == null || publisher.DisabledAtUtc != null || publisher.UnregisteredAtUtc != null) return false;
            var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(RequiredSql(Statement.DeliveryLock),
                new { EventId = eventId, Name = publisherName }, transaction);
            if (row == null || row.Status != 2) return false;
            var now = await OutboxNowAsync(connection, transaction);
            row.Status = 0;
            row.Attempts = 0;
            row.FirstAttemptAtUtc = null;
            row.NextAttemptAtUtc = now;
            row.LeaseToken = null;
            row.LeaseUntilUtc = null;
            row.LastError = null;
            row.GivenUpReason = null;
            row.RetriedAtUtc = now;
            row.RetriedBy = actor;
            await UpdateDeliveryAsync(connection, transaction, row);
            await CompleteAsync(transaction);
            return true;
        });
    }

    public async Task<bool> EnablePublisherAsync(string publisherName, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.OutboxMark);
            await LockPublishersAsync(connection, transaction);
            var publisher = await connection.QuerySingleOrDefaultAsync<PublisherRow>(RequiredSql(Statement.PublisherRead),
                new { Name = publisherName }, transaction);
            if (publisher == null || publisher.UnregisteredAtUtc != null || publisher.DisabledAtUtc == null) return false;
            var now = await OutboxNowAsync(connection, transaction);
            var parameters = WithTimes(new { Name = publisherName, Actor = actor }, ("Now", now));
            await connection.ExecuteAsync(RequiredSql(Statement.EnablePublisher), parameters, transaction);
            // Bound each update while keeping enable and all its retries atomic with retention.
            while (await connection.ExecuteAsync(RequiredSql(Statement.RetryDisabledDeliveries), parameters, transaction) == 1000) { }
            await CompleteAsync(transaction);
            return true;
        });
    }

    public async Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Validate();
        var parameters = new DynamicParameters(new { Limit = query.Limit + 1 });
        string D(string column) => "d." + SearchColumn(column);
        var filters = new List<string>();
        void Filter(string column, string parameter, object? value)
        {
            if (value == null) return;
            filters.Add($"{D(column)}=@{parameter}");
            parameters.Add(parameter, value);
        }
        Filter("publisher_name", "Publisher", query.PublisherName);
        Filter("status", "Status", query.Status is { } status ? (int)status : null);
        if (query.InstanceId != null)
        {
            filters.Add($"e.{SearchColumn("instance_id")}=@InstanceId");
            parameters.Add("InstanceId", query.InstanceId);
        }
        if (query.NeverAttempted) filters.Add($"{D("attempts")}=0 AND {D("first_attempt_at_utc")} IS NULL");
        if (cursor is { } after)
        {
            filters.Add($"({D("event_id")} > @EventId OR ({D("event_id") }=@EventId AND {D("publisher_name")} > @PublisherAfter))");
            parameters.Add("EventId", after.EventId);
            parameters.Add("PublisherAfter", after.Publisher);
        }
        string[] columns = ["event_id", "publisher_name", "attempts", "first_attempt_at_utc", "next_attempt_at_utc",
            "status", "last_error", "given_up_reason", "retried_at_utc", "retried_by"];
        string[] aliases = ["EventId", "PublisherName", "Attempts", "FirstAttemptAtUtc", "NextAttemptAtUtc",
            "Status", "LastError", "GivenUpReason", "RetriedAtUtc", "RetriedBy"];
        var projection = string.Join(", ", columns.Select((column, i) => $"{D(column)} AS {aliases[i]}"));
        var sql = $"SELECT {SearchLimitPrefix} {projection}, e.{SearchColumn("event_json")} AS EventJson " +
            $"FROM {DeliveryTable} d JOIN {OutboxEventTable} e ON e.{SearchColumn("event_id")}={D("event_id")} " +
            (filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters)) +
            $" ORDER BY {D("event_id")}, {D("publisher_name")} {SearchLimitSuffix}";
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<DeliveryRow>(sql, parameters);
        return query.Page(rows.Select(row => DeliverySummary.From(row.Read(default))).ToArray());
    }
}
