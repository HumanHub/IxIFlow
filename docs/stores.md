---
title: Stores
description: Memory, SQLite, SQL Server and PostgreSQL persistence.
---

Choose one store on the builder returned by `services.AddIxIFlow()`. Provider extensions use `IxIFlow.Extensions`; install the matching package. A second provider selection is rejected.

| Store | Registration | Deployment |
| --- | --- | --- |
| Memory | `services.AddIxIFlow()` | Default, process lifetime |
| SQLite | `services.AddIxIFlow().UseSqlite(connectionString)` | Durable file, one owning host |
| SQL Server | `services.AddIxIFlow().UseSqlServer(connectionString)` | Shared durable database |
| PostgreSQL | `services.AddIxIFlow().UsePostgreSql(connectionString)` | Shared durable database plus notification wake-ups |

All four registrations compile in [Hosting.Services](../samples/IxIFlow.Samples/Hosting.cs). [SampleTests.cs](../tests/IxIFlow.Samples.Tests/SampleTests.cs) runs memory/SQLite and constructs the other providers. Database execution has additional provider and conformance tests under [tests](../tests).

## Common contract

`IWorkflowStateRepository` includes revisioned checkpoint commits and receipts, execution leases, row-projected instance search, inboxes, waits, committed trace history, outbox subscriptions/deliveries, retention, tombstones and the member registry. Provider schemas are created on first use in a transaction. Creation supplies complete tables and indexes; it does not upgrade existing tables or repair their missing indexes. Before 1.0, recreate a database from an earlier schema build rather than reuse it. All relational providers leave the schema alone once the instance table exists.

Every commit requires a granted, active execution lease and fences the owner by its positive epoch and expected revision. An epoch is never granted twice in a store, and epochs strictly increase for each instance ID, including across deletions and re-creations. Across instances, epochs follow allocation order, with gaps allowed; grants may complete in a different order. Every commit after the first must also carry the row's incarnation. Instance changes, consumed events, wait transitions, child starts and terminal completion envelopes share the persistence transaction. Publishers use their own per-delivery leases. Cleanup atomically removes operational data and writes a tombstone while preserving the epoch sequence.

Persisted execution state is a validated schema-3 checkpoint JSON object. Whole checkpoints are split into a trunk and state parts, then assembled on full reads. Escaped strings, opaque state and unsupported checkpoint versions are rejected. Serialization reports an error when the checkpoint exceeds the JSON depth limit.

Definition/version registries are in memory for every provider. Load the matching definitions and activity code on every host that could resume work. The default template store is also in memory; workflow bundles and artifact storage remain planned.

`IWorkflowVersionRegistry` supports registration, exact name/version lookup, snapshots of registered definitions and deactivation. Child invocations require a registered definition; named invocations pin its version. Selecting the latest published version belongs to the planned publishing lifecycle. Re-registering a name/version rejects changed data types, descriptions, tags or runtime fingerprints. Deactivation blocks new starts while existing instances can recover.

## Memory

The singleton `InMemoryWorkflowStateRepository` survives DI request scopes in one process. It uses the registered `TimeProvider` and implements the same inbox, lease, outbox, retention and member-registry semantics. Restarting the process loses those records. This is useful for examples and deterministic tests.

## SQLite

Use a file connection string, for example `Data Source=workflows.db`. `UseSqlite` rejects `Mode=Memory`, `:memory:`, an empty data source and `file:` URIs with `ArgumentException` during registration. The provider uses WAL and takes an exclusive OS handle on the normalized database path plus `.ixiflow-owner`. Ownership is enforced even when the repository is constructed directly.

A second owner cannot open that database concurrently. Asynchronously dispose the service provider/repository before reopening it; a process crash releases the OS handle. Use a local file system with working file locking. UNC and network paths are rejected, and startup probes that the ownership lock is enforced. SQLite does not support coordinator or multiple-host execution.

The registered `TimeProvider` supplies store time. Run it with the [core host](coordinator-and-hosts.md) for recovery scans, outbox publication and retention.

## SQL Server

The provider persists state, inboxes, waits, traces, leases, outbox and tombstones in SQL Server. Multiple compatible hosts can share the database; only a fenced lease owner advances an instance.

Instance, event, wait and commit IDs, parent IDs, lease tokens, checkpoint-part keys and outbox publisher identities use `Latin1_General_100_BIN2` comparisons, preserving case distinctions independently of the database default collation. Workflow names, status, stop kind and correlation IDs also use binary comparisons; event/wait keys and other descriptive columns retain the database collation.

Database time governs event arrival and deadlines. A separate ownership/cancellation connection pool defaults to 16 connections so ordinary workflow traffic does not consume all lease-renewal capacity. For cancellation reads to avoid waiting behind unrelated commits, enable `READ_COMMITTED_SNAPSHOT` in the database.

## PostgreSQL

PostgreSQL has the same durable responsibilities and fenced ownership. Database time governs event arrival and deadlines. A separate ownership pool also protects lease/cancellation work.

The provider implements `IRunnableWakeSource` using `LISTEN`/`NOTIFY`. Notifications prompt recovery scans when instances become runnable; they are wake-up hints, with normal polling providing the fallback after missed notifications or reconnection.

## Store extensions

Set `IxIFlowOptions.UseCustomStateRepository` to the repository type in `AddIxIFlow(options => ...)`; it is registered as a singleton and counts as the chosen store. It must support concurrent calls and the complete public store contract, including inbox, outbox, retention and the [member registry](coordinator-and-hosts.md#host-registry-and-capabilities). It implements batched cancellation reads, instance summaries and combined row search directly. Batch cancellation reads preserve successful results when other reads fail and throw `AggregateException` when every read fails. `IxIFlow.Relational` is an internal implementation dependency, not a public provider SDK.

Read [execution model](execution-model.md), [retention](retention-and-tombstones.md) and [outbox delivery](outbox-and-publishers.md) before implementing a store.
