# Contributing to IxIFlow

Use [GitHub issues](https://github.com/HumanHub/IxIFlow/issues) for bug reports and feature discussions. Include a minimal reproduction, expected and observed behavior, SDK/OS versions and relevant sanitized diagnostics. Discuss larger API or behavior changes before implementing them. Keep pull requests focused and update documentation and relevant tests with the change.

## Checkout and build

Install the .NET 10 SDK and Git. An editor with .NET 10 support is sufficient; the compiler tools also target `netstandard2.0`. Node.js 18 or later is required only for the VS Code extension tests. Package restore needs access to NuGet.org.

```sh
git clone https://github.com/HumanHub/IxIFlow
cd IxIFlow
dotnet restore IxIFlow.sln
dotnet build IxIFlow.sln -c Release
dotnet tool restore
```

Create a branch from `main`; contributors submitting from forks can set their fork as a remote. Build and run the relevant test suites before opening a pull request.

## Repository structure

| Location | Contents |
| --- | --- |
| `src/IxIFlow` | Fluent builders, validation, client, in-process host/executor, memory store and telemetry |
| `src/IxIFlow.Abstractions` | Activity, recovery and scope contracts, attributes and combiners |
| `src/IxIFlow.Authoring` | YAML/document APIs, package resolution, catalogs and publishing |
| `src/IxIFlow.Generators` | YAML document model, translation and source generator |
| `src/IxIFlow.Analyzers`, `src/IxIFlow.Analyzers.CodeFixes` | Compiler diagnostics and fixes |
| `src/IxIFlow.Relational` | Shared implementation for relational providers |
| `src/IxIFlow.Sqlite`, `src/IxIFlow.SqlServer`, `src/IxIFlow.PostgreSql` | Provider repositories and registration |
| `src/IxIFlow.Coordinator` | Host discovery, stateless matching and authenticated links |
| `src/IxIFlow.AspNetCore` | Authorized triggers, operations and publishing HTTP mappings |
| `src/IxIFlow.Webhooks` | Signed outbox delivery over HTTP |
| `src/IxIFlow.Templates`, `src/IxIFlow.Templates.Liquid` | Template contracts/activities and Liquid rendering |
| `src/IxIFlow.Debugging` | Conditional breakpoints and Debug Adapter Protocol endpoint |
| `src/Shared` | Source shared by engine and compiler projects |
| `tests/IxIFlow.Tests` | Engine, API, provider, authoring and repository guards |
| `tests/IxIFlow.Conformance` | Store/host profile scenarios |
| `tests/IxIFlow.ExecutionContract.Tests` | Host/executor message contract tests |
| `tests/IxIFlow.Samples.Tests`, `tests/IxIFlow.Generators.Tests`, `tests/IxIFlow.Analyzers.Tests` | Samples/docs, generator and analyzer suites |
| `tests/IxIFlow.TestSupport`, `tests/IxIFlow.BundleFixtures` | Shared fixtures, test compilation and bundle inputs |
| `samples/IxIFlow.Samples` | Runnable console and C#/YAML examples |
| `samples/IxIFlow.Recipes` | Wait, reply, payment and list recipes |
| `samples/IxIFlow.DemoActivities` | Sample activity library/package |
| `extensions/ixiflow-debug` | VS Code debugger extension and Node tests |
| `docs` | Public guides and diagnostic reference |

## Tests and prerequisites

A complete solution test run includes database integration and child-process tests. Run it only in an environment with the prerequisites below:

```sh
dotnet test IxIFlow.sln -c Release
```

`IxIFlow.Tests` provider/integration cases and SQL Server/PostgreSQL profiles in `IxIFlow.Conformance` need those database servers. The shared Testcontainers fixtures start Docker containers on first use unless you provide an external server through `IXIFLOW_TEST_SQL_CONNECTION_STRING` and `IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING`, respectively. The supplied server credentials must allow creation and deletion of isolated test databases. Use dedicated test servers. Docker is unnecessary when both external servers are provided.

Conformance accepts comma-separated `IXIFLOW_PROFILES`: `local-memory`, `local-sqlite`, `distributed-sqlserver`, `distributed-postgresql`, and each distributed profile with `/coordinators-1` or `/coordinators-2`. Unset selects all profiles. `unprofiled` additionally selects facts and plain-provider rows, which can themselves need database servers. Profile selection does not filter the separate `IxIFlow.Tests` assembly; several server facts are selected by class/method rather than a database category. For runs without servers, select local classes explicitly and exclude all server cases rather than relying only on category filters.

The following classes start `dotnet` pack/restore/build or consumer processes. They require an environment that permits child processes, package feeds and SDK access, and are unsuitable for restricted runners. Exclude them when those prerequisites are unavailable:

- `NuGetFeedRestoreProcessTests` (Conformance).
- `ActivityPublishingConsumerProcessTests`, `WorkflowPublishingConfiguredReferenceTests`, `WorkflowPublishingMetadataReferenceTests`, `WorkflowPublishingReferenceRegressionTests`, `WorkflowPublishingTypeSyntaxTests` (engine tests).
- `PackageConsumerTests`, `ActivitySchemaPackageTests`, `WorkflowSchemaPackageTests` (generator tests).

For a local documentation/compiler check in PowerShell:

```powershell
$env:IXIFLOW_PROFILES = 'local-memory,local-sqlite,unprofiled'
dotnet test tests/IxIFlow.Samples.Tests -c Release
dotnet test tests/IxIFlow.Analyzers.Tests -c Release
dotnet test tests/IxIFlow.Generators.Tests -c Release --filter 'FullyQualifiedName!~PackageConsumerTests&FullyQualifiedName!~ActivitySchemaPackageTests&FullyQualifiedName!~WorkflowSchemaPackageTests'
dotnet test tests/IxIFlow.Tests -c Release --filter 'FullyQualifiedName~SpecTraceabilityTests|FullyQualifiedName~ExecutorBoundaryGuardTests|FullyQualifiedName~FeatureBaselineGuardTests|FullyQualifiedName~PackageMetadataGuardTests'
dotnet test tests/IxIFlow.ExecutionContract.Tests -c Release
```

The optional specification traceability guard skips when its separately supplied input is absent; public builds and other suites need no such input. Documentation tests check links and compile-backed snippets. When formatting moves a snippet, update the guide from its source region. Native fixtures have platform restrictions documented in [their README](tests/IxIFlow.TestSupport/NativeFixtures/README.md).

Run the extension tests from `extensions/ixiflow-debug` with `npm test`. Its [README](extensions/ixiflow-debug/README.md) explains Extension Development Host setup.

## Formatting and review

Follow [.editorconfig](.editorconfig). Use four-space C# indentation, one statement per line, expanded blocks and accessors with bodies, compact auto-properties, a 140-column line limit and one blank line between logical paragraphs. Keep public XML documentation accurate and comments self-contained. Use file-scoped namespaces and descriptive names consistent with the surrounding code.

Restore the pinned tools, then format with the repository command:

```sh
dotnet tool restore
dotnet regitlint -s IxIFlow.sln --format-only
```

An optional pre-commit hook can check staged changes with:

```sh
dotnet regitlint -f staged --fail-on-diff
```

Review the diff, build in Release and run relevant tests after formatting. Describe the concrete problem, resulting behavior and validation in the pull request. Report unavailable test prerequisites and unresolved failures explicitly.

## License and DCO sign-off

Contributions are licensed under the [Apache License 2.0](LICENSE). Sign off every commit under the [Developer Certificate of Origin 1.1](https://developercertificate.org/), certifying that you have the right to submit it under this license. `git commit -s` adds:

```text
Signed-off-by: Your Name <your.email@example.com>
```

Use your name and email. Add missing sign-offs on your own branch before requesting merge, and coordinate any rewritten shared history with collaborators.

## Security reports

Do not report security vulnerabilities in public issues. Use GitHub's private vulnerability reporting on this repository (Security > Report a vulnerability); see [SECURITY.md](SECURITY.md). Keep exploit details and sensitive data out of public issues, pull requests and discussions.

## Release process

There is no CI configuration in this repository, and package publication is manual. Maintainers choose the semantic version, update package metadata and release notes, and validate a clean release checkout with the full build and applicable local, database, coordinator and process-consumer suites. Record any platform restrictions.

Pack the packable source projects and the DemoActivities sample with `dotnet pack -c Release`. Inspect package dependencies, READMEs, licenses/NOTICE, XML documentation, symbols and SourceLink, and verify restore/build from the resulting package feed. The engine embeds its compiler tooling; Generators also ships as a library package, while the analyzer/code-fix projects and runnable samples are non-packable. Build release symbols from the committed release revision. Maintainers publish the reviewed NuGet packages and symbols using their own credentials, create the release tag and publish release notes. No repository automation performs these steps.
