using System.Diagnostics;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

internal sealed partial class WorkflowRecoveryService
{
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _unschedulableWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, int Version), (long Count, DateTime Oldest)> _unschedulableTotals = [];
    private HashSet<(string Name, int Version)> _reportedUnschedulable = [];
    private UnschedulableCursor? _unschedulableCursor;

    internal async Task ScanUnschedulableOnceAsync(CancellationToken token)
    {
        // Every host checks, but only the lowest live host ID reports a shared store's backlog.
        var members = await repository.GetLiveMembersAsync(token);
        if (members.Where(m => m.Kind == MemberKind.Host).OrderBy(m => m.Id).FirstOrDefault()?.Id != _settings.Id)
        {
            _unschedulableCursor = null;
            _unschedulableTotals.Clear();
            return;
        }
        var localNow = _time.GetUtcNow().UtcDateTime;
        foreach (var id in _unschedulableWarnings.Where(pair => localNow - pair.Value >= WarningInterval)
                     .Select(pair => pair.Key).ToArray()) _unschedulableWarnings.Remove(id);

        // A large backlog continues next tick. No tick performs an unbounded global scan.
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            var page = await repository.GetUnschedulableAsync(100, _unschedulableCursor, token);
            var commands = await repository.GetCancellationRequestsAsync(page.Items.Select(row => row.InstanceId).ToArray());
            foreach (var row in page.Items)
            {
                token.ThrowIfCancellationRequested();
                if (commands.GetValueOrDefault(row.InstanceId)?.Mode == WorkflowControlMode.Terminate)
                {
                    // The accepting process may be gone and no host may meet placement. This
                    // bounded scan uses a host slot and the dedicated fenced termination claim.
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<WorkflowHost>().RunAsync(null,
                            (client, _, _) => client.FinishTerminationAsync(row.InstanceId, row.Incarnation), token))
                        continue;
                }
                var key = (row.WorkflowName, row.WorkflowVersion);
                var previous = _unschedulableTotals.GetValueOrDefault(key, (Count: 0L, Oldest: DateTime.MaxValue));
                _unschedulableTotals[key] = (previous.Count + 1,
                    previous.Oldest < row.RunnableAtUtc ? previous.Oldest : row.RunnableAtUtc);
                if (_unschedulableWarnings.TryAdd(row.InstanceId, localNow))
                    logger.LogWarning("Workflow {InstanceId} ({WorkflowName}@{WorkflowVersion}) is unschedulable; unmet demands: {Demands}",
                        row.InstanceId, row.WorkflowName, row.WorkflowVersion,
                        string.Join(", ", row.UnmetDemands.Select(d => d.Value == null ? d.Key : $"{d.Key}={d.Value}")));
            }
            _unschedulableCursor = page.NextCursor;
            if (page.NextCursor != null) continue;
            foreach (var key in _reportedUnschedulable.Union(_unschedulableTotals.Keys))
            {
                var value = _unschedulableTotals.GetValueOrDefault(key);
                var tags = new TagList { { Telemetry.WorkflowName, key.Name }, { Telemetry.WorkflowVersion, key.Version } };
                Telemetry.Unschedulable.Record(value.Count, tags);
                Telemetry.UnschedulableAge.Record(value.Count == 0 ? 0 :
                    Math.Max(0, (page.StoreTimeUtc - value.Oldest).TotalSeconds), tags);
            }
            _reportedUnschedulable = _unschedulableTotals.Keys.ToHashSet();
            _unschedulableTotals.Clear();
            return;
        }
    }

    private async Task UnschedulableAsync(CancellationToken token)
    {
        try
        {
            // Spread checks across the interval when many hosts start together.
            await Task.Delay(TimeSpan.FromTicks((long)(_settings.UnschedulableScanInterval.Ticks * Random.Shared.NextDouble())),
                _time, token);
            using var timer = new PeriodicTimer(_settings.UnschedulableScanInterval, _time);
            do
            {
                try { await ScanUnschedulableOnceAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // Restart a failed cycle rather than counting a partially consumed page twice.
                    _unschedulableCursor = null;
                    _unschedulableTotals.Clear();
                    (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Error, error, "Could not scan unschedulable workflows");
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
