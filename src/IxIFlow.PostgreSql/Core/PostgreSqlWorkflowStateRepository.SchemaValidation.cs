using System.Data.Common;
using Dapper;
using Npgsql;

namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    // Zero-row reads check every required table and column without changing an existing database.
    private static async Task ValidateCurrentSchemaAsync(DbConnection connection, DbTransaction transaction)
    {
        try
        {
            using var schema = await connection.QueryMultipleAsync("""
                SELECT instance_id, command_id, command_order, command_json FROM ixiflow_workflow_debug_commands WHERE 1=0;
                SELECT digest, bytes, body, metadata_json FROM ixiflow_workflow_payloads WHERE 1=0;
                SELECT digest, owner_kind, owner_id FROM ixiflow_workflow_payload_refs WHERE 1=0;
                SELECT name, version, kind, source, revision, state FROM ixiflow_definitions WHERE 1=0;
                SELECT name, version, structure_hash, state, artifact_digest, metadata_json FROM ixiflow_workflow_versions WHERE 1=0;
                SELECT digest, kind, content, created_at_utc FROM ixiflow_artifacts WHERE 1=0;
                SELECT instance_id, workflow_name, workflow_version, created_at_utc, stop_kind, stop_crash_count, status, correlation_id, runnable_at_utc, ended_at_utc, state_json, cancellation_json, cancellation_acknowledged, revision, inbox_sequence, inbox_read_through, incarnation, parent_instance_id, parent_wait_id FROM ixiflow_workflow_instances WHERE 1=0;
                SELECT instance_id, event_id, sequence, event_type, event_key, target_wait_id, subject, stored_payload_json, payload_hash, received_at_utc, outcome, wait_id, rejection, settled_at_utc FROM ixiflow_workflow_events WHERE 1=0;
                SELECT instance_id, part_key, json FROM ixiflow_workflow_parts WHERE 1=0;
                SELECT instance_id, sequence, entry_json, revision, commit_end_sequence, commit_status FROM ixiflow_workflow_traces WHERE 1=0;
                SELECT instance_id, wait_id, wait_ref, kind, wait_key, event_type, default_payload_json, deadline_utc, opened_sequence, opened_at_utc, close_reason, closed_at_utc FROM ixiflow_workflow_waits WHERE 1=0;
                SELECT instance_id, commit_id, expected_revision, revision, payload_hash FROM ixiflow_workflow_commits WHERE 1=0;
                SELECT instance_id, token, expires_at_utc, epoch, crash_count FROM ixiflow_workflow_leases WHERE 1=0;
                SELECT name, types_json, disabled_at_utc, enabled_at_utc, enabled_by, unregistered_at_utc, unregistered_by, last_error FROM ixiflow_outbox_publishers WHERE 1=0;
                SELECT event_id, incarnation, instance_id, event_type, event_json, occurred_at_utc FROM ixiflow_outbox_events WHERE 1=0;
                SELECT event_id, publisher_name, attempts, first_attempt_at_utc, given_up_reason, retried_at_utc, retried_by, next_attempt_at_utc, status, last_error, lease_token, lease_until_utc FROM ixiflow_outbox_deliveries WHERE 1=0;
                SELECT instance_id, removed_at_utc, expires_at_utc FROM ixiflow_workflow_tombstones WHERE 1=0;
                SELECT incarnation, sender, trigger_id, request_key, fingerprint, instance_id, workflow_name, workflow_version, expires_at_utc FROM ixiflow_workflow_admissions WHERE 1=0;
                SELECT incarnation, instance_id, sender, trigger_id, owner_kind, owner_id, tenant FROM ixiflow_workflow_ownership WHERE 1=0;
                SELECT delivery_identity, payload_hash, sequence, rejection, expires_at_utc FROM ixiflow_webhook_deliveries WHERE 1=0;
                SELECT member_id, kind, name, endpoint, slots, engine_version, heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc FROM ixiflow_hosts WHERE 1=0;
                SELECT member_id, capability_key, capability_value FROM ixiflow_host_capabilities WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
            if (await connection.ExecuteScalarAsync<int>("SELECT CASE WHEN to_regclass('ixiflow_payload_refs_owner') IS NOT NULL THEN 1 ELSE 0 END", transaction: transaction) != 1)
                throw EarlierBuild();
            // Opening an existing database must retain catalog, operations and outbox access paths.
            var indexes = (await connection.QueryAsync<string>("SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()",
                transaction: transaction)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] requiredIndexes =
            [
                "ix_artifacts_created",
                "ix_ixiflow_admissions_expiry",
                "ix_ixiflow_hosts_heartbeat",
                "ix_ixiflow_instances_correlation_creation",
                "ix_ixiflow_instances_creation",
                "ix_ixiflow_instances_ended",
                "ix_ixiflow_instances_ended_creation",
                "ix_ixiflow_instances_parent_creation",
                "ix_ixiflow_instances_status_creation",
                "ix_ixiflow_instances_stop_creation",
                "ix_ixiflow_instances_version_creation",
                "ix_ixiflow_instances_workflow_creation",
                "ix_ixiflow_instances_workflow_status_creation",
                "ix_ixiflow_instances_workflow_version_creation",
                "ix_ixiflow_outbox_deliveries_listing",
                "ix_ixiflow_outbox_deliveries_pending",
                "ix_ixiflow_outbox_deliveries_publisher_listing",
                "ix_ixiflow_outbox_deliveries_status_listing",
                "ix_ixiflow_outbox_events_instance",
                "ix_ixiflow_outbox_events_listing",
                "ix_ixiflow_tombstones_expiry",
                "ix_ixiflow_webhook_deliveries_expiry",
                "ix_ixiflow_workflow_events_pending",
                "ix_ixiflow_workflow_instances_correlation_id",
                "ix_ixiflow_workflow_instances_parent",
                "ix_ixiflow_workflow_instances_runnable",
                "ix_ixiflow_workflow_instances_status",
                "ix_ixiflow_workflow_instances_unfinished_workflow",
                "ix_ixiflow_workflow_instances_workflow_name",
                "ix_ixiflow_workflow_leases_expires_at",
                "ix_ixiflow_workflow_waits_open",
                "ixiflow_workflow_waits_page",
            ];
            if (requiredIndexes.Any(name => !indexes.Contains(name)))
                throw EarlierBuild();
            if (await connection.ExecuteScalarAsync<int>("SELECT CASE WHEN to_regclass('ixiflow_execution_epoch') IS NOT NULL THEN 1 ELSE 0 END", transaction: transaction) != 1)
                throw EarlierBuild();
        }
        catch (Exception error) when (error is PostgresException { SqlState: "42703" or "42P01" })
        {
            throw EarlierBuild(error);
        }
    }

    private static WorkflowEngineException EarlierBuild(Exception? error = null) =>
        new(WorkflowErrorKind.InvalidState,
            "The workflow database was created by an earlier build and must be recreated. In-place migration is not supported.", error);
}
