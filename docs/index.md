---
title: IxIFlow documentation
description: Author, run and operate durable .NET 10 workflows.
---

IxIFlow is a durable workflow engine for .NET 10 and C#. Author a typed definition with the fluent API or YAML, run it through `IWorkflowClient`, and choose a store for checkpoints, inboxes, waits, traces and completion deliveries.

These guides describe the engine as it is today. Features still planned for 1.0 are identified explicitly in the [roadmap](roadmap.md).

## Guides

| Page | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | Packages, a first workflow and client results |
| [Fluent C# API](fluent-api.md) | Mappings and every control-flow family |
| [YAML authoring](yaml-authoring.md) | Every document construct, expressions, strict reading and schema |
| [Activity SDK](activity-sdk.md) | Activity contracts, .NET metadata, DI and installed catalogs |
| [Execution model](execution-model.md) | Commits, recovery, leases, inboxes, waits, children and parallel data |
| [Sagas](sagas.md) | Compensation, saved faults and retry scopes |
| [Reply tokens](reply-tokens.md) | Capability tokens, safe handling and reply authorization |
| [Hosting](coordinator-and-hosts.md) | The core host, message contract, slots, demands, recovery, shutdown and unschedulable work |
| [Placement and data residency](coordinator-and-hosts.md#placement-and-data-residency) | Inherited compute demands and a separate cluster/store per region |
| [Coordinators](coordinators.md) | Local matching services, host links, awaits, discovery and fallback |
| [Workflow operations](operations.md) | Instance search, wait inspection, trace pages and hard termination |
| [Operations over HTTP](operations-http.md) | Management endpoints, policies, query scope, actors and bounded waits |
| [Stores](stores.md) | Memory, SQLite, SQL Server and PostgreSQL; persistent catalog and artifact retention |
| [YAML publishing](yaml-authoring.md#publishing-and-boundaries) | Import-free drafts, verified immutable bundles and remaining deployment boundaries |
| [Incoming webhooks](incoming-webhooks.md) | Authorized triggers, atomic idempotency, CloudEvents and public status |
| [Outbox and publishers](outbox-and-publishers.md) | Completion delivery, custom publishers, outgoing webhooks and templates |
| [Retention and tombstones](retention-and-tombstones.md) | Retained results, cleanup and removed instance IDs |
| [Tracing and observability](observability.md) | Committed history, snapshots, OpenTelemetry and logging |
| [Marking and redaction](redaction.md) | Classified members, inspection erasure and optional HMAC pseudonyms |
| [Architecture](architecture.md) | Runtime boundaries and packages |
| [Engine capabilities](engine-capabilities.md) | A compact guide to available behavior |
| [Roadmap](roadmap.md) | Remaining 1.0 work and later features |

## Examples

The [sample feature map](../samples/README.md) links runnable C# and YAML twins. Every C# or YAML block in these guides names its compiled source or equivalent. [DocumentationExamples.cs](../samples/IxIFlow.Samples/DocumentationExamples.cs), [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs) and the existing sample tests keep the examples checked by the solution build and sample test run.
