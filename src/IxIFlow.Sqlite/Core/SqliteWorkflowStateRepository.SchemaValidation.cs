using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    // Zero-row reads check every required table and column without changing an existing database.
    private static async Task ValidateCurrentSchemaAsync(DbConnection connection, DbTransaction transaction)
    {
        try
        {
            using var schema = await connection.QueryMultipleAsync("""
                SELECT [instance_id], [incarnation], [workflow_name], [status], [correlation_id], [runnable_at_utc], [ended_at_utc], [state_json], [cancellation_json], [cancellation_acknowledged], [revision], [inbox_sequence], [inbox_read_through], [parent_instance_id] FROM ixiflow_workflow_instances WHERE 1=0;
                SELECT [instance_id], [event_id], [sequence], [event_type], [event_key], [target_wait_id], [subject], [payload_json], [payload_hash], [received_at_utc], [outcome], [wait_id], [rejection], [settled_at_utc] FROM ixiflow_workflow_events WHERE 1=0;
                SELECT [instance_id], [part_key], [json] FROM ixiflow_workflow_parts WHERE 1=0;
                SELECT [instance_id], [sequence], [entry_json] FROM ixiflow_workflow_traces WHERE 1=0;
                SELECT [instance_id], [wait_id], [kind], [wait_key], [event_type], [default_payload_json], [deadline_utc], [opened_sequence], [opened_at_utc], [close_reason], [closed_at_utc] FROM ixiflow_workflow_waits WHERE 1=0;
                SELECT [instance_id], [commit_id], [expected_revision], [revision], [payload_hash] FROM ixiflow_workflow_commits WHERE 1=0;
                SELECT [id], [epoch] FROM ixiflow_execution_epoch WHERE 1=0;
                SELECT [instance_id], [token], [expires_at_utc], [epoch], [crash_count] FROM ixiflow_workflow_leases WHERE 1=0;
                SELECT [name], [types_json], [disabled_at_utc], [last_error] FROM ixiflow_outbox_publishers WHERE 1=0;
                SELECT [event_id], [incarnation], [instance_id], [event_type], [event_json], [occurred_at_utc] FROM ixiflow_outbox_events WHERE 1=0;
                SELECT [event_id], [publisher_name], [attempts], [first_attempt_at_utc], [next_attempt_at_utc], [status], [last_error], [lease_token], [lease_until_utc] FROM ixiflow_outbox_deliveries WHERE 1=0;
                SELECT [instance_id], [removed_at_utc], [expires_at_utc] FROM ixiflow_workflow_tombstones WHERE 1=0;
                SELECT [incarnation], [sender], [trigger_id], [request_key], [fingerprint], [instance_id], [workflow_name], [workflow_version], [expires_at_utc] FROM ixiflow_workflow_admissions WHERE 1=0;
                SELECT [incarnation], [instance_id], [sender], [trigger_id], [owner_kind], [owner_id], [tenant] FROM ixiflow_workflow_ownership WHERE 1=0;
                SELECT [delivery_identity], [payload_hash], [sequence], [rejection], [expires_at_utc] FROM ixiflow_webhook_deliveries WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
        }
        catch (Exception error) when (error is SqliteException { SqliteErrorCode: 1 })
        {
            throw EarlierBuild(error);
        }
    }

    private static WorkflowEngineException EarlierBuild(Exception? error = null) =>
        new(WorkflowErrorKind.InvalidState,
            "The workflow database was created by an earlier build and must be recreated. In-place migration is not supported.", error);
}
