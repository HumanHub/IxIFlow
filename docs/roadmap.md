---
title: Roadmap
description: What comes after IxIFlow 1.0.
---

IxIFlow 1.0 is the engine: typed fluent and YAML authoring, durable execution, publishing, catalogs, coordinators and the debugger. The next step is a visual designer built on it.

## Next: a workflow and activity designer

A web editor for designing workflows and activities without writing their documents by hand:

- **Workflow editor.** Build sequences, branches, parallel paths, waits, sagas and workflow calls on a canvas. The editor works on the same YAML document that the engine validates, publishes and runs, so a design and its document stay one artifact.
- **Activity designer.** Describe an activity's inputs, outputs, defaults and presentation, and author YAML code activities.
- **Catalog-driven palette.** Installed and published activities, templates and workflows appear with the descriptors that the catalog APIs return.
- **Publishing from the editor.** Validate, preview and publish drafts through the [publishing HTTP API](publishing-http.md), with its authorization, preconditions and publication history.

The designer uses the 1.0 publishing and catalog APIs; the engine remains a library that runs in your process. There are no delivery dates. See [engine capabilities](engine-capabilities.md) for what 1.0 provides today.
