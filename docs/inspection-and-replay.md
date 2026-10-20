---
title: Inspecting and replaying workflows
description: Read saved workflow positions and navigate committed trace history without executing workflow code.
---

# Inspecting and replaying workflows

Resolve `IWorkflowOperations` from dependency injection and call `InspectAsync(instanceId)` to read an `InstanceInspection`. It describes one saved revision, including the workflow summary and structure hash, committed debug state and breakpoint hit counts, threads and their nested frames, pending activity attempts, waits, and each continuation's data view. The revision identifies the saved state; a running activity's uncommitted changes are not visible. An unknown instance returns null. A missing or incompatible definition is refused.

Threads use continuation identities. Frames use activation identities, so repeated visits to the same loop, parallel block or item remain distinct. Loop iteration numbers and ForEach item indices follow the live snapshot's naming and ordering. A branch names its parent continuation, join and fork activation. Completed or cancelled threads disappear, matching the live snapshot.

## Read trace pages

Call `GetTraceAsync(instanceId, afterSequence, limit)` on `IWorkflowOperations`, beginning at zero. A page contains `StoredTraceEntry` records strictly after that sequence. Continue with the last returned sequence until an empty page. The limit must be between 1 and 1000. Pages do not freeze a running instance; append-only sequences let a reader continue while execution proceeds. Removed or unknown histories return an empty page.

Sequence is the authoritative order. Each entry also carries its timestamp and `Host` or `Store` clock source; timestamps can go backwards when the two clocks differ. Activity and control-flow entries include the catalog step, continuation and frame activation. Value-free thread positions preserve stack changes, from which replay derives scope entry and exit. Stored entries carry `Revision`, `CommitEndSequence` and `CommitStatus` as metadata; commits add no boundary event and consume no trace budget. The end sequence remains available when a page stops partway through a commit. Standalone inbox refusals carry the observed revision and null commit metadata. The trace never contains checkpoint data values; existing activity errors and notes remain application text.

## Navigate recorded execution

Pass the same workflow definition and all ordered pages to `TraceReplay.Load(definition, entries)`. The cursor starts at the first available sequence. `Current` returns a detached projection with its threads and frames, timestamp, clock, last committed revision and completeness information. `StepForward`, `StepBackward` and `Seek(sequence)` move through recorded sequences. `SetBreakpoints(catalogStepIds)` sets cursor stops; `Continue()` and `Continue(backwards: true)` move to the next recorded step occurrence or the end. Cursor breakpoints use catalog addresses. They do not evaluate live breakpoint conditions or change the instance.

Replay invokes no activity, condition, item selector or data combiner. It reads recorded decisions and positions, beginning with the instance's single origin. A recorded structure hash that differs from the supplied definition is refused. Histories from different instances, duplicate sequences and out-of-order sequences are refused. Missing prefixes, sequence gaps, recording-limit markers, reduced control-flow detail, empty histories and tails that end before their declared commit end produce an explicitly incomplete reconstruction. Later positions do not make omitted earlier history complete. A commit with no trace entries has no cursor position; a trace alone cannot prove that a whole suffix of execution is absent.

Replay data is absent by default. To display values at a saved checkpoint, call `AttachCheckpoint(inspection)` with an inspection of that exact instance, structure hash, revision and position. `Current.Data` is then available only on that revision's commit boundary. At other sequences it is null. Historical data cannot be inferred from a trace, and replay never interpolates values between checkpoints.

## Access and capability privacy

Applications must restrict inspection, paged trace reads and attached checkpoint values to callers with reply rights for the instance. Authorization belongs to the application. These APIs do not mint reply tokens. Waits use non-answerable `WaitRef` values. Projection types exclude raw wait IDs, parent wait IDs, accepted replies, stored timer maps, activity inputs and outputs, and reply tokens. Replay exposes positions rather than the raw activity metadata or checkpoint.

As defense in depth, inspection and paged reads erase recognizable encoded reply tokens and known checkpoint wait IDs from copied text and data. This does not replace classification-based redaction: data, previous outputs, error messages, notes and arbitrary caller strings can copy capabilities, including historical or foreign raw IDs. The redaction stack must cover these fields when it joins this branch, including error text and operator notes. Until then, the reply-rights restriction is required; an inspect-only permission must not expose these views.

Children run independently. A parent's inspection identifies a child instance without exposing its completion wait capability. Inspect and replay each child separately using its own definition and trace.
