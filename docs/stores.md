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

`IWorkflowStateRepository` includes revisioned checkpoint commits and receipts, execution leases, row-projected instance search, inboxes, waits, committed trace history, outbox subscriptions/deliveries, retention, tombstones, the catalog and artifacts, debugger commands, payload bodies/pins and the member registry. Provider schemas are created on first use in a transaction. Creation supplies complete tables and indexes; it does not upgrade existing tables or repair their missing indexes. Before 1.0, recreate a database from an earlier schema build rather than reuse it. All relational providers leave the schema alone once the instance table exists.

Every commit requires a granted, active execution lease and fences the owner by its positive epoch and expected revision. An epoch is never granted twice in a store, and epochs strictly increase for each instance ID, including across deletions and re-creations. Across instances, epochs follow allocation order, with gaps allowed; grants may complete in a different order. Every commit after the first must also carry the row's incarnation. Instance changes, consumed events, wait transitions, child starts and terminal completion envelopes share the persistence transaction. Publishers use their own per-delivery leases. Cleanup atomically removes operational data and writes a tombstone while preserving the epoch sequence.

Persisted execution state is a validated schema-4 checkpoint JSON object. Whole checkpoints are split into a trunk and state parts, then assembled on full reads. Escaped strings, opaque state and unsupported checkpoint versions are rejected. Serialization reports an error when the checkpoint exceeds the JSON depth limit.

`IWorkflowCatalog` persists drafts and version metadata in the selected store. Code-defined executable definitions remain local to each host, so load the matching definitions and activity code on every host that could resume work. The default template store is in memory. [YAML publishing](yaml-authoring.md#publishing-and-boundaries) creates immutable, verified bundles in `IArtifactStore`, addressed by SHA-256 digest. Memory, SQLite, SQL Server, PostgreSQL and file-system artifact stores are implemented. Hosts configured with `UseWorkflowBundles` fetch published bundles by digest, verify them and load each in its own collectible context.

`IWorkflowCatalog` supports registration, exact name/version lookup, local executable-definition snapshots and durable version lifecycle. Draft, Published, Deprecated and Retired are distinct states; transitions move forward. Unversioned starts select the highest Published version, while explicitly pinned starts may use a Deprecated version. Retired versions refuse new starts while existing instances can recover. Retirement is guarded against published/deprecated callers and unfinished parents that still need the version. Child invocations require a loaded definition and pin its version. Re-registering a name/version rejects a different `StructureHash`; a description, tag or display-name edit is accepted, retaining the first registration’s metadata. The checkpoint stores the hash and execution refuses a mismatch with both hashes in the error. Schema 4 cannot read earlier saved instances; there is no migration. Reset development state and restart hosts together.

Artifact retention is implemented alongside instance cleanup. `IxIFlowOptions.ArtifactRetention` defaults to 90 days; bounded collection removes only artifacts old enough and unreferenced by the catalog, with reference checks and collection serialized. Re-uploading the same digest refreshes its age. This does not delete a catalog-referenced bundle simply because it is old. See [retention](retention-and-tombstones.md).

## Memory

The singleton `InMemoryWorkflowStateRepository` survives DI request scopes in one process. It uses the registered `TimeProvider` and implements the same inbox, lease, outbox, retention and member-registry semantics. Restarting the process loses those records. This is useful for examples and deterministic tests.

## SQLite

Use a file connection string, for example `Data Source=workflows.db`. `UseSqlite` rejects `Mode=Memory`, `:memory:`, an empty data source and `file:` URIs with `ArgumentException` during registration. The provider uses WAL and takes an exclusive OS handle on the normalized database path plus `.ixiflow-owner`. Ownership is enforced even when the repository is constructed directly.

A second owner cannot open that database concurrently. Asynchronously dispose the service provider/repository before reopening it; a process crash releases the OS handle. Use a local file system with working file locking. UNC and network paths are rejected, and startup probes that the ownership lock is enforced. SQLite does not support coordinator or multiple-host execution.

The registered `TimeProvider` supplies store time. Run it with the [core host](coordinator-and-hosts.md) for recovery scans, outbox publication and retention.

## SQL Server

The provider persists state, inboxes, waits, traces, leases, outbox and tombstones in SQL Server. Multiple compatible hosts can share the database; only a fenced lease owner advances an instance.

Instance, event, wait and commit IDs, parent IDs, lease tokens, checkpoint-part keys and outbox publisher identities use `Latin1_General_100_BIN2` comparisons, preserving case distinctions independently of the database default collation. SQL Server equality still ignores trailing spaces, including under this binary collation. Workflow names, status, stop kind and correlation IDs also use binary comparisons; event/wait keys and other descriptive columns retain the database collation.

Database time governs event arrival and deadlines. A separate ownership/cancellation connection pool defaults to 16 connections so ordinary workflow traffic does not consume all lease-renewal capacity. For cancellation reads to avoid waiting behind unrelated commits, enable `READ_COMMITTED_SNAPSHOT` in the database.

The waiting instance ID lock is taken by first commits (`ExpectedRevision == 0`, including root creation), each child start for the child's ID, explicit deletion, each instance purged by retention, cancellation requests, lease releases and debugger writes. It is an exclusive transaction-owned `sp_getapplock` on `IxIFlow.Instance/<instance ID with trailing spaces removed>`. Removing trailing spaces makes its identity match key equality; a resource longer than 255 UTF-16 code units is rejected before SQL Server can truncate it. Ordinary existing-row commits, including receipt replays, rely on their row lock and take no lock on their own ID. Claims refuse that row lock; deleters hold the ID and wait for the row. A commit with child starts locks each child's ID after locking its own row.

A claim acquires its ID lock with `@LockTimeout = 0` and reads the instance key with `UPDLOCK, ROWLOCK, NOWAIT` in the same command batch under READ COMMITTED. Contention on either returns no grant. A held row or an uncommitted insert or delete of that key refuses immediately; absence remains protected by the ID lock through eligibility, lease arbitration and host stamping. Different absent IDs have independent locks, including in the same index gap. After obtaining a lease, the claim's instance host stamp also uses `NOWAIT`: a shared reader compatible with the initial update lock must not delay its conversion to an exclusive lock. Any instance lock timeout (1222) rolls back the entire claim, including lease and host changes, with either `XACT_ABORT` setting. Lease statements retain their waiting locks.

Blocking ID-lock deadlocks are reported as 51001 and receive the same commit retry handling as row-lock deadlocks (1205). Outside commits, neither has special retry handling; both surface to the caller as row-lock deadlocks did before ID locking.
Candidate discovery also stays READ COMMITTED. Its instance reads use `READCOMMITTEDLOCK, READPAST`, including with `READ_COMMITTED_SNAPSHOT` enabled. Lease lookups retain ordinary reads: a locked live lease must never look absent. Claims can still wait for lease locks. Commits, deletes, termination requests and retention retain their waiting locks.

Across all providers, recovery continues an ordered scan across bounded passes, ordered by host affinity, due time and ID. The host retains a scan offset, advances it for each fetched page and wraps at the end; changes to the runnable set are picked up on subsequent scans. A stable prefix of any number of persistently refused candidates therefore cannot cycle ahead of free work forever. A wake probes a fresh head page of up to eight entries without resetting the continuing scan, leaving budget for progress even under repeated wakes. Recent refusals sort after other entries within each page for one following pass; they are never excluded, so a wake can retry immediately.

A no-grant result refills the same slot while sibling runs continue. Workers share refill pages of up to eight; a pass examines at most 256 entries and makes at most 256 reservations, trying each ID at most once. Prefetched entries have no reservations: a worker checks live reservations and rejection deferrals when it takes an entry. A slow sibling cannot exclude unused entries from later passes. Reservations belong to their pass and are removed only by their owner. A discovery failure stops and logs that worker's refilling while sibling runs continue. Skipping changes no stored scheduling state. Polling remains a fallback after missed wakes (up to 10 seconds with a coordinator); recent claim refusal adds no exclusion pass. Successful claims still recheck eligibility and read store time after locking.
## PostgreSQL

PostgreSQL has the same durable responsibilities and fenced ownership. Database time governs event arrival and deadlines. A separate ownership pool also protects lease/cancellation work.

Execution claims stay READ COMMITTED and combine the zero-timeout `pg_try_advisory_xact_lock(hashtextextended(instance ID COLLATE "C", 0))` and `FOR UPDATE SKIP LOCKED` row read in one statement. Contention on either returns no grant immediately. A materialized identity probe gates the row read; a same-statement existence check distinguishes a skipped visible row from genuine absence. PostgreSQL instance keys are `text COLLATE "C"`: equality compares the complete text, including trailing spaces, and the ID hash uses the same collation and untrimmed text. Equal keys therefore share a lock identity; hash collisions can only cause extra contention between distinct IDs.

The exact waiting-ID-lock operations are the same as SQL Server's list above, using blocking `pg_advisory_xact_lock` on the same hash, including protection of uncommitted insertions that a row read cannot yet see. Ordinary existing-row commits need only their row lock on PostgreSQL too: an absent row cannot pass their positive expected revision check, and a present locked row excludes claims and deletes. Child creation waits for its child's ID; it does not return Retry for a busy ID. Lease locks may still wait. Eligibility, fencing and store time after locking follow the common contract. Recovery continues and wraps as described above.
On both server providers the instance lock order is parent row (when a child operation needs its parent), instance ID (where required), instance row, then lease. Parent commits lock their own row before taking child IDs in ordinal order and then each child's row; deletion uses the same parent-before-child order. Claims never acquire a parent row. Cancellation requests, lease releases and debugger writes take the waiting ID lock before their instance row. Termination requests wait for rows, traversing parent before child, and never acquire an ID lock after a row. Payload digest pins precede instance/parent locks; claims acquire no payload pins. SQLite and memory retain their existing serialization.

The provider implements `IRunnableWakeSource` using `LISTEN`/`NOTIFY`. Notifications prompt recovery scans when instances become runnable; they are wake-up hints, with normal polling providing the fallback after missed notifications or reconnection.

## Store extensions

Set `IxIFlowOptions.UseCustomStateRepository` to the repository type in `AddIxIFlow(options => ...)`; it is registered as a singleton and counts as the chosen store. It must support concurrent calls and the complete public store contract, including inbox, outbox, retention and the [member registry](coordinator-and-hosts.md#host-registry-and-capabilities). It implements combined batched control reads (cancellation and debugger state), instance summaries and row search directly. Batch control reads preserve successful results when other reads fail and throw `AggregateException` when every read fails. `IxIFlow.Relational` is an internal implementation dependency, not a public provider SDK.

Read [execution model](execution-model.md), [retention](retention-and-tombstones.md) and [outbox delivery](outbox-and-publishers.md) before implementing a store.
