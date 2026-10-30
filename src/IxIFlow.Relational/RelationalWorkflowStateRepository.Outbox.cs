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

    private Task<DateTime> OutboxNowAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        UsesLocalClock ? Task.FromResult(LocalNow()) : ServerNowAsync(connection, transaction, cancellationToken: cancellationToken);

    private async Task<DateTime> ServerNowAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        ReadTime(await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(RequiredSql(Statement.OutboxClock),
            transaction: transaction, cancellationToken: cancellationToken)));

    public async Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(eventTypes);
        RuntimeIdentifiers.ValidateText(name);

        foreach (var eventType in eventTypes)
            RuntimeIdentifiers.ValidateText(eventType);

        if (name.Length > 100 || eventTypes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Publisher names are at most 100 characters; event types cannot be blank");

        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxMark, cancellationToken: cancellationToken);

            await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.PublisherUpsert),
                new { Name = name, TypesJson = JsonSerializer.Serialize(eventTypes.Distinct(StringComparer.Ordinal)) }, transaction,
                cancellationToken: cancellationToken));

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            foreach (var type in replayRetained ? eventTypes.Distinct(StringComparer.Ordinal) : [])
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.BackfillDeliveries),
                    WithTimes(new { Name = name, EventType = type }, ("Now", now)), transaction, cancellationToken: cancellationToken));

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return true;
        }, cancellationToken: cancellationToken);
    }

    // SQLite already holds its immediate writer transaction; no extra round trip is needed.
    private Task LockPublishersAsync(DbConnection connection, DbTransaction? transaction, bool shared = false,
        CancellationToken cancellationToken = default) =>
        Sql(shared ? Statement.PublisherSharedLock : Statement.PublisherLock) is { } sql
            ? connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken))
            : Task.CompletedTask;

    public async Task UnregisterPublisherAsync(string name, string actor = "host", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxMark, cancellationToken: cancellationToken);

            await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);
            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.UnregisterPublisher),
                WithTimes(new { Name = name, Actor = actor }, ("Now", now)), transaction, cancellationToken: cancellationToken));

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return true;
        }, cancellationToken: cancellationToken);
    }

    private async Task RecordCompletionAsync(DbConnection connection, DbTransaction? transaction,
        WorkflowInstance? instance, long revision, DateTime now, CancellationToken cancellationToken = default)
    {
        if (instance == null || !WorkflowStoreRules.IsTerminal(instance.Status))
            return;

        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.StampEnd),
            WithTimes(new { instance.InstanceId }, ("Now", now)), transaction, cancellationToken: cancellationToken));

        instance.Revision = revision;
        await LockPublishersAsync(connection, transaction, shared: true, cancellationToken: cancellationToken);
        var item = OutboxEvent.Completion(instance, now);

        var changed = await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.InsertOutboxEvent), WithTimes(new
        {
            EventId = item.Id, item.InstanceId, EventType = item.Type, EventJson = JsonSerializer.Serialize(item)
        }, ("Now", now)), transaction, cancellationToken: cancellationToken));

        if (changed != 1)
            return;

        var publishers = await connection.QueryAsync<PublisherRow>(new CommandDefinition(RequiredSql(Statement.Publishers),
            transaction: transaction, cancellationToken: cancellationToken));

        foreach (var publisher in publishers)
            if (publisher.UnregisteredAtUtc == null &&
                JsonSerializer.Deserialize<string[]>(publisher.TypesJson)!.Contains(item.Type, StringComparer.Ordinal))
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.InsertDelivery), WithTimes(new
                {
                    EventId = item.Id, Name = publisher.Name,
                    Status = publisher.DisabledAtUtc == null ? 0 : 2, publisher.LastError,
                    GivenUpReason = publisher.DisabledAtUtc == null ? (int?)null : (int)OutboxGivenUpReason.Disabled
                }, ("Now", now)), transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token,
        TimeSpan leaseDuration, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, limit);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync<IReadOnlyList<OutboxDelivery>>(Operation.OutboxClaim, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxClaim, cancellationToken: cancellationToken);

            var rows = (await connection.QueryAsync<DeliveryRow>(new CommandDefinition(RequiredSql(Statement.ClaimDeliveries),
                WithLocalTime(new { Name = publisherName, Limit = limit }), transaction, cancellationToken: cancellationToken))).ToArray();

            // Candidate locks are held before the clock is read; hosts send durations only.
            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            foreach (var row in rows)
            {
                row.Attempts = checked(row.Attempts + 1);
                row.FirstAttemptAtUtc ??= now;
                row.LeaseToken = token;
                row.LeaseUntilUtc = now + leaseDuration;
                await UpdateDeliveryAsync(connection, transaction, row, cancellationToken: cancellationToken);
            }

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return rows.Select(row => row.Read(now)).ToArray();
        }, cancellationToken: cancellationToken);
    }

    private Task UpdateDeliveryAsync(DbConnection connection, DbTransaction? transaction, DeliveryRow row,
        CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeliveryUpdate), WithTimes(new
            {
                row.EventId, Name = row.PublisherName, row.Attempts, row.Status, row.LastError, row.LeaseToken,
                row.GivenUpReason, row.RetriedBy
            }, ("RetriedAtUtc", row.RetriedAtUtc), ("FirstAttemptAtUtc", row.FirstAttemptAtUtc), ("NextAttemptAtUtc", row.NextAttemptAtUtc),
            ("LeaseUntilUtc", row.LeaseUntilUtc)), transaction, cancellationToken: cancellationToken));

    private async Task<bool> ChangeDeliveryAsync(string eventId, string name, string token,
        Func<DeliveryRow, DateTime, Task> change, bool disable = false, string? error = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxMark, cancellationToken: cancellationToken);

            if (disable)
                await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(RequiredSql(Statement.DeliveryLock),
                new { EventId = eventId, Name = name }, transaction, cancellationToken: cancellationToken));

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            if (row == null || row.Status != 0 || row.LeaseToken != token || row.LeaseUntilUtc == null ||
                Utc(row.LeaseUntilUtc.Value) <= now)
                return false;

            await change(row, now);
            await UpdateDeliveryAsync(connection, transaction, row, cancellationToken: cancellationToken);

            if (disable)
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DisablePublisher),
                    WithTimes(new { Name = name, Error = error }, ("Now", now)), transaction, cancellationToken: cancellationToken));

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return true;
        }, cancellationToken: cancellationToken);
    }

    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutboxRetry.ValidateClaim(publisherName, token, leaseDuration, 1);

        return ChangeDeliveryAsync(eventId, publisherName, token, (row, now) =>
        {
            row.LeaseUntilUtc = now + leaseDuration;

            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);
    }

    public async Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);

        var changed = await ChangeDeliveryAsync(eventId, publisherName, token, (row, now) =>
        {
            row.NextAttemptAtUtc = now + OutboxRetry.Delay(row.Attempts, result);

            row.Status = result.DisablePublisher ? 2
                : result.Outcome == OutboxPublishOutcome.Delivered ? 1
                : result.Outcome == OutboxPublishOutcome.GiveUp || result.DisablePublisher ? 2 : 0;

            row.GivenUpReason = result.DisablePublisher
                ? OutboxGivenUpReason.Disabled
                : row.Status == 2
                    ? result.GivenUpReason ?? OutboxGivenUpReason.Exhausted
                    : null;

            row.LastError = result.Error;
            row.LeaseToken = null;
            row.LeaseUntilUtc = null;

            return Task.CompletedTask;
        }, result.DisablePublisher, result.Error, cancellationToken);

        return changed;
    }

    public async Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeIdentifiers.ValidateText(eventId);
        RuntimeIdentifiers.ValidateText(publisherName);

        return await ExecuteStoreAsync<OutboxDelivery?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxRead, cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(RequiredSql(Statement.DeliveryRead),
                new { EventId = eventId, Name = publisherName }, transaction, cancellationToken: cancellationToken));

            return row?.Read(await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    public async Task<OutboxPublisherState?> GetPublisherAsync(string publisherName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeIdentifiers.ValidateText(publisherName);

        return await ExecuteStoreAsync<OutboxPublisherState?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<PublisherRow>(new CommandDefinition(RequiredSql(Statement.PublisherRead),
                new { Name = publisherName }, cancellationToken: cancellationToken));

            return row?.Read();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await ExecuteStoreAsync<IReadOnlyList<OutboxPublisherState>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            return (await connection.QueryAsync<PublisherRow>(new CommandDefinition(RequiredSql(Statement.Publishers),
                    cancellationToken: cancellationToken)))
                .OrderBy(row => row.Name, StringComparer.Ordinal).Select(row => row.Read()).ToArray();
        }, cancellationToken);
    }

    public async Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxMark, cancellationToken: cancellationToken);

            // Retention rechecks eligibility under this same lock before deleting outbox rows.
            await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);

            var publisher = await connection.QuerySingleOrDefaultAsync<PublisherRow>(new CommandDefinition(
                RequiredSql(Statement.PublisherRead),
                new { Name = publisherName }, transaction, cancellationToken: cancellationToken));

            if (publisher == null || publisher.DisabledAtUtc != null || publisher.UnregisteredAtUtc != null)
                return false;

            var row = await connection.QuerySingleOrDefaultAsync<DeliveryRow>(new CommandDefinition(RequiredSql(Statement.DeliveryLock),
                new { EventId = eventId, Name = publisherName }, transaction, cancellationToken: cancellationToken));

            if (row == null || row.Status != 2)
                return false;

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
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
            await UpdateDeliveryAsync(connection, transaction, row, cancellationToken: cancellationToken);
            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.OutboxMark, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.OutboxMark, cancellationToken: cancellationToken);

            await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);

            var publisher = await connection.QuerySingleOrDefaultAsync<PublisherRow>(new CommandDefinition(
                RequiredSql(Statement.PublisherRead),
                new { Name = publisherName }, transaction, cancellationToken: cancellationToken));

            if (publisher == null || publisher.UnregisteredAtUtc != null || publisher.DisabledAtUtc == null)
                return false;

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
            var parameters = WithTimes(new { Name = publisherName, Actor = actor }, ("Now", now));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.EnablePublisher), parameters, transaction,
                cancellationToken: cancellationToken));

            // Bound each update while keeping enable and all its retries atomic with retention.
            while (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.RetryDisabledDeliveries), parameters,
                       transaction, cancellationToken: cancellationToken)) == 1000)
            {
            }

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Validate();
        var parameters = new DynamicParameters(new { Limit = query.Limit + 1 });
        string D(string column) => "d." + SearchColumn(column);
        var filters = new List<string>();

        void Filter(string column, string parameter, object? value)
        {
            if (value == null)
                return;

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

        if (query.NeverAttempted)
            filters.Add($"{D("attempts")}=0 AND {D("first_attempt_at_utc")} IS NULL");

        if (cursor is { } after)
        {
            filters.Add($"({D("event_id")} > @EventId OR ({D("event_id")}=@EventId AND {D("publisher_name")} > @PublisherAfter))");
            parameters.Add("EventId", after.EventId);
            parameters.Add("PublisherAfter", after.Publisher);
        }

        string[] columns =
        [
            "event_id", "publisher_name", "attempts", "first_attempt_at_utc", "next_attempt_at_utc",
            "status", "last_error", "given_up_reason", "retried_at_utc", "retried_by"
        ];

        string[] aliases =
        [
            "EventId", "PublisherName", "Attempts", "FirstAttemptAtUtc", "NextAttemptAtUtc",
            "Status", "LastError", "GivenUpReason", "RetriedAtUtc", "RetriedBy"
        ];

        var projection = string.Join(", ", columns.Select((column, i) => $"{D(column)} AS {aliases[i]}"));

        var sql = $"SELECT {SearchLimitPrefix} {projection}, e.{SearchColumn("event_json")} AS EventJson " +
            $"FROM {DeliveryTable} d JOIN {OutboxEventTable} e ON e.{SearchColumn("event_id")}={D("event_id")} " +
            (filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters)) +
            $" ORDER BY {D("event_id")}, {D("publisher_name")} {SearchLimitSuffix}";

        return await ExecuteStoreAsync<DeliveryPage>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<DeliveryRow>(new CommandDefinition(sql, parameters,
                cancellationToken: cancellationToken));

            return query.Page(rows.Select(row => DeliverySummary.From(row.Read(default))).ToArray());
        }, cancellationToken);
    }
}