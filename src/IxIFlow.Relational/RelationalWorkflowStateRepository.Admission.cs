using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private sealed class WebhookDeliveryRow
    {
        public byte[] PayloadHash { get; set; } = [];
        public long Sequence { get; set; }
        public short? Rejection { get; set; }
    }

    private async Task<EventAppendResult?> ReadWebhookDeliveryAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, byte[] hash)
    {
        var now = await OutboxNowAsync(connection, transaction);
        var row = await connection.QuerySingleOrDefaultAsync<WebhookDeliveryRow>(RequiredSql(Statement.WebhookDeliveryRead),
            WithTimes(new { workflowEvent.DeliveryIdentity }, ("Now", now)), transaction);
        return row == null ? null : row.PayloadHash.SequenceEqual(hash)
            ? new(EventAppendStatus.Duplicate, row.Sequence, Rejection: (EventRejection?)row.Rejection)
            : new(EventAppendStatus.Conflict);
    }

    private async Task WriteWebhookDeliveryAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, byte[] hash, EventAppendResult receipt, DateTime now)
    {
        await connection.ExecuteAsync(RequiredSql(Statement.WebhookDeliveryDelete), new { workflowEvent.DeliveryIdentity }, transaction);
        await connection.ExecuteAsync(RequiredSql(Statement.WebhookDeliveryInsert), WithTimes(new
        {
            workflowEvent.DeliveryIdentity, PayloadHash = hash, receipt.Sequence, Rejection = (short?)receipt.Rejection
        }, ("Expires", now + (workflowEvent.DeliveryRetention ?? TimeSpan.FromHours(24)))), transaction);
    }

    private sealed class AdmissionRow
    {
        public Guid Incarnation { get; set; }
        public string Sender { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Key { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string WorkflowName { get; set; } = "";
        public int WorkflowVersion { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    public async Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission)
    {
        admission.ValidateCommit(commit);
        await EnsureSchemaAsync();
        try { return await ExecuteOperationAsync(Operation.Commit, () => CommitAsync(commit, admission)); }
        catch (Exception error) when (IsRetryableCommit(error)) { return new(WorkflowCommitStatus.Retry, 0); }
    }

    private async Task<WorkflowAdmissionRecord?> ReadAdmissionAsync(DbConnection connection, DbTransaction? transaction,
        string sender, string trigger, string key, DateTime now)
    {
        var row = await connection.QuerySingleOrDefaultAsync<AdmissionRow>(RequiredSql(Statement.AdmissionRead),
            WithTimes(new { Sender = sender, Trigger = trigger, Key = key }, ("Now", now)), transaction);
        return row == null ? null : new(row.Sender, row.Trigger, row.Key, row.Fingerprint, row.InstanceId,
            row.WorkflowName, row.WorkflowVersion, ReadTime(row.ExpiresAtUtc), row.Incarnation);
    }

    public async Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key)
    {
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Read, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            return await ReadAdmissionAsync(connection, null, sender, trigger, key,
                await OutboxNowAsync(connection, null));
        });
    }

    private static string AdmissionLockId(WorkflowAdmission admission) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { admission.Sender, admission.Trigger, admission.Key }))));

    public async Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Read, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<WorkflowOwnership>(RequiredSql(Statement.OwnershipRead),
                WithTimes(new { InstanceId = instanceId }, ("Now", await OutboxNowAsync(connection, null))));
        });
    }
}
