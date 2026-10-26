using System.Data.Common;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private sealed class PublicationHeadRow
    {
        public Guid? ActiveId { get; set; }
        public int LastAttempt { get; set; }
    }

    private sealed class PublicationRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public Guid Generation { get; set; }
        public long Revision { get; set; }
        public int Attempt { get; set; }
        public string State { get; set; } = "";
        public Guid HostId { get; set; }
        public string? StartedBy { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime HeartbeatAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public string? OutcomeJson { get; set; }
        public WorkflowPublication Value => new(Id, new(Kind, Name, Version, Generation, Revision), Attempt,
            Enum.Parse<WorkflowPublicationState>(State), HostId, StartedBy, Utc(StartedAtUtc), Utc(HeartbeatAtUtc),
            FinishedAtUtc is { } finish ? Utc(finish) : null,
            OutcomeJson == null ? null : JsonSerializer.Deserialize<WorkflowPublicationOutcome>(OutcomeJson));
    }

    private object PublicationParameters(WorkflowPublication row, long revision) => WithTimes(new
    {
        row.Id, row.Draft.Kind, row.Draft.Name, row.Draft.Version, row.Draft.Generation, row.Draft.Revision,
        row.Attempt, State = row.State.ToString(), row.HostId, row.StartedBy,
        OutcomeJson = row.Outcome == null ? null : JsonSerializer.Serialize(row.Outcome), CatalogRevision = revision
    }, ("StartedAtUtc", row.StartedAtUtc), ("HeartbeatAtUtc", row.HeartbeatAtUtc), ("FinishedAtUtc", row.FinishedAtUtc));

    // Values are bound parameters. Prefix matching is literal and uses each key column's ordinal collation.
    internal virtual int CatalogPrefixLength(string value) => value.EnumerateRunes().Count();

    private string ScopePredicate(CatalogScope scope, DynamicParameters parameters)
    {
        List<string> kinds = [];
        foreach (var kind in new[] { WorkflowDraftKinds.Workflow, WorkflowDraftKinds.Activity, WorkflowDraftKinds.Template })
        {
            var names = scope.For(kind);
            var key = "scope" + kinds.Count;
            parameters.Add(key, kind, DbType.AnsiString, size: 10);
            List<string> terms = [];
            if (names.Names == null && names.Prefixes == null) terms.Add("1=1");
            foreach (var name in names.Names ?? [])
            {
                var param = key + "n" + terms.Count;
                parameters.Add(param, name);
                terms.Add("Name=@" + param);
            }
            foreach (var prefix in names.Prefixes ?? [])
            {
                var param = key + "p" + terms.Count;
                parameters.Add(param, prefix);
                parameters.Add(param + "Length", CatalogPrefixLength(prefix));
                terms.Add("SUBSTRING(Name,1,@" + param + "Length)=@" + param);
            }
            kinds.Add("(Kind=@" + key + " AND (" + (terms.Count == 0 ? "1=0" : string.Join(" OR ", terms)) + "))");
        }
        return string.Join(" OR ", kinds);
    }

    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query)
    {
        WorkflowPublicationRules.Query(query);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, query.Kind, query.NamePrefix });
        var key = CatalogPaging.Decode(query.After, filter, 3, query.Limit);
        int? afterVersion = key == null ? null : int.TryParse(key[2], out var parsed) ? parsed : throw new ArgumentException("Invalid draft cursor");
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var parameters = DraftPageParameters(query, key, afterVersion);
            var sql = RequiredSql(Statement.CatalogDefinitionPage).Replace("{scope}", ScopePredicate(query.Scope, parameters));
            var rows = (await connection.QueryAsync<DraftRow>(sql, parameters, transaction)).ToArray();
            var page = rows.Take(query.Limit).Select(row => row.Summary).ToArray();
            var last = page.LastOrDefault();
            return new CatalogPage<WorkflowDraftSummary>(page, rows.Length > query.Limit && last != null
                ? CatalogPaging.Encode(filter, last.Kind, last.Name, last.Version.ToString(CultureInfo.InvariantCulture)) : null,
                await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    private DynamicParameters DraftPageParameters(WorkflowDraftQuery query, string[]? key, int? afterVersion)
    {
        var parameters = new DynamicParameters(new { query.NamePrefix, PrefixLength = query.NamePrefix == null ? (int?)null : CatalogPrefixLength(query.NamePrefix),
            Limit = query.Limit + 1, AfterName = key?[1], AfterVersion = afterVersion });
        // Match the indexed SQL Server key types, including the binary cursor's declared length.
        parameters.Add("Kind", query.Kind, DbType.AnsiString, size: 10);
        parameters.Add("AfterKind", key?[0], DbType.AnsiString, size: 10);
        parameters.Add("AfterNameOrder", key == null ? null : Encoding.UTF8.GetBytes(key[1]), DbType.Binary, size: 800);
        return parameters;
    }

    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope)
    {
        WorkflowPublicationRules.Key(WorkflowDraftKinds.Workflow, name, version);
        ArgumentNullException.ThrowIfNull(scope);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            if (!scope.Allows(WorkflowDraftKinds.Workflow, name)) return new RetirementBlockers([], null, true);
            var callers = (await ReadVersionsAsync(connection, transaction)).Where(row =>
                (row.Dependencies ?? []).Any(edge => edge.Name == name && edge.Version == version)).ToArray();
            var unfinished = callers.Length == 0 ? null : await connection.QuerySingleOrDefaultAsync<UnfinishedCallerRow>(
                RequiredSql(Statement.CatalogUnfinishedCallers), new { Callers = JsonSerializer.Serialize(callers.Select(row => new WorkflowVersionReference(row.Name, row.Version))) }, transaction);
            return WorkflowPublicationRules.Mask(callers.Where(row => (row.Name, row.Version) != (name, version) && row.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated), unfinished == null ? null : new(unfinished.Name, unfinished.Version), scope);
        }, protectsStarts: false);
    }

    private async Task<WorkflowPublication?> ReadPublicationAsync(DbConnection connection, DbTransaction transaction, Guid id) =>
        (await connection.QuerySingleOrDefaultAsync<PublicationRow>(RequiredSql(Statement.Publication), new { Id = id }, transaction))?.Value;

    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter)
    {
        WorkflowPublicationRules.Id(draftId);
        WorkflowPublicationRules.Actor(actor);
        if (hostId == Guid.Empty) throw new ArgumentException("A host identity is required", nameof(hostId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var head = await connection.QuerySingleOrDefaultAsync<PublicationHeadRow>(RequiredSql(Statement.PublicationHead), draftId, transaction);
            var prior = head?.ActiveId is { } active ? await ReadPublicationAsync(connection, transaction, active) : null;
            var now = await OutboxNowAsync(connection, transaction);
            if (prior?.State == WorkflowPublicationState.Published || prior?.State == WorkflowPublicationState.Running && prior.HeartbeatAtUtc > now - staleAfter)
                return prior;
            var stored = (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion), draftId, transaction))?.Value;
            var draft = await connection.QuerySingleOrDefaultAsync<DraftRow>(RequiredSql(Statement.CatalogDefinition), draftId, transaction);
            var canStart = draft?.Value.Id == draftId || stored?.PublishedDraft == draftId ||
                draftId.Kind == WorkflowDraftKinds.Template && (await ReadTemplateAsync(connection, transaction, draftId.Name, draftId.Version))?.PublishedDraft == draftId ||
                draftId.Kind == WorkflowDraftKinds.Activity && (await ReadPackageRowAsync(connection, transaction, draftId.Name, PackageIndexRules.PublishedVersion(draftId)))?.Value.PublishedDraft == draftId;
            if (!canStart && prior?.State is not (WorkflowPublicationState.Running or WorkflowPublicationState.Abandoned))
                throw new CatalogPreconditionException("The definition identity changed");
            if (!canStart && prior?.State == WorkflowPublicationState.Abandoned) return prior;
            var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
            if (prior?.State == WorkflowPublicationState.Running)
            {
                prior = prior with { State = WorkflowPublicationState.Abandoned, FinishedAtUtc = now };
                await connection.ExecuteAsync(RequiredSql(Statement.PublicationUpdate), PublicationParameters(prior, revision), transaction);
            }
            // Commit abandonment without creating work against an edited/deleted draft.
            if (!canStart) return prior!;
            var row = new WorkflowPublication(Guid.NewGuid(), draftId, checked((head?.LastAttempt ?? 0) + 1), WorkflowPublicationState.Running,
                hostId, actor, now, now, null, null);
            await connection.ExecuteAsync(RequiredSql(Statement.PublicationInsert), PublicationParameters(row, revision), transaction);
            await connection.ExecuteAsync(RequiredSql(head == null ? Statement.PublicationHeadInsert : Statement.PublicationHeadUpdate), new
            { draftId.Kind, draftId.Name, draftId.Version, draftId.Generation, draftId.Revision, ActiveId = row.Id, LastAttempt = row.Attempt }, transaction);
            return row;
        }, protectsStarts: false);
    }

    private async Task<WorkflowPublication?> OwnedPublicationAsync(DbConnection connection, DbTransaction transaction, Guid id, Guid hostId)
    {
        var row = await ReadPublicationAsync(connection, transaction, id);
        if (row?.State != WorkflowPublicationState.Running || row.HostId != hostId) return null;
        var head = await connection.QuerySingleOrDefaultAsync<PublicationHeadRow>(RequiredSql(Statement.PublicationHead), row.Draft, transaction);
        return head?.ActiveId == id ? row : null;
    }

    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => CatalogWriteAsync(async (connection, transaction) =>
    {
        if (await OwnedPublicationAsync(connection, transaction, id, hostId) is not { } row) return false;
        var now = await OutboxNowAsync(connection, transaction);
        if (row.HeartbeatAtUtc == now) return true;
        var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
        return await connection.ExecuteAsync(RequiredSql(Statement.PublicationHeartbeat), WithTimes(new { Id = id, HostId = hostId, CatalogRevision = revision },
            ("Now", now)), transaction) == 1;
    }, protectsStarts: false);

    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => CatalogWriteAsync(async (connection, transaction) =>
    {
        var row = await OwnedPublicationAsync(connection, transaction, id, hostId)
            ?? throw new CatalogPreconditionException("The publication attempt is no longer owned by this host");
        var stored = (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion), row.Draft, transaction))?.Value;
        outcome = WorkflowPublicationRules.Outcome(row.Draft, outcome, stored,
            row.Draft.Kind == WorkflowDraftKinds.Template ? await ReadTemplateAsync(connection, transaction, row.Draft.Name, row.Draft.Version) : null,
            row.Draft.Kind == WorkflowDraftKinds.Activity ? (await ReadPackageRowAsync(connection, transaction, row.Draft.Name, PackageIndexRules.PublishedVersion(row.Draft)))?.Value : null);
        var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
        var saved = row with { State = outcome.Receipt == null ? WorkflowPublicationState.Failed : WorkflowPublicationState.Published,
            FinishedAtUtc = await OutboxNowAsync(connection, transaction), Outcome = outcome };
        if (await connection.ExecuteAsync(RequiredSql(Statement.PublicationUpdate), PublicationParameters(saved, revision), transaction) != 1)
            throw new CatalogPreconditionException("The publication attempt is no longer owned by this host");
        return WorkflowPublicationRules.Copy(saved);
    }, protectsStarts: false);

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return CatalogConnectionAsync(async (connection, transaction) =>
        {
            var parameters = new DynamicParameters(new { Id = id });
            var sql = RequiredSql(Statement.Publication) + " AND (" + ScopePredicate(scope, parameters) + ")";
            return (await connection.QuerySingleOrDefaultAsync<PublicationRow>(sql, parameters, transaction))?.Value;
        });
    }

    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query)
    {
        WorkflowPublicationRules.Key(query.Kind, query.Name, query.Version);
        ArgumentNullException.ThrowIfNull(query.Scope);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, query.Kind, query.Name, query.Version, query.Generation });
        var key = CatalogPaging.Decode(query.After, filter, 2, query.Limit);
        DateTime? afterTime = key == null ? null : DateTime.TryParse(key[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : throw new ArgumentException("Invalid publication cursor");
        Guid? afterId = key == null ? null : Guid.TryParse(key[1], out var id) ? id : throw new ArgumentException("Invalid publication cursor");
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var revision = await ReadCatalogRevisionAsync(connection, transaction);
            if (!query.Scope.Allows(query.Kind, query.Name)) return new CatalogPage<WorkflowPublication>([], null, revision);
            var parameters = PublicationPageParameters(query, afterTime, afterId);
            var rows = (await connection.QueryAsync<PublicationRow>(RequiredSql(Statement.PublicationPage), parameters, transaction)).Select(row => row.Value).ToArray();
            var page = rows.Take(query.Limit).ToArray();
            var last = page.LastOrDefault();
            return new CatalogPage<WorkflowPublication>(page, rows.Length > query.Limit && last != null
                ? CatalogPaging.Encode(filter, last.StartedAtUtc.ToString("O"), last.Id.ToString("N")) : null, revision);
        }, protectsStarts: false);
    }

    private DynamicParameters PublicationPageParameters(WorkflowPublicationQuery query, DateTime? afterTime, Guid? afterId)
    {
        // UUIDs on PostgreSQL and canonical GUID text on SQLite have the canonical ordinal order.
        // SQL Server uses its persisted varchar key instead of uniqueidentifier's different order.
        var parameters = WithTimes(new { query.Name, query.Version, query.Generation, AfterId = afterId, Limit = query.Limit + 1 }, ("AfterTime", afterTime));
        parameters.Add("Kind", query.Kind, DbType.AnsiString, size: 10);
        parameters.Add("AfterIdOrder", afterId?.ToString("D"), DbType.AnsiString, size: 36);
        return parameters;
    }

    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await connection.QueryAsync<Guid>(new CommandDefinition(RequiredSql(Statement.PublicationPurgeCandidates),
                WithTimes(new { Limit = batchSize }, ("Cutoff", await OutboxNowAsync(connection, transaction) - retention)), transaction, cancellationToken: cancellationToken));
            var removed = 0;
            foreach (var id in ids)
                removed += await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.PublicationDelete), new { Id = id }, transaction, cancellationToken: cancellationToken));
            if (removed > 0) await AdvanceCatalogRevisionAsync(connection, transaction);
            return removed;
        }, protectsStarts: false);
    }
}
