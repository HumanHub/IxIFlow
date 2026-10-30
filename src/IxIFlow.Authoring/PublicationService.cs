using System.Collections.Concurrent;
using IxIFlow.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Authoring;

/// <summary>Owns durable publication work. Caller cancellation only stops waiting.</summary>
public sealed class PublicationService : IHostedService, IDisposable
{
    private readonly IWorkflowCatalogStore _catalog;
    private readonly WorkflowPublisher _publisher;
    private readonly WorkflowPublisherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicationService> _logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _running = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopToken;
    private int _disposed;

    private TimeSpan CatalogTimeout => PublicationMetadata.Timeout(_options);

    public Guid HostId { get; } = Guid.NewGuid();

    public PublicationService(IWorkflowCatalogStore catalog, WorkflowPublisher publisher, WorkflowPublisherOptions? options = null,
        TimeProvider? timeProvider = null, ILogger<PublicationService>? logger = null)
    {
        _catalog = catalog;
        _publisher = publisher;
        _options = options ?? new();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PublicationService>.Instance;
        _stopToken = _stopping.Token;
        TimerIntervals.Validate(_options.PublicationHeartbeatInterval, nameof(_options.PublicationHeartbeatInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PublicationStaleAfter, _options.PublicationHeartbeatInterval);
    }

    /// <summary>Starts or joins publication. The completion wait uses elapsed real time; caller cancellation only stops waiting.</summary>
    public async Task<WorkflowPublication> PublishAsync(WorkflowDraftId draftId, TimeSpan wait,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wait, TimeSpan.FromDays(1));
        cancellationToken.ThrowIfCancellationRequested();
        _stopToken.ThrowIfCancellationRequested();

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopToken);

        var attempt = await CatalogCallAsync(token =>
                _catalog.BeginPublicationAsync(draftId, HostId, actor, _options.PublicationStaleAfter, cancellationToken: token),
            request.Token);

        if (attempt.State != WorkflowPublicationState.Running)
            return attempt;

        Task? completion = null;

        if (attempt.HostId == HostId)
        {
            var work = _running.GetOrAdd(attempt.Id,
                _ => new Lazy<Task>(() => Start(attempt), LazyThreadSafetyMode.ExecutionAndPublication));

            completion = work.Value;
        }

        if (wait == TimeSpan.Zero)
            return attempt;

        // Request budgets are elapsed real time. The injected catalog clock drives heartbeats
        // and stale ownership, and may be stationary while another host completes the attempt.
        using var deadline = new CancellationTokenSource(wait);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(request.Token, deadline.Token);

        try
        {
            if (completion != null)
                await completion.WaitAsync(waiting.Token).ConfigureAwait(false);

            while (true)
            {
                attempt = await _catalog.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted, cancellationToken: waiting.Token)
                        .WaitAsync(waiting.Token)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                        "The active publication attempt is unavailable");

                if (attempt.State != WorkflowPublicationState.Running || completion != null)
                    return attempt;

                await Task.Delay(TimeSpan.FromMilliseconds(100), waiting.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !request.IsCancellationRequested)
        {
            return attempt;
        }
    }

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) =>
        CatalogCallAsync(token => _catalog.GetPublicationAsync(id, scope, cancellationToken: token), _stopToken);

    private Task Start(WorkflowPublication attempt)
    {
        using (ExecutionContextFlow.SuppressIfNeeded())
            return Task.Run(() => RunAsync(attempt));
    }

    private async Task RunAsync(WorkflowPublication attempt)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
        var heartbeat = HeartbeatAsync(attempt, work);

        try
        {
            var current = await CatalogCallAsync(token =>
                _catalog.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted, cancellationToken: token), work.Token);

            if (current?.State != WorkflowPublicationState.Running || current.HostId != HostId)
                return;

            using var scope = _logger.BeginScope(new Dictionary<string, object> { ["PublicationAttemptId"] = attempt.Id });
            WorkflowPublicationOutcome outcome;

            try
            {
                // PublishAsync checks the exact durable receipt before reading a possibly consumed draft.
                var result = await _publisher.PublishAsync(attempt.Draft, attempt.StartedBy, work.Token, CatalogTimeout);

                if (!result.Success)
                    result = await CatalogCallAsync(token => _publisher.FindPublishedAsync(attempt.Draft, token), work.Token) ?? result;

                outcome = new(result.Receipt, result.Diagnostics.Select(item => new WorkflowPublicationDiagnostic(
                    item.Stage.ToString(), item.Code, item.Message, item.Severity.ToString(), item.Path, item.Line, item.Column,
                    item.Class)).ToArray(), result.FailureClass);

                if (outcome.Receipt == null && outcome.FailureClass == null)
                    outcome = Failure(WorkflowPublishFailures.Classify(new InvalidOperationException()));
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _logger.LogError(error, "Publication attempt {AttemptId} failed", attempt.Id);
                outcome = Failure(WorkflowPublishFailures.Classify(error));
            }

            if (!work.IsCancellationRequested)
                await CatalogCallAsync(token => _catalog.CompletePublicationAsync(attempt.Id, HostId, outcome,
                    cancellationToken: token), work.Token);
        }
        catch (CatalogPreconditionException)
        {
            _logger.LogInformation("Publication attempt {AttemptId} lost ownership", attempt.Id);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _logger.LogError(error, "Could not persist publication attempt {AttemptId}; a later owner will reconcile its receipt",
                attempt.Id);
        }
        finally
        {
            await work.CancelAsync();
            await heartbeat;
            _running.TryRemove(attempt.Id, out _);
        }
    }

    private async Task HeartbeatAsync(WorkflowPublication attempt, CancellationTokenSource work)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.PublicationHeartbeatInterval, _time, work.Token);

                if (!await CatalogCallAsync(token => _catalog.HeartbeatPublicationAsync(attempt.Id, HostId,
                        cancellationToken: token), work.Token))
                {
                    await work.CancelAsync();

                    return;
                }
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _logger.LogError(error, "Could not heartbeat publication attempt {AttemptId}", attempt.Id);
            await work.CancelAsync();
        }
    }

    private static WorkflowPublicationOutcome Failure(WorkflowPublishDiagnostic item) => new(null,
        [new(item.Stage.ToString(), item.Code, item.Message, item.Severity.ToString(), item.Path, item.Line, item.Column, item.Class)],
        item.Class);

    private Task<T> CatalogCallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken) =>
        PublicationMetadata.CallAsync(call, CatalogTimeout, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _stopping.CancelAsync();

        await Task.WhenAll(_running.Values.Where(item => item.IsValueCreated).Select(item => item.Value))
            .WaitAsync(CatalogTimeout, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stopping.Cancel();
        _stopping.Dispose();
    }
}