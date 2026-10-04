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
