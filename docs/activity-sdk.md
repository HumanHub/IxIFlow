---
title: Custom activity SDK design
description: Proposed runtime and Studio contracts for custom activity libraries.
---

This page describes the proposed SDK contract. The current engine executes user-defined `IAsyncActivity` classes, and the preliminary document compiler resolves activity keys through `IActivityRegistry`. The catalog, Studio integration, and YAML authoring path below are still being designed.

## One activity package, two parts

An activity package should contain a .NET assembly for execution and a versioned activity manifest for authoring. The manifest names each activity, its category, input and output types, required properties, validation rules, and optional child slots. The management API can expose the manifest to Studio. The same keys and property rules must be used by the YAML compiler.

Studio should render most nodes and property forms from this manifest. A package may also include a separately built Vue extension for a distinctive node or field editor. Studio loads only registered extensions. A .NET assembly cannot inject a Vue component into the browser on its own. The Vue component edits the same activity configuration that YAML stores; it does not execute the activity.

Built-in control flow such as `If` is a workflow definition node with `then` and `else` child lists. It can have a custom Vue Flow renderer with labeled branch handles. A custom activity can have its own Vue Flow renderer and property panel without creating a separate workflow format.

## First exercise: Order Intake

The first demo library should include `Read JSON file`, `Query customer`, and `Write JSON file`. `Query customer` uses a PostgreSQL connection reference, parameter bindings, SQL text, and a typed result. Put a built-in `If` between the file read and the query so both paths can be edited and saved as YAML. A later `With database connection` container can test custom child slots.

Published definitions should contain a connection or secret reference. The operator configures the actual connection string outside the workflow document. This lets the same workflow run on another host without copying credentials into YAML. Use a disposable PostgreSQL container for execution tests on Windows and Linux.

A database transaction scope is distinct from a connection scope. A live transaction cannot be saved in a checkpoint, so validation should reject a suspension inside a transaction scope until the runtime has an explicit recovery model. A normal connection scope can reopen a connection after resume.

## SDK acceptance test

1. Install the activity assembly and publish its manifest.
2. Find its activities in Studio's toolbox and configure them with generic fields or a registered Vue editor.
3. Save to YAML, reopen the YAML in Studio, and verify the same activity IDs, settings, branches, and bindings.
4. Compile through the same validator used by the fluent and YAML paths; show any property or expression error at the corresponding node.
5. Execute with the memory provider in process. Later, repeat with a durable provider and resume on another host.

The [Studio designer exercise](/studio-demo/) shows the canvas, a custom database node, manifest-driven properties, and YAML editing. It is an interactive authoring exercise; it does not yet run the workflow or load a .NET activity assembly.

## Package boundaries and embedding

Keep execution, authoring, and editing separate:

| Package | Responsibility |
| --- | --- |
| `IxIFlow` | Run a compiled workflow in process. No designer, web server, or SQL dependency. |
| `IxIFlow.Authoring` | Own the versioned workflow document, YAML reader and writer, validation, and compilation to the runtime graph. |
| `IxIFlow.ActivitySdk` | Help activity authors register a .NET activity, its stable key, input and output metadata, and property rules. It builds on the runtime and authoring contracts. |
| Vue editor package | Render the activity palette, canvas, property editor, diagnostics, and YAML view. It receives the document and activity catalog through an application adapter. |
| Studio application | Supply login, storage, publishing, instance inspection, and API integration around the reusable editor. |

The editor should ship as an npm package that a Vue application can mount in its own page. A small web-component wrapper can make the same editor available to React, Angular, Razor, or plain HTML hosts. The standalone Studio should consume the package too. A host may provide its own save and validation adapters, so embedding the editor does not require the IxIFlow coordinator. The website is a consumer of the editor package, not its permanent source.

YAML remains the workflow source format for document-authored workflows. An API may transport the parsed document as JSON, and generated TypeScript types may describe it, but those are representations of the same authoring contract rather than a second workflow language.

## Canvas interaction

Use top-to-bottom layout for structured workflows. A sequence is ordered from top to bottom; an `If` owns distinct Then and Else paths. Every path and position between activities should have a visible insertion target. Drag an activity from the palette to that target, or select an activity and activate the target by keyboard. A target belongs to a specific control-flow node, so ten `If` steps still have unambiguous Then and Else destinations.

For structured constructs, the document's child lists define execution order and the canvas derives its lines from them. Freehand connections would allow links the document cannot represent. If arbitrary links, back edges, and joins are added later, define a separate flowchart construct with explicit graph semantics and validation. Canvas coordinates are editor layout data and never change execution order.
