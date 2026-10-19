---
title: Large payloads
description: Checkpoint references, payload limits and recovery of unavailable data.
---

IxIFlow stores whole JSON values larger than `PayloadThresholdBytes` outside the checkpoint and keeps their SHA-256 digest and exact UTF-8 byte length in the checkpoint. The default is **256 KiB**, enabled. Set the option to `0` to disable new claim checks. Existing references still need their payload store to read them.

The checkpoint seams include root workflow data, branch data changes, saved activity input copies, `Previous`, ForEach items and results, bound and accepted replies, and wait default payloads. Large structural parts can themselves be references; their nested references are recorded as dependencies. Unchanged immutable values reuse their cached encoding, length, digest and dependencies. Data observation detects writes through activity contexts and mappings without serializing or hashing unchanged string values. Opaque converter shapes are conservatively treated as changed.

| Option | Default | What it bounds |
| --- | --- | --- |
| `PayloadThresholdBytes` | 256 KiB | Whole values above this UTF-8 size become references; zero disables new references |
| `MaxPayloadBytes` | 64 MiB | One payload put; an oversized put is refused with `WorkflowErrorKind.ResourceExhausted` |
| `MaxHydratedPayloadBytes` | 128 MiB | Aggregate unique payload bytes hydrated in one run, and the new bodies one checkpoint carries |
| `MaxCheckpointBytes` | 4 MiB | Checkpoint trunk and parts, counting a reference's encoded metadata instead of its body |

The aggregate byte budget counts UTF-8 body lengths. It is a limit on payload data, rather than an estimate of every managed allocation or the memory used by application activities. Repeated reads of one digest share a run cache. Reaching the aggregate budget stops the run for an operator with its last checkpoint kept; raise the budget and release it.

The state store supplies `IPayloadStore` by default. `UsePayloadStore` selects another provider; its durable catalog must participate in the same pin protocol and referencing transaction as the state repository. Payload data has its own ownership and lifecycle, separate from code artifacts. Deploy its storage, credentials, encryption and backups inside the workflow data's residency boundary. Existing stores require recreation for the current checkpoint-column encoding; there is no compatibility reader.

The host publishes each new body before the referencing commit. The commit acquires checkpoint-owner pins in the same transaction as its state write. Other owners' inbox or reader pins are independent. Nested dependency pins retain the entire closure. SQL Server uses a transaction application lock per digest, PostgreSQL uses a transaction advisory lock keyed from the digest, SQLite uses its writer transaction, and memory uses per-digest locks. Collection and publication retain their generation fences. An acknowledgement lost after commit does not release pins: replay publishes the same bytes harmlessly and returns the original receipt.

Executors request bodies asynchronously from the host. They do not access a payload store. Structural checkpoint parts are read during assembly; workflow data is read when a run needs its data; saved inputs are read for restoration; `Previous` is read for the next transition. ForEach item bodies are read as their items run, and saved results when the join consumes them. New bodies travel whole with the checkpoint that references them. A read returns the whole verified body from an independent snapshot, pinned until the snapshot is ready.

A missing body or a length or digest mismatch produces `WorkflowErrorKind.PayloadUnavailable`. The host records an operator stop using a fenced metadata-only commit. It keeps the saved parts and their pins without assembling or deserializing unreadable state. Automatic recovery leaves the instance stopped. Restore the original bytes under the same digest, then call `ReleaseAsync`; events received while stopped remain pending. A failed read of an already terminal result reports the payload error without reopening the instance. Temporary store failures remain infrastructure failures.

Step 3 adds claim checks to inbox events, child starts and child completions. Those transport and persistence boundaries remain inline in this step; replies already bound or accepted into a checkpoint use the checkpoint claim check. Automatic orphan collection and retention-driven pin release are subsequent work.
