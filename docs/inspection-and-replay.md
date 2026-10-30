---
title: Inspecting and replaying workflows
description: Read saved workflow positions and navigate committed trace history without executing workflow code.
---

# Inspecting and replaying workflows

Resolve `IWorkflowOperations` from dependency injection and call `InspectAsync(instanceId)` to read an `InstanceInspection`. It describes one saved revision, including the workflow summary and structure hash, committed debug state and breakpoint hit counts, threads and their nested frames, pending activity attempts, waits, and each continuation's data view. The revision identifies the saved state; a running activity's uncommitted changes are not visible. An unknown instance returns null. A missing or incompatible definition is refused.

Threads use continuation identities. Frames use activation identities, so repeated visits to the same loop, parallel block or item remain distinct. Loop iteration numbers and ForEach item indices follow the live snapshot's naming and ordering. A branch names its parent continuation, join and fork activation. Completed or cancelled threads disappear, matching the live snapshot.

## Read trace pages

Call `GetTraceAsync(instanceId, afterSequence, limit)` on `IWorkflowOperations`, beginning at zero. A page contains `StoredTraceEntry` records strictly after that sequence. Continue with the last returned sequence until an empty page. The limit must be between 1 and 1000. Pages do not freeze a running instance; append-only sequences let a reader continue while execution proceeds. Removed or unknown histories return an empty page.

Sequence is the authoritative order. Each entry also carries its timestamp and `Host` or `Store` clock source; timestamps can go backwards when the two clocks differ. Activity and control-flow entries include the catalog step, continuation and frame activation. Value-free thread positions preserve stack changes, from which replay derives scope entry and exit. Stored entries carry `Revision`, `CommitEndSequence` and `CommitStatus` as metadata; commits add no boundary event and consume no trace budget. The end sequence remains available when a page stops partway through a commit. Standalone inbox refusals carry the observed revision and null commit metadata. The transition trace contains no checkpoint data values. Inspection trace reads project error text and attempt/operator notes through the [redaction policy](redaction.md); raw store reads remain execution primitives.

## Navigate recorded execution

Pass the same workflow definition and all ordered pages to `TraceReplay.Load(definition, entries)`. The cursor starts at the first available sequence. `Current` returns a detached projection with its threads and frames, timestamp, clock, last committed revision and completeness information. `StepForward`, `StepBackward` and `Seek(sequence)` move through recorded sequences. `SetBreakpoints(catalogStepIds)` sets cursor stops; `Continue()` and `Continue(backwards: true)` move to the next recorded step occurrence or the end. Cursor breakpoints use catalog addresses. They do not evaluate live breakpoint conditions or change the instance.

Replay invokes no activity, condition, item selector or data combiner. It reads recorded decisions and positions, beginning with the instance's single origin. A recorded structure hash that differs from the supplied definition is refused. Histories from different instances, duplicate sequences and out-of-order sequences are refused. Missing prefixes, sequence gaps, recording-limit markers, reduced control-flow detail, empty histories and tails that end before their declared commit end produce an explicitly incomplete reconstruction. Later positions do not make omitted earlier history complete. A commit with no trace entries has no cursor position; a trace alone cannot prove that a whole suffix of execution is absent.

Replay data is absent by default. To display values at a saved checkpoint, call `AttachCheckpoint(inspection)` with an inspection of that exact instance, structure hash, revision and position. `Current.Data` is then available only on that revision's commit boundary. At other sequences it is null. Historical data cannot be inferred from a trace, and replay never interpolates values between checkpoints.

## Access and capability privacy

Applications must restrict inspection, paged trace reads and attached checkpoint values to callers with reply rights for the instance. Authorization belongs to the application. These APIs do not mint reply tokens. Waits use non-answerable `WaitRef` values. Projection types exclude raw wait IDs, parent wait IDs, accepted replies, stored timer maps and reply tokens. Pending-attempt summaries omit activity inputs and outputs; scope inspection separately exposes saved inputs/outputs through redaction. Replay exposes positions rather than the raw activity metadata or checkpoint.

Inspection values honor `[Sensitive]` classifications and the application's `Microsoft.Extensions.Compliance.Redaction.IRedactorProvider`. Errors expose safe diagnostic identities; plain string notes are wholly sensitive and erased by default. Privileged diagnostic reads are separate. As defense in depth, inspection and paged reads also erase recognizable encoded reply tokens and known checkpoint wait IDs from copied text and data. Applications must classify values that can contain capabilities, including historical or foreign raw IDs. See [redaction](redaction.md) for the policy and its boundaries.

Children run independently. A parent's inspection identifies a child instance without exposing its completion wait capability. Inspect and replay each child separately using its own definition and trace.

## Open replay through operations

`IWorkflowOperations.OpenReplayAsync(instanceId, maxEntries: 10000, cancellationToken: cancellationToken)` loads the definition and ordered, privacy-projected trace pages. The entry bound accepts 1 through 100,000; a longer history is refused rather than silently truncated. It attaches the current inspection only when the final trace entry ends the same saved revision. Inspection, trace paging and replay loading accept cancellation tokens.

## Retained invocations

`InstanceInspection.Invocations` lists durable records retained while an occurrence can resume. Completed occurrences are removed when no live continuation references them; their history belongs to the committed trace, subject to its detail and limits. Each includes pinned artifact/contract identities, scheduled retry count and due time, ordered policy-attempt IDs/numbers, preparation/start/end times, child identities/statuses, policy action/disposition, operator-decision kind and safe diagnostic reference. It contains no input payloads, raw exception messages or wait capabilities. A termination closure projects open attempts as ended even when management termination preserved their external part bodies.

Pending invocation listings expose `PolicyAttemptId`. Submit that observed ID in `ActivityResolution.CompletedInvocation(..., expectedPolicyAttemptId: pending.PolicyAttemptId)` or `ActivityResolution.Failed(..., expectedPolicyAttemptId: pending.PolicyAttemptId)`. The HTTP resolution body uses `expectedPolicyAttemptId`; missing targets return 400 and stale targets return 409. Rejected decisions change no outputs, retry budget or accepted-resolution trace. The logical invocation ID is a correlation key, not a sufficient resolution target.

See [invocation trace correlation](observability.md#invocation-attempt-correlation) for payload names and attempt identities. Replay `Current.Invocations` retains recorded attempt facts and due times; `Current.Resolutions` retains the original operator targets. Trace omissions still make replay incomplete. Checkpoint inspection supplies retained resumable records; it cannot reconstruct completed occurrences omitted from the trace.

## Scope activities

`InstanceInspection.Scopes` contains activation ID, owner continuation, step ID, value type, threading, concurrency, phase, recorded-open count and redacted saved inputs/outputs. Payload hydration uses the inspection budget. `InspectionThread.ScopesInForce` lists its own and inherited activation IDs, including closing entries still needed by a branch. Scope frames in inspection and execution snapshots have `Kind = "Scope"` and the owning step's name.

The live resource is never inspected. Saved values describe the recorded open; they do not promise a live session or expose later resource mutations. DAP presents these values in its read-only Scopes scope. Replay retains scope frame positions; historical lifecycle values are unavailable and are never filled from current inspection. See [scope debugging](debugging.md#scope-activities).
