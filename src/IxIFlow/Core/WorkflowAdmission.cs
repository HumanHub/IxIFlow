using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Trusted identity and request identity supplied by an ingress adapter.</summary>
public sealed record WorkflowAdmission(string Sender, string Trigger, string Key, string Fingerprint,
    string Owner, string? Tenant = null, TimeSpan? Retention = null, WorkflowOwnerKind OwnerKind = WorkflowOwnerKind.Principal)
{
    public const int MaxKeyLength = 128;
    public TimeSpan KeyRetention => Retention ?? TimeSpan.FromHours(24);

    public void Validate()
    {
        foreach (var value in new[] { Sender, Trigger, Key, Owner })
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxKeyLength || value != value.Trim())
                throw new ArgumentException("Admission identities must contain 1 to 128 characters");
        if (Tenant?.Length > 200 || Fingerprint == null || Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Admission requires a SHA-256 fingerprint and a tenant of at most 200 characters");
        if (KeyRetention <= TimeSpan.Zero || KeyRetention > CheckpointCommit.MaxDuration)
            throw new ArgumentOutOfRangeException(nameof(Retention));
        if (!Enum.IsDefined(OwnerKind)) throw new ArgumentOutOfRangeException(nameof(OwnerKind));
    }

    public void ValidateCommit(CheckpointCommit commit)
    {
        Validate();
        if (commit.Epoch != 0 || commit.ExpectedRevision != 0 || commit.Instance.Status != WorkflowStatus.Running ||
            commit.ChildStarts.Count != 0 || commit.ParentNotice != null || commit.Settlements.Count != 0 || commit.Parks)
            throw new ArgumentException("Admission requires a new, unowned running instance's first commit");
    }
}

public sealed record WorkflowAdmissionRecord(string Sender, string Trigger, string Key, string Fingerprint,
    string InstanceId, string WorkflowName, int WorkflowVersion, DateTime ExpiresAtUtc, Guid Incarnation = default)
{
    /// <summary>The original admission's hint, retained for acknowledgement retries.</summary>
    public RunnableNotice? Notice { get; init; }
}

/// <summary>Retained with the instance and its removal tombstone, independently of key expiry.</summary>
public sealed record WorkflowOwnership(string InstanceId, string Sender, string Trigger, string Owner, string? Tenant,
    Guid Incarnation = default, WorkflowOwnerKind OwnerKind = WorkflowOwnerKind.Principal);

public enum WorkflowOwnerKind { Principal, SignedSender }

/// <summary>Atomic admission through the store's checkpoint commit protocol.</summary>
public interface IWorkflowAdmissionStore
{
    Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission);
    Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId);
    Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key);
}

/// <summary>Acceptance-only operations. Acknowledgement always follows durable storage.</summary>
public interface IWorkflowIngressClient
{
    Task<WorkflowAdmissionRecord> AdmitAsync<TData>(WorkflowDefinition definition, TData data,
        WorkflowAdmission admission, CancellationToken cancellationToken = default) where TData : class;
    Task<EventAppendResult> AcceptEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default);
    Task<EventAppendResult> AcceptReplyAsync(string token, WorkflowEvent workflowEvent, CancellationToken cancellationToken = default);
    Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId);
    Task<WorkflowOwnership?> GetOwnershipAsync(string instanceId);
}

internal sealed class WorkflowIngressClient(IWorkflowStateRepository store, IWorkflowCatalog registry,
    WorkflowHost host, ExecutionLimits limits, TimeProvider time, ILogger<WorkflowIngressClient>? logger = null,
    RunnableNotifications? notifications = null) : IWorkflowIngressClient
{
    private readonly IWorkflowAdmissionStore _admission = store;

    public async Task<WorkflowAdmissionRecord> AdmitAsync<TData>(WorkflowDefinition definition, TData data,
        WorkflowAdmission admission, CancellationToken cancellationToken = default) where TData : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        admission.Validate();
        if (await _admission.GetWorkflowAdmissionAsync(admission.Sender, admission.Trigger, admission.Key) is { } existing)
        {
            notifications?.Publish(existing.Notice);
            host.WakeNow(existing.InstanceId);
            return existing;
        }
        var registered = await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
        var now = time.GetUtcNow().UtcDateTime;
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, data, new(), now,
            registrationDemands: registered?.RegistrationDemands);
        var prepared = ExecutionCheckpointWriter.Prepare(instance, checkpoint, data, scopes, now,
            verify: limits.VerifyCheckpointParts);
        if (prepared.Bytes > limits.MaxCheckpointBytes)
            throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "The admission checkpoint exceeds the configured limit");
        var commit = CheckpointCommit.CreateAdmission(instance);
        await registry.RegisterWorkflowAsync(definition);
        WorkflowCatalogRules.CheckStartable(await registry.GetWorkflowVersionAsync(definition.Name, definition.Version),
            definition.Name, definition.Version);
        // Keep the exact commit across store-classified retries; no key reservation or separate instance insert.
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Stores classify retryable concurrency errors as Retry; arbitrary exceptions propagate.
            var result = await _admission.AdmitWorkflowInstanceAsync(commit, admission);
            if (result.Status == WorkflowCommitStatus.Retry && attempt < 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger?.LogWarning("Retrying workflow admission for trigger {Trigger}, instance {InstanceId}, attempt {Attempt}: store returned {CommitStatus}",
                    admission.Trigger, commit.InstanceId, attempt + 1, result.Status);
                continue;
            }
            if (result.Status is not (WorkflowCommitStatus.Applied or WorkflowCommitStatus.AlreadyApplied) || result.Admission is not { } record)
                throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Atomic admission was not acknowledged");
            notifications?.Publish(result.Notice);
            host.WakeNow(record.InstanceId);
            return record;
        }
    }

    public async Task<EventAppendResult> AcceptEventAsync(WorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workflowEvent.TargetWaitId != null || workflowEvent.Subject != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown reply token");
        return await AppendAsync(workflowEvent);
    }

    public Task<EventAppendResult> AcceptReplyAsync(string token, WorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReplyToken.TryRead(token, out var instanceId, out var waitId, out var subject) ||
            instanceId != workflowEvent.InstanceId)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown reply token");
        return AppendAsync(workflowEvent with { TargetWaitId = waitId, Subject = subject, Key = null });
    }

    private async Task<EventAppendResult> AppendAsync(WorkflowEvent workflowEvent)
    {
        var receipt = await store.AppendEventAsync(workflowEvent);
        notifications?.Publish(receipt.Notice);
        if (receipt.MadeRunnable) host.WakeNow(workflowEvent.InstanceId);
        return receipt;
    }

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) => store.GetEventAsync(instanceId, eventId);
    public Task<WorkflowOwnership?> GetOwnershipAsync(string instanceId) => _admission.GetWorkflowOwnershipAsync(instanceId);
}
