using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublishingSliceFixScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OutOfProfileConfiguredTypesRemainCuratedInDryRunAndPublication(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Configured", 1, "workflow",
            "# Authored header\nworkflow: {name: Configured, version: 1, dataType: System.Object}\ndefinitions: []"), null);
        var revision = await store.GetCatalogRevisionAsync();
        foreach (var collection in new[] { "Types", "WorkflowDataTypes", "Workflows" })
        foreach (var configured in new[] { "global::MissingPlugin.Data", "global::System.Collections.Generic.List<global::MissingPlugin.Data>" })
        {
            var source = new WorkflowSourceOptions();
            switch (collection)
            {
                case "Types": source.Types["Configured"] = configured; break;
                case "WorkflowDataTypes": source.WorkflowDataTypes["Child"] = configured; break;
                case "Workflows": source.Workflows["Child@1"] = new(configured, 1); break;
            }
            var publisher = new WorkflowPublisher(store, store, new() { SourceOptions = source });
            var validation = await publisher.ValidateAsync(draft);
            var publication = await publisher.PublishAsync(draft.Id);
            foreach (var diagnostics in new[] { validation.Diagnostics, publication.Diagnostics })
            {
                var error = Assert.Single(diagnostics);
                Assert.Equal("PUB003", error.Code);
                Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
                Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, error.Class);
                Assert.Contains("Configured type '" + configured + "'", error.Message);
                Assert.Contains("component 'global::MissingPlugin.Data'", error.Message);
                Assert.Contains("host reference profile", error.Message);
                Assert.Equal(2, error.Line);
            }
        }
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, draft.Version));
        Assert.Empty(await store.GetWorkflowVersionsAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ValidationKeepsAuthoredPositionsInDryRunAndPublication(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        foreach (var (declaration, code, path) in new[]
        {
            ("dataType: Missing", "WF003", "workflow.dataType"),
            ("dataType: System.Object, input: [Missing]", "WF005", "workflow.input[0]"),
            ("dataType: System.Object, output: [Missing]", "WF005", "workflow.output[0]")
        })
        {
            var yaml = "# Authored formatting must survive in validation diagnostics.\n\n" +
                "workflow: {name: Positions, version: 1, " + declaration + "}\ndefinitions: []";
            var draft = await store.SaveDefinitionAsync(new("Positions", 1, "workflow", yaml), null);
            var revision = await store.GetCatalogRevisionAsync();
            var expected = DocumentModel.GetLocation(WorkflowYamlSerializer.Read(yaml), path);
            var canonical = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(yaml)));
            Assert.NotEqual(expected.Line, DocumentModel.GetLocation(canonical, path).Line);
            var validation = await publisher.ValidateAsync(draft);
            var publication = await publisher.PublishAsync(draft.Id);
            foreach (var diagnostics in new[] { validation.Diagnostics, publication.Diagnostics })
            {
                var error = Assert.Single(diagnostics);
                Assert.Equal(code, error.Code);
                Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
                Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, error.Class);
                Assert.Equal((expected.Line, expected.Column), (error.Line, error.Column));
            }
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, draft.Version));
            Assert.Empty(await store.GetWorkflowVersionsAsync());
            await store.DeleteDefinitionAsync(draft.Id);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SharedCompatibilityDiagnosticsRemainCuratedInDryRunAndPublication(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        var packages = new[]
        {
            ("fixture.range", ActivityPackageFixture.Package("fixture.range", "1.0.0", new(),
                "<dependency id=\"IxIFlow.Abstractions\" version=\"[99.0.0]\"/>")),
            ("fixture.low-range", ActivityPackageFixture.Package("fixture.low-range", "1.0.0", new(),
                "<dependency id=\"IxIFlow.Abstractions\" version=\"[0.1.0,0.2.0)\"/>")),
            ("fixture.assembly-conflict", ActivityPackageFixture.SharedAssemblyConflictPackage())
        };
        foreach (var (id, bytes) in packages)
        {
            await publisher.UploadPackageAsync(new MemoryStream(bytes));
            var yaml = "workflow: {name: Conflict, version: 1, dataType: System.Object}\n\n" +
                "imports:\n  catalogs: [" + id + "@1.0.0]\ndefinitions: []";
            var draft = await store.SaveDefinitionAsync(new("Conflict", 1, "workflow", yaml), null);
            var revision = await store.GetCatalogRevisionAsync();
            var validation = await publisher.ValidateAsync(draft);
            var publication = await publisher.PublishAsync(draft.Id);
            foreach (var diagnostics in new[] { validation.Diagnostics, publication.Diagnostics })
            {
                var error = Assert.Single(diagnostics);
                Assert.Equal("PUB003", error.Code);
                Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
                Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, error.Class);
                Assert.Contains("shared assembly", error.Message);
                Assert.Contains("IxIFlow.Abstractions", error.Message);
                Assert.Contains(id == "fixture.low-range" ? "0.1.0" : "99.0.0", error.Message);
                Assert.Equal(WorkflowYamlSerializer.Read(yaml).Imports.Location.Line, error.Line);
            }
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, draft.Version));
            Assert.Empty(await store.GetWorkflowVersionsAsync());
            await store.DeleteDefinitionAsync(draft.Id);
        }
    }
}
