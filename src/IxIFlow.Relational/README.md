# IxIFlow.Relational

Internal design note for maintainers of the three first-party relational stores. The packages ship and version together. IxIFlow.Relational references IxIFlow and Dapper; database drivers stay in the store packages. The protocol and its helpers belong to the IxIFlow.Relational namespace. Only IxIFlow.SqlServer, IxIFlow.PostgreSql and IxIFlow.Sqlite receive friend access. The only public type is the abstract RelationalWorkflowStateRepository, whose public members implement the store interfaces. Its constructor, hooks, policies, statement enums, helpers and SQL fragments are internal; only the three friend store assemblies can derive from it. Each public sealed store repository inherits this protocol directly and supplies its SQL and internal overrides.

## Shared protocol

RelationalWorkflowStateRepository owns revision and receipt checks, ownership fencing, inbox arrivals and gone-child sequences, waits, child starts and cancellations, parent notices, parts, trace dispatch, runnable calculation, outbox claims, recovery scans, deletion and instance assembly.

The commit lock order is parent, then identity, then instance, then lease. A child completing into its parent's inbox must take locks in the same order as a parent cancelling its children. PostgreSQL's identity lock also protects an ID with no instance row. SQL Server takes range locks where creation must be serialized, but claims, inbox locks and keyed deletion use row locks to avoid blocking unrelated instances.

CreateSchemaAsync creates or upgrades tables on an open connection, including cross-host schema locking. One double-checked asynchronous schema guard lives in the base. Schema creation uses the provider's operation admission policy; SQLite queues it through its writer gate. Initialization errors stay outside commit retry mapping.

OpenConnectionAsync chooses the main or reserved ownership connection. The base builds the reserved pool's application name, pool bounds and admission semaphore. Claims, cancellation requests and single cancellation reads share at most half that pool so renewals and cancellation watches still obtain connections. SQLite instead queues writes, including inbox-page reads, through its asynchronous gate. BeginTransactionAsync selects each operation's transaction. The core commits successful writes and disposes every transaction; snapshot reads are disposed without committing.

## Clocks and parameters

Server-clock providers return time in a separate statement after the required row lock, in the same round trip. A statement that waits for a lock must not carry its start time into a deadline decision (D-35). ParentInbox only locks and reads; the child's CommitState supplies the commit time. SQL Server's lease activity, settlement and finished-event statements retain their own server clock expressions.

SQLite samples its injected TimeProvider after BEGIN IMMEDIATE for commits, after the inbox read for append and page reads, after beginning lease and outbox write transactions, and after deleting old parts but before serializing a save. Recovery scans sample immediately before their query.

At, TimeDbType, SmallInt and SmallIntDbType preserve driver wire types. Every SQL Server timestamp parameter, including a save's RunnableAtUtc, is datetime2. PostgreSQL uses DateTimeOffset; SQLite uses fixed-width UTC text and small integers. ReadTime normalizes returned timestamps, while runnable columns preserve SQL Server's existing driver DateTimeKind behavior.

Save parameters include Now only for a local-clock store. LeaseParameters supplies captured Now only when the lease SQL references it. Claims send server duration milliseconds or SQLite's local Now/Until pair. AdvanceInbox receives only InstanceId, Sequence, MadeRunnable and Now. No unused null clock is sent as sql_variant.

## SQL contract

Sql supplies fixed statements and predicates. ReadInstanceSql(filter, wholly) and QuerySql(filter) are the only builders that accept internal predicates; caller values remain parameters. Required SQL is checked before Dapper sees it, and a missing or empty statement reports both its name and provider.

Optional fixed statements are LockIdentity, AdvanceInbox (when InsertEvent advances the inbox), SaveParts (when Save includes it), and LockAndReadPage. A supplied LockAndReadPage acquires the inbox lock and returns the boundary, events and waits in one statement batch. Otherwise the base runs LockInbox and then requires PendingEvents. SQL Server retains its single round trip through LockAndReadPage.

| Statement group | Results and ordering |
| --- | --- |
| QuerySql | InstanceId, StateJson, RunnableAtUtc, Revision; Incarnation when stamp checks are needed. |
| ClaimDeliveries, DeliveryLock, DeliveryRead | EventId, EventJson, PublisherName, Attempts, NextAttemptAtUtc, Status, LastError, LeaseToken, LeaseUntilUtc. ClaimDeliveries locks up to Limit due records and skips locked rows on server stores; DeliveryLock locks one record. |
| Publishers, PublisherRead | Name, TypesJson, DisabledAtUtc, LastError. |
| OutboxClock | The store clock after delivery locks have been acquired. SQLite samples its injected clock instead. |
| ReadInstanceSql | Trunk, then parts (InstanceId, PartKey, Json), then current stamp (InstanceId, Revision, Incarnation) when needed. Retry reads reapply the original filter. |
| ReadParts | Parts for selected IDs, then current stamps when needed. Snapshot reads return only parts. |
| LockInbox, DeleteParentInbox | Status and InboxSequence, followed by server time for server-clock stores. |
| ParentInbox | Status and InboxSequence, with no clock result. |
| CommitState | Revision, CancellationJson, CancellationAcknowledged, InboxSequence, followed by server time when applicable. |
| Receipt, Lease | ExpectedRevision, Revision, PayloadHash; Epoch, CrashCount, IsActive respectively. |
| Running, ClaimLease | Running always returns a count. ClaimLease returns Epoch and CrashCount, or no row when held. |
| ReleaseLease, Save | Affected count, or nullable due flag when the corresponding ReturnsDue capability is enabled. No row means release/save did not apply. |
| LockAndReadPage | Boundary (Through, Now), then the same events and waits as PendingEvents. |
| PendingEvents | Events, then page waits (WaitId, OpenedSequence, DeadlineUtc). |
| Event, CandidateWait, Wait, OpenWaits | EventColumns or WaitColumns projection. |
| EventHash, TakenChild, EarliestDeadline | Sequence/PayloadHash; existing child ID or null; earliest open deadline or null. |
| Runnable, Cancellation, Cancellations, InboxSequences, Trace | InstanceId/RunnableAtUtc; cancellation JSON; InstanceId/cancellation JSON; InstanceId/InboxSequence; Sequence/EntryJson. |
| DeleteState | State JSON or no row. |

Writes that return rows (ClaimLease and Save with SaveReturnsDue) may use a multi-statement batch ending in SELECT where RETURNING/OUTPUT is unavailable. ByName, ByStatus, ByCorrelation and ByParent use @Value.

## Deletion and provider capabilities

Deletion is one DeletePlan: the lease-reset position and an optional locked-state revalidation statement. DeleteLease is required. SQL Server resets the lease before the transaction, then locks the parent and deletes the instance and dependents by key. SQLite reads state and resets the lease inside its immediate transaction before reading the parent. PostgreSQL locks the parent, identity and instance, revalidates the parent, then resets the lease; a changed parent rolls back and retries, and an absent locked instance leaves any later insertion whole. Lease rows survive deletion so recreated instances never reuse an epoch.

The remaining policies describe database capabilities: stamp rechecks versus one WAL snapshot, timestamp normalization, parameter-limited batching, key-ordered bulk child insertion to avoid deadlocks, local versus server clocks, returned due flags for notifications, and retryable database concurrency errors. Their declarations document their meaning.

Batch hooks bind nonempty settlements, waits, parts, trace and child starts. They never commit, open connections, decide transitions or sample clocks. SQL Server batches at 400 rows (1,000 IDs for ownership reads), PostgreSQL uses typed unnest arrays, and SQLite uses parameter sets. PostgreSQL sorts children by ordinal ID to avoid overlapping insert deadlocks; the others retain payload order. Settlements return the affected count; trace appends consecutive numbers under the instance lock.

RequestCancellationAsync executes atomic provider logic. A null clock uses server time; a supplied clock is the commit's captured time or SQLite's time inside its write transaction. The outcome distinguishes unchanged, recorded, made-runnable and gone. SQL Server shares one UPDATE template for both clock sources. PostgreSQL retains its CTE and conditional transactional NOTIFY; SQLite retains its read/update pair. Only PostgreSQL supplies runnable notifications and LISTEN/NOTIFY wake-ups.

Read assembly retains revision/incarnation checks for SQL Server and PostgreSQL and one deferred WAL snapshot for SQLite. SQL Server preserves its autocommit lease release; saves now use a transaction so a terminal save and its outbox event are atomic. PostgreSQL retains transactional save/release/cancellation. SQLite preserves immediate writes, file ownership, pool clearing, disposed checks and busy/locked error mapping. Deadlock and serialization mappings and notification conditions remain provider-specific.

## Publisher delivery protocol (D-40)

The outbox has immutable event rows, publisher registrations with persisted endpoint disablement, and delivery rows keyed by event ID and publisher name. Completion insertion and delivery materialization are part of the terminal commit or save transaction. Event deduplication uses the instance incarnation; SQLite now stores an incarnation too. Repeated commits never regenerate the event ID. Deleting an instance retains pending deliveries and their immutable events.

Completion materialization takes the publisher lock shared; registration, unregistration and disabling take it exclusive. SQL Server uses a transaction application lock and PostgreSQL an advisory transaction lock. SQLite uses its already-held immediate writer transaction without a lock query. Registration starts with future events unless replayRetained is explicitly enabled; it never resets a delivery or re-enables a disabled publisher. Unregistration gives up pending deliveries and removes the subscription. Publishers absent from a particular host remain registered until explicitly unregistered.

ClaimDeliveries takes delivery row locks, then the core samples OutboxClock and stamps the lease and the first-attempt time while incrementing attempts. DeliveryUpdate persists a row only under that transaction and lock. Renewal and settlement lock the delivery, read the store clock, and reject an expired or replaced token. A retry sends only a duration; the core stamps its next attempt from store time. Disabling is atomic with the fenced settlement and gives up all other pending deliveries for that publisher. Acknowledgments after disablement cannot revive a delivery.
