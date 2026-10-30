---
title: After 1.0
description: Features outside the IxIFlow 1.0 release.
---

## After 1.0

IxIFlow 1.0 runs workflows in the host process. The following features are outside this release:

- Process isolation: separate executor programs and process slots, pipe transport, liveness checks, hard timeouts, resource accounting and fenced crash reports. Deployment tooling supplies process memory limits.
- Native AOT for compiled-in workflows, with trimming and publish/run verification.
- Broker publisher and consumer examples for RabbitMQ and Azure Service Bus.
- A distributed map that pages external input, bounds child concurrency and stores results outside the parent checkpoint.
- Composed durable-step and workflow-call state-machine triggers.

See [engine capabilities](engine-capabilities.md) for the 1.0 feature set and [hosting](coordinator-and-hosts.md) for its deployment boundaries. These directions carry no delivery dates.
