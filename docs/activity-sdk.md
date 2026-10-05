---
title: Custom activity SDK
description: Current activity registration and editor contracts.
---

This page describes the activity package boundary and its current prototype. The engine executes user-defined `IAsyncActivity` classes. `IxIFlow.ActivitySdk` reads activity keys, inputs, outputs, display names, icon names, and designer IDs from C# attributes on those classes. `ActivityPackageRegistry.AddAssembly` builds a catalog from an installed assembly and its package identity. The authoring compiler checks package and activity versions, and YAML can compile and run with a registered activity. The browser editor still uses a separate draft workflow shape and cannot execute it.

## One activity package, two parts

An activity package contains a .NET assembly for execution and may contain Vue designer assets. Authors define activity metadata once, on the C# type and its properties. The host derives a versioned catalog from the installed assembly and can serve that catalog to the editor. The catalog is a transport format; activity authors do not maintain a second metadata file. The same keys and property rules must be used by the YAML compiler.

The split follows the useful part of Windows Workflow Foundation's activity/designer model: an activity type runs on the host, while design metadata associates it with a designer. In IxIFlow, the stable activity key and version are the link. A host needs the matching .NET assembly; a browser needs the derived catalog and, when provided, a registered Vue designer extension. The browser never loads the .NET assembly. The current catalog includes inputs, outputs, simple field rules, and defaults. Runtime-managed child slots, service-slot metadata, designer asset loading, and NuGet package resolution still need implementation.

The structured editor has a shared activity frame for the icon, type name, editable title, annotation, move control, and collapse control. The activity designer supplies the content inside that frame. When no custom designer is registered, the editor shows fields from the catalog. The `Icon` value on `WorkflowActivityAttribute` becomes an icon name in the derived catalog; the browser chooses a registered icon or falls back to a generic box. A package may include a separately built Vue extension for a distinctive body or field editor, but browser-side extension loading is not implemented yet. A .NET assembly cannot inject a Vue component into the browser on its own.

Built-in control flow such as `If` is a workflow definition node with `then` and `else` child lists. The editor gives it the same frame and an inline condition, with separate nested Then and Else areas. Sequence, Parallel, loops, handlers, and saga use their own child slots. The earlier Vue Flow graph experiment remains available as a reference for a future Flowchart activity; ordinary workflows use the structured editor.

## First exercise: Order Intake

The demo library under `Activities/IxIFlow.DemoActivities` includes `Read JSON file`, `Query customer`, and `Write JSON file`. `Query customer` uses a connection reference, a parameterized query, and a JSON result. The editor example puts a built-in `If` between file read and query so both paths can be edited and saved as draft YAML. `With database connection` is the next runtime and designer test for custom child slots; it must schedule its Body through the workflow engine so suspension and resumption work at child boundaries.

Published definitions should contain a connection or secret reference. The operator configures the actual connection string outside the workflow document. This lets the same workflow run on another host without copying credentials into YAML. Use a disposable PostgreSQL container for execution tests on Windows and Linux.

A database transaction scope is distinct from a connection scope. A live transaction cannot be saved in a checkpoint, so validation should reject a suspension inside a transaction scope until the runtime has an explicit recovery model. A normal connection scope can reopen a connection after resume.

## SDK acceptance test

1. Install the activity assembly and expose its derived catalog.
2. Find its activities in Studio's toolbox and configure them with generic fields or a registered Vue editor.
3. Save to YAML, reopen the YAML in Studio, and verify the same activity IDs, settings, branches, and bindings.
4. Compile through the same validator used by the fluent and YAML paths; show any property or expression error at the corresponding node.
5. Execute with the memory provider in process. Later, repeat with a durable provider and resume on another host.

The [workflow editor exercise](/studio-demo/) shows nested activity frames, a database activity, built-in flow steps, local catalog loading, and local YAML file open/download. Titles and annotations are saved in its draft YAML. It accepts only its draft example shape, reformats YAML when applied, and does not yet run the workflow or load a .NET activity assembly. The demo catalog in the Site repository is a temporary browser fixture; the host must supply the catalog derived from the assembly to remove that duplication.

## Package boundaries and embedding

Keep execution, authoring, and editing separate:

| Package | Responsibility |
| --- | --- |
| `IxIFlow` | Run a compiled workflow in process. No designer, web server, or SQL dependency. |
| `IxIFlow.Authoring` | Own the versioned workflow document, YAML reader and writer, validation, and compilation to the runtime graph. |
| `IxIFlow.ActivitySdk` | Read activity metadata from C# attributes, register installed assemblies, and derive editor catalogs. It builds on the runtime and authoring contracts. |
| Vue editor package | Render the activity palette, structured activity frames, diagnostics, and YAML view. It receives the document and activity catalog through an application adapter. |
| Studio application | Supply login, storage, publishing, instance inspection, and API integration around the reusable editor. |

The editor should ship as an npm package that a Vue application can mount in its own page. A small web-component wrapper can make the same editor available to React, Angular, Razor, or plain HTML hosts. The standalone Studio should consume the package too. A host may provide its own save and validation adapters, so embedding the editor does not require the IxIFlow coordinator. The website is a consumer of the editor package, not its permanent source.

YAML remains the workflow source format for document-authored workflows. An API may transport the parsed document as JSON, and generated TypeScript types may describe it, but those are representations of the same authoring contract rather than a second workflow language.

## Structured editor interaction

Use top-to-bottom layout for structured workflows. A sequence is ordered from top to bottom; an `If` owns distinct Then and Else areas. A downward marker between activities shows order and opens insertion choices. Drag an activity from the palette to that marker, or select an activity and activate the marker by keyboard. A marker belongs to a specific child list, so ten `If` steps still have unambiguous Then and Else destinations. A step can be moved by dragging its header handle or using Alt+Up and Alt+Down within its list.

For structured constructs, the document's child lists define execution order. If arbitrary links, back edges, and joins are added later, define a separate Flowchart construct with explicit graph semantics and validation. Its canvas coordinates would be editor layout data and would not change execution order.
