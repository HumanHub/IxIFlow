namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<Guid, WorkflowPublication> _publications = [];
    private readonly Dictionary<WorkflowDraftId, (Guid? Active, int Last)> _publicationHeads = [];

    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope)
    {
        WorkflowPublicationRules.Key(WorkflowDraftKinds.Workflow, name, version);
        ArgumentNullException.ThrowIfNull(scope);
        lock (_claimLock)
        {
            if (!scope.Allows(WorkflowDraftKinds.Workflow, name)) return Task.FromResult(new RetirementBlockers([], null, true));
            var callers = _versions.Values.Where(row => (row.Dependencies ?? []).Any(edge => edge.Name == name && edge.Version == version)).ToArray();
            var pairs = callers.Select(row => new WorkflowVersionReference(row.Name, row.Version)).ToHashSet();
            return Task.FromResult(WorkflowPublicationRules.Mask(callers.Where(row => (row.Name, row.Version) != (name, version) && row.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated), FindUnfinishedCaller(_instances.Values, pairs), scope));
        }
    }

    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter)
    {
        WorkflowPublicationRules.Id(draftId);
        WorkflowPublicationRules.Actor(actor);
        if (hostId == Guid.Empty) throw new ArgumentException("A host identity is required", nameof(hostId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        lock (_claimLock)
        {
            var head = _publicationHeads.GetValueOrDefault(draftId);
            var prior = head.Active is { } active ? _publications.GetValueOrDefault(active) : null;
            var now = Now;
            if (prior?.State == WorkflowPublicationState.Published || prior?.State == WorkflowPublicationState.Running && prior.HeartbeatAtUtc > now - staleAfter)
                return Task.FromResult(WorkflowPublicationRules.Copy(prior));
            var stored = _versions.GetValueOrDefault((draftId.Name, draftId.Version));
            var canStart = _definitions.GetValueOrDefault((draftId.Kind, draftId.Name, draftId.Version))?.Id == draftId || stored?.PublishedDraft == draftId;
            if (!canStart && prior?.State is not (WorkflowPublicationState.Running or WorkflowPublicationState.Abandoned))
                throw new CatalogPreconditionException("The definition identity changed");
            if (prior?.State == WorkflowPublicationState.Running)
            {
                prior = prior with { State = WorkflowPublicationState.Abandoned, FinishedAtUtc = now };
                _publications[prior.Id] = prior;
                if (!canStart) _catalogRevision = checked(_catalogRevision + 1);
            }
            // Recover a lost owner even when its draft no longer permits replacement work.
            if (!canStart) return Task.FromResult(WorkflowPublicationRules.Copy(prior!));
            var row = new WorkflowPublication(Guid.NewGuid(), draftId, checked(head.Last + 1), WorkflowPublicationState.Running,
                hostId, actor, now, now, null, null);
            _publicationHeads[draftId] = (row.Id, row.Attempt);
            // A committed receipt is reconciled by CompletePublicationAsync, never by a scoped read.
            _publications.Add(row.Id, row);
            _catalogRevision = checked(_catalogRevision + 1);
            return Task.FromResult(WorkflowPublicationRules.Copy(row));
        }
    }

    private WorkflowPublication? OwnedPublication(Guid id, Guid hostId) =>
        _publications.TryGetValue(id, out var row) && row.HostId == hostId && row.State == WorkflowPublicationState.Running &&
        _publicationHeads.GetValueOrDefault(row.Draft).Active == id ? row : null;

    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId)
    {
        lock (_claimLock)
        {
            if (OwnedPublication(id, hostId) is not { } row) return Task.FromResult(false);
            var now = Now;
            if (row.HeartbeatAtUtc != now)
            {
                _publications[id] = row with { HeartbeatAtUtc = now };
                _catalogRevision = checked(_catalogRevision + 1);
            }
            return Task.FromResult(true);
        }
    }

    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome)
    {
        lock (_claimLock)
        {
            var row = OwnedPublication(id, hostId) ?? throw new CatalogPreconditionException("The publication attempt is no longer owned by this host");
            outcome = WorkflowPublicationRules.Outcome(row.Draft, outcome, _versions.GetValueOrDefault((row.Draft.Name, row.Draft.Version)));
            row = WorkflowPublicationRules.Copy(row with { State = outcome.Receipt == null ? WorkflowPublicationState.Failed : WorkflowPublicationState.Published,
                FinishedAtUtc = Now, Outcome = outcome });
            _publications[id] = row;
            _catalogRevision = checked(_catalogRevision + 1);
            return Task.FromResult(WorkflowPublicationRules.Copy(row));
        }
    }

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_claimLock) return Task.FromResult(_publications.TryGetValue(id, out var row) && scope.Allows(row.Draft.Kind, row.Draft.Name)
            ? WorkflowPublicationRules.Copy(row) : null);
    }

    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query)
    {
        WorkflowPublicationRules.Key(query.Kind, query.Name, query.Version);
        ArgumentNullException.ThrowIfNull(query.Scope);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, query.Kind, query.Name, query.Version, query.Generation });
        var key = CatalogPaging.Decode(query.After, filter, 2, query.Limit);
        var afterTime = key == null ? DateTime.MinValue : DateTime.TryParse(key[0], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : throw new ArgumentException("Invalid publication cursor");
        if (key != null && !Guid.TryParse(key[1], out _)) throw new ArgumentException("Invalid publication cursor");
        lock (_claimLock)
        {
            var rows = _publications.Values.Where(row => query.Scope.Allows(row.Draft.Kind, row.Draft.Name) &&
                (row.Draft.Kind, row.Draft.Name, row.Draft.Version) == (query.Kind, query.Name, query.Version) &&
                (query.Generation == null || row.Draft.Generation == query.Generation) &&
                (key == null || row.StartedAtUtc > afterTime || row.StartedAtUtc == afterTime && string.CompareOrdinal(row.Id.ToString("N"), key[1]) > 0))
                .OrderBy(row => row.StartedAtUtc).ThenBy(row => row.Id.ToString("N"), StringComparer.Ordinal).Take(query.Limit + 1).ToArray();
            var page = rows.Take(query.Limit).Select(WorkflowPublicationRules.Copy).ToArray();
            var last = page.LastOrDefault();
            return Task.FromResult(new CatalogPage<WorkflowPublication>(page, rows.Length > query.Limit && last != null
                ? CatalogPaging.Encode(filter, last.StartedAtUtc.ToString("O"), last.Id.ToString("N")) : null, _catalogRevision));
        }
    }

    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_claimLock)
        {
            var ids = _publications.Values.Where(row => row.State != WorkflowPublicationState.Running && row.FinishedAtUtc < Now - retention)
                .OrderBy(row => row.FinishedAtUtc).ThenBy(row => row.Id.ToString("N"), StringComparer.Ordinal).Take(batchSize).Select(row => row.Id).ToArray();
            foreach (var id in ids) _publications.Remove(id);
            if (ids.Length > 0) _catalogRevision = checked(_catalogRevision + 1);
            return Task.FromResult(ids.Length);
        }
    }
}
