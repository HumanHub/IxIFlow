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
                SELECT package_id, package_version, "key", version, metadata_type_name, csharp_type_name, display_name, category, icon, search_text, contract_hash, descriptor_json, catalog_revision FROM ixiflow_package_activities WHERE 1=0;
                SELECT package_id, package_version, "key", version, metadata_type_name, csharp_type_name, display_name, category, icon, search_text, contract_hash, descriptor_json, catalog_revision, fingerprint FROM ixiflow_installed_activities WHERE 1=0;
                SELECT package_id, package_version, missing_package_id FROM ixiflow_package_pending_dependencies WHERE 1=0;
                SELECT id, kind, name, version, generation, revision, attempt, state, host_id, started_by, started_at_utc, heartbeat_at_utc, finished_at_utc, outcome_json, catalog_revision FROM ixiflow_publication_attempts WHERE 1=0;
                SELECT kind, name, version, generation, revision, active_id, last_attempt FROM ixiflow_publication_heads WHERE 1=0;
                SELECT generation, kind, name, version FROM ixiflow_draft_generations WHERE 1=0;
                SELECT id, version, fingerprint, catalog_revision FROM ixiflow_installed_packages WHERE 1=0;
                SELECT member_id, package_id, package_version, fingerprint FROM ixiflow_host_packages WHERE 1=0;
                SELECT instance_id, command_id, command_order, command_json FROM ixiflow_workflow_debug_commands WHERE 1=0;
                SELECT digest, bytes, body, metadata_json FROM ixiflow_workflow_payloads WHERE 1=0;
                SELECT digest, owner_kind, owner_id FROM ixiflow_workflow_payload_refs WHERE 1=0;
                SELECT name, version, kind, source, revision, generation, source_length, updated_at_utc, updated_by FROM ixiflow_definitions WHERE 1=0;
                SELECT id, revision FROM ixiflow_catalog_revision WHERE 1=0;
                SELECT name, version, structure_hash, state, artifact_digest, metadata_json, catalog_revision, published_draft_generation, published_draft_revision, published_at_utc, published_by, state_changed_at_utc, state_changed_by, lifecycle_revision FROM ixiflow_workflow_versions WHERE 1=0;
                SELECT digest, kind, content, created_at_utc FROM ixiflow_artifacts WHERE 1=0;
                SELECT id, version, digest, metadata_json, dependency_groups_json, origin, extraction, extraction_detail, closure_json, withdrawn, withdrawn_at_utc, withdrawn_by, registered_at_utc, registered_by, published_draft_generation, published_draft_revision, published_draft_name, published_draft_version, published_at_utc, published_by, lifecycle_revision, catalog_revision FROM ixiflow_packages WHERE 1=0;
                SELECT instance_id, workflow_name, workflow_version, bundle_digest, created_at_utc, stop_kind, stop_crash_count, status, correlation_id, runnable_at_utc, ended_at_utc, state_json, cancellation_json, cancellation_acknowledged, revision, inbox_sequence, inbox_read_through, incarnation, parent_instance_id, parent_wait_id, last_host_id, demand_set_id, pinned_host, pin_expires_at_utc, pin_inherit FROM ixiflow_workflow_instances WHERE 1=0;
                SELECT instance_id, event_id, sequence, event_type, event_key, target_wait_id, subject, stored_payload_json, payload_hash, received_at_utc, outcome, wait_id, rejection, settled_at_utc, notice_json FROM ixiflow_workflow_events WHERE 1=0;
                SELECT instance_id, part_key, json FROM ixiflow_workflow_parts WHERE 1=0;
                SELECT instance_id, sequence, entry_json, revision, commit_end_sequence, commit_status FROM ixiflow_workflow_traces WHERE 1=0;
                SELECT instance_id, diagnostic_id, diagnostic_json FROM ixiflow_workflow_diagnostics WHERE 1=0;
                SELECT instance_id, wait_id, wait_ref, kind, wait_key, event_type, default_payload_json, deadline_utc, opened_sequence, opened_at_utc, close_reason, closed_at_utc FROM ixiflow_workflow_waits WHERE 1=0;
                SELECT instance_id, commit_id, expected_revision, revision, payload_hash, notice_json, made_runnable_json FROM ixiflow_workflow_commits WHERE 1=0;
                SELECT instance_id, token, expires_at_utc, epoch, crash_count, host_id FROM ixiflow_workflow_leases WHERE 1=0;
                SELECT name, types_json, disabled_at_utc, enabled_at_utc, enabled_by, unregistered_at_utc, unregistered_by, last_error FROM ixiflow_outbox_publishers WHERE 1=0;
                SELECT event_id, incarnation, instance_id, event_type, event_json, occurred_at_utc FROM ixiflow_outbox_events WHERE 1=0;
                SELECT event_id, publisher_name, attempts, first_attempt_at_utc, given_up_reason, retried_at_utc, retried_by, next_attempt_at_utc, status, last_error, lease_token, lease_until_utc FROM ixiflow_outbox_deliveries WHERE 1=0;
                SELECT instance_id, removed_at_utc, expires_at_utc FROM ixiflow_workflow_tombstones WHERE 1=0;
                SELECT incarnation, sender, trigger_id, request_key, fingerprint, instance_id, workflow_name, workflow_version, expires_at_utc, notice_json FROM ixiflow_workflow_admissions WHERE 1=0;
                SELECT incarnation, instance_id, sender, trigger_id, owner_kind, owner_id, tenant FROM ixiflow_workflow_ownership WHERE 1=0;
                SELECT delivery_identity, payload_hash, sequence, rejection, expires_at_utc, notice_json FROM ixiflow_webhook_deliveries WHERE 1=0;
                SELECT member_id, kind, name, endpoint, slots, engine_version, heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc FROM ixiflow_hosts WHERE 1=0;
                SELECT demand_set_id FROM ixiflow_demand_sets WHERE 1=0;
                SELECT demand_set_id, demand_key, demand_value, expires_at_utc, value_expires_at_utc, inherit FROM ixiflow_demand_set_rows WHERE 1=0;
                SELECT member_id, capability_key, capability_value FROM ixiflow_host_capabilities WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
            await ValidateDraftKeysAsync(connection, transaction);
            await ValidatePackageKeysAsync(connection, transaction);
            if (await connection.ExecuteScalarAsync<int>("SELECT CASE WHEN to_regclass('ixiflow_payload_refs_owner') IS NOT NULL THEN 1 ELSE 0 END", transaction: transaction) != 1)
                throw EarlierBuild();
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
            // Opening an existing database must retain catalog, operations and outbox access paths.
            var indexes = (await connection.QueryAsync<string>("SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()",
                transaction: transaction)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] requiredIndexes =
            [
                "ix_ixiflow_instances_demand_set",
                "ix_ixiflow_instances_set_affinity",
                "ix_ixiflow_instances_set_pin",
                "ix_ixiflow_instances_set_pin_no_expiry",
                "ix_ixiflow_instances_set_runnable",

                "ix_publication_attempt_identity",
                "ix_publication_history",
                "ix_publication_finished",
                "ix_artifacts_created",
                "ix_workflow_versions_revision",
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
                "ix_ixiflow_instances_bundle_digest",
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
        catch (Exception error) when (error is PostgresException { SqlState: "42703" or "42P01" or "42704" })
        {
            throw EarlierBuild(error);
        }
    }

    private static async Task ValidateDraftKeysAsync(DbConnection connection, DbTransaction transaction)
    {
        foreach (var (table, name, primary, unique, columns) in new (string, string?, bool, bool, string[])[]
        {
            ("ixiflow_definitions", null, true, true, ["kind", "name", "version"]),
            ("ixiflow_workflow_versions", null, true, true, ["name", "version"]),
            ("ixiflow_draft_generations", null, true, true, ["generation"]),
            ("ixiflow_publication_heads", null, true, true, ["kind", "name", "version", "generation", "revision"]),
            ("ixiflow_publication_attempts", null, true, true, ["id"]),
            ("ixiflow_publication_attempts", "ix_publication_attempt_identity", false, true, ["kind", "name", "version", "generation", "revision", "attempt"]),
            ("ixiflow_publication_attempts", "ix_publication_finished", false, false, ["finished_at_utc"]),
            ("ixiflow_publication_attempts", "ix_publication_history", false, false, ["kind", "name", "version", "started_at_utc", "id"])
        })
        {
            if (!(await connection.QueryAsync<string>("""
                SELECT CASE WHEN i.indoption[k.ord::int-1]=0
                    AND (a.attcollation=0 OR i.indcollation[k.ord::int-1]='"C"'::regcollation)
                    THEN a.attname::text ELSE '' END
                FROM pg_index i
                JOIN pg_class index_table ON index_table.oid=i.indexrelid
                JOIN pg_am method ON method.oid=index_table.relam AND method.amname='btree'
                CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ord)
                LEFT JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                WHERE i.indrelid=to_regclass(@Table) AND i.indisprimary=@Primary AND i.indisunique=@Unique
                  AND (@Name IS NULL OR i.indexrelid=to_regclass(@Name))
                  AND i.indpred IS NULL AND i.indexprs IS NULL AND i.indisvalid AND i.indisready AND i.indislive
                  AND k.ord<=i.indnkeyatts
                ORDER BY k.ord
                """, new { Table = table, Name = name, Primary = primary, Unique = unique }, transaction)).SequenceEqual(columns, StringComparer.Ordinal))
                throw EarlierBuild();
        }
    }

    private static async Task ValidatePackageKeysAsync(DbConnection connection, DbTransaction transaction)
    {
        foreach (var (table, name, primary, columns) in new (string, string?, bool, string[])[]
        {
            ("ixiflow_packages", null, true, ["id", "version"]),
            ("ixiflow_installed_packages", null, true, ["id", "version", "fingerprint"]),
            ("ixiflow_package_activities", null, true, ["package_id", "package_version", "key"]),
            ("ixiflow_installed_activities", null, true, ["package_id", "package_version", "fingerprint", "key"]),
            ("ixiflow_package_pending_dependencies", null, true, ["package_id", "package_version", "missing_package_id"]),
            ("ixiflow_packages", "ix_packages_origin", false, ["origin", "id", "version"]),
            ("ixiflow_package_activities", "ix_package_activities_key", false, ["key", "version", "package_id", "package_version"]),
            ("ixiflow_package_activities", "ix_package_activities_category", false, ["category", "key"]),
            ("ixiflow_installed_activities", "ix_installed_activities_key", false, ["key", "version", "package_id", "package_version"]),
            ("ixiflow_installed_activities", "ix_installed_activities_category", false, ["category", "key"]),
            ("ixiflow_package_pending_dependencies", "ix_package_pending_reverse", false, ["missing_package_id", "package_id", "package_version"]),
        })
            if (!(await connection.QueryAsync<string>("""
                SELECT CASE WHEN i.indoption[k.ord::int-1]=0 AND (a.attcollation=0 OR i.indcollation[k.ord::int-1]='"C"'::regcollation) THEN a.attname::text ELSE '' END
                FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ord)
                LEFT JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                WHERE i.indrelid=to_regclass(@Table) AND i.indisprimary=@Primary AND i.indisunique=@Primary
                  AND (@Name IS NULL OR i.indexrelid=to_regclass(@Name)) AND i.indpred IS NULL AND i.indexprs IS NULL
                  AND i.indisvalid AND i.indisready AND i.indislive AND k.ord<=i.indnkeyatts ORDER BY k.ord
                """, new { Table = table, Name = name, Primary = primary }, transaction)).SequenceEqual(columns, StringComparer.Ordinal))
                throw EarlierBuild();
    }

    private static WorkflowEngineException EarlierBuild(Exception? error = null) =>
        new(WorkflowErrorKind.InvalidState,
            "The workflow database was created by an earlier build and must be recreated. In-place migration is not supported.", error);
}
