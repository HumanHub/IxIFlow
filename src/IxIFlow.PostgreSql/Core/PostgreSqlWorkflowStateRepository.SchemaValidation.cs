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
                SELECT instance_id, workflow_name, status, correlation_id, runnable_at_utc, last_host_id, demand_set_id, pinned_host, pin_expires_at_utc, pin_inherit, ended_at_utc, state_json, cancellation_json, cancellation_acknowledged, revision, inbox_sequence, inbox_read_through, incarnation, parent_instance_id FROM ixiflow_workflow_instances WHERE 1=0;
                SELECT instance_id, event_id, sequence, event_type, event_key, target_wait_id, subject, payload_json, payload_hash, received_at_utc, outcome, wait_id, rejection, settled_at_utc, notice_json FROM ixiflow_workflow_events WHERE 1=0;
                SELECT instance_id, part_key, json FROM ixiflow_workflow_parts WHERE 1=0;
                SELECT instance_id, sequence, entry_json FROM ixiflow_workflow_traces WHERE 1=0;
                SELECT instance_id, wait_id, kind, wait_key, event_type, default_payload_json, deadline_utc, opened_sequence, opened_at_utc, close_reason, closed_at_utc FROM ixiflow_workflow_waits WHERE 1=0;
                SELECT instance_id, commit_id, expected_revision, revision, payload_hash, notice_json, made_runnable_json FROM ixiflow_workflow_commits WHERE 1=0;
                SELECT instance_id, token, expires_at_utc, epoch, host_id, crash_count FROM ixiflow_workflow_leases WHERE 1=0;
                SELECT name, types_json, disabled_at_utc, last_error FROM ixiflow_outbox_publishers WHERE 1=0;
                SELECT event_id, incarnation, instance_id, event_type, event_json, occurred_at_utc FROM ixiflow_outbox_events WHERE 1=0;
                SELECT event_id, publisher_name, attempts, first_attempt_at_utc, next_attempt_at_utc, status, last_error, lease_token, lease_until_utc FROM ixiflow_outbox_deliveries WHERE 1=0;
                SELECT instance_id, removed_at_utc, expires_at_utc FROM ixiflow_workflow_tombstones WHERE 1=0;
                SELECT incarnation, sender, trigger_id, request_key, fingerprint, instance_id, workflow_name, workflow_version, expires_at_utc, notice_json FROM ixiflow_workflow_admissions WHERE 1=0;
                SELECT incarnation, instance_id, sender, trigger_id, owner_kind, owner_id, tenant FROM ixiflow_workflow_ownership WHERE 1=0;
                SELECT delivery_identity, payload_hash, sequence, rejection, expires_at_utc, notice_json FROM ixiflow_webhook_deliveries WHERE 1=0;
                SELECT member_id, kind, name, endpoint, slots, engine_version, heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc FROM ixiflow_hosts WHERE 1=0;
                SELECT demand_set_id FROM ixiflow_demand_sets WHERE 1=0;
                SELECT demand_set_id, demand_key, demand_value, expires_at_utc, inherit FROM ixiflow_demand_set_rows WHERE 1=0;
                SELECT member_id, capability_key, capability_value FROM ixiflow_host_capabilities WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
            // Check coverage, ordering and the disjoint pin predicates, including the dedicated
            // null-expiry index. Names alone would retain the old broad pin access path.
            if (await connection.ExecuteScalarAsync<int>("""
                SELECT count(*) FROM pg_index AS definition
                WHERE definition.indrelid = 'ixiflow_workflow_instances'::regclass AND definition.indisvalid
                  AND definition.indcollation[0] = '"C"'::regcollation
                  AND (SELECT array_agg(attribute.attname::text ORDER BY key.ordinality)
                       FROM unnest(definition.indkey) WITH ORDINALITY AS key(attnum, ordinality)
                       JOIN pg_attribute AS attribute ON attribute.attrelid = definition.indrelid AND attribute.attnum = key.attnum)
                    = CASE WHEN definition.indexrelid = to_regclass('ix_ixiflow_instances_set_runnable')
                        THEN ARRAY['demand_set_id', 'runnable_at_utc', 'instance_id', 'last_host_id', 'pinned_host', 'pin_expires_at_utc']
                        WHEN definition.indexrelid = to_regclass('ix_ixiflow_instances_set_pin_no_expiry')
                        THEN ARRAY['demand_set_id', 'runnable_at_utc', 'instance_id', 'pinned_host']
                        ELSE ARRAY['demand_set_id', 'pin_expires_at_utc', 'runnable_at_utc', 'instance_id', 'pinned_host'] END
                  AND ((definition.indexrelid = to_regclass('ix_ixiflow_instances_set_runnable')
                        AND definition.indnkeyatts = 3 AND definition.indcollation[2] = '"C"'::regcollation
                        AND definition.indoption[1] = 0 AND definition.indoption[2] = 0
                        AND pg_get_expr(definition.indpred, definition.indrelid) = '(runnable_at_utc IS NOT NULL)')
                    OR (definition.indexrelid = to_regclass('ix_ixiflow_instances_set_pin')
                        AND definition.indnkeyatts = 4 AND definition.indcollation[3] = '"C"'::regcollation
                        AND definition.indoption[1] = 0 AND definition.indoption[2] = 0 AND definition.indoption[3] = 0
                        AND pg_get_expr(definition.indpred, definition.indrelid) = '((pinned_host IS NOT NULL) AND (pin_expires_at_utc IS NOT NULL) AND (runnable_at_utc IS NOT NULL))')
                    OR (definition.indexrelid = to_regclass('ix_ixiflow_instances_set_pin_no_expiry')
                        AND definition.indnkeyatts = 3 AND definition.indcollation[2] = '"C"'::regcollation
                        AND definition.indoption[1] = 0 AND definition.indoption[2] = 0
                        AND pg_get_expr(definition.indpred, definition.indrelid) = '((pinned_host IS NOT NULL) AND (pin_expires_at_utc IS NULL) AND (runnable_at_utc IS NOT NULL))'))
                """, transaction: transaction) != 3)
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
