using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Moq;

namespace IxIFlow.Tests.TemplateTests;

[Trait("Feature", "templates"), Trait("Spec", "F-16"), Trait("Spec", "F-17")]
public sealed class TemplateDispatchTests
{
    public sealed class Model
    {
        public string Value { get; set; } = "Ana";
    }

    private static bool Authored(WorkflowEngineException error) => (bool)typeof(WorkflowEngineException).GetProperty("RaisedByWorkflowCode",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(error)!;

    private static readonly TemplateReference Alpha = new("alpha", 1), Beta = new("beta", 1);

    private static TemplateVersion Version(string key) =>
        TemplateVersion.Create<Model>(key, 1, "liquid", TemplateFormat.Text, new TemplateVariant("", key + " {{ Value }}"));

    private static WorkflowDefinition Definition() => new()
    {
        BundleDigest = ArtifactDigest.Of([1]), Templates = new([Version("alpha"), Version("beta")]),
        TemplateBindings = [new("a", [Alpha]), new("b", [Beta])]
    };

    [Fact]
    public async Task ParallelAndNestedFramesRestoreTheirOwnBindingsAndNeverReadTheCatalog()
    {
        var store = new Mock<IWorkflowCatalogStore>(MockBehavior.Strict);
        var renderer = new TemplateRenderer(store.Object, new([new LiquidTemplateEngine()]));
        var definition = Definition();
        using var lease = new WorkflowDefinitionLease(definition);
        using var scope = lease.EnterTypes();
        var missing = await Assert.ThrowsAsync<WorkflowEngineException>(() => renderer.RenderAsync(Alpha, new Model(), null));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, missing.Kind);
        Assert.False(Authored(missing));

        await Task.WhenAll(Task.Run(async () =>
        {
            using var a = WorkflowDefinitionScope.Dispatch(definition, "a");
            await Task.Yield();
            Assert.Equal("alpha Ana", (await renderer.RenderAsync(Alpha, new Model(), null)).Content);

            using (WorkflowDefinitionScope.Dispatch(definition, "b"))
                Assert.Equal("beta Ana", (await renderer.RenderAsync(Beta, new Model(), null)).Content);

            Assert.Equal("alpha Ana", (await renderer.RenderAsync(Alpha, new Model(), null)).Content);
        }), Task.Run(async () =>
        {
            using var b = WorkflowDefinitionScope.Dispatch(definition, "b");
            await Task.Yield();
            Assert.Equal("beta Ana", (await renderer.RenderAsync(Beta, new Model(), null)).Content);
            await Assert.ThrowsAsync<WorkflowEngineException>(() => renderer.RenderAsync(Alpha, new Model(), null));
            var failure = Assert.Throws<WorkflowEngineException>(b.ThrowIfFailed);
            Assert.False(Authored(failure));
        }));

        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CodeDefinedScopeUsesCatalogAndReturnsToTheEnclosingBundle()
    {
        var version = Version("alpha");
        var store = new Mock<IWorkflowCatalogStore>(MockBehavior.Strict);

        store.Setup(item => item.GetTemplateVersionAsync("alpha", 1, cancellationToken: Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateVersionEntry(version, null, DateTime.UtcNow, null));

        var renderer = new TemplateRenderer(store.Object, new([new LiquidTemplateEngine()]));
        var definition = Definition();
        using var bundle = WorkflowDefinitionScope.Enter(definition);

        using (WorkflowDefinitionScope.Enter(new()))
            Assert.Equal("alpha Ana", (await renderer.RenderAsync(Alpha, new Model(), null)).Content);

        using (WorkflowDefinitionScope.Dispatch(definition, "b"))
            await Assert.ThrowsAsync<WorkflowEngineException>(() => renderer.RenderAsync(Alpha, new Model(), null));

        store.Verify(item => item.GetTemplateVersionAsync("alpha", 1, cancellationToken: Moq.It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WrongDeclaredModelAndNullModelAreTrustedDispatchViolations()
    {
        var renderer = new TemplateRenderer(new Mock<IWorkflowCatalogStore>(MockBehavior.Strict).Object, new([new LiquidTemplateEngine()]));

        foreach (var nullModel in new[] { false, true })
        {
            using var dispatch = WorkflowDefinitionScope.Dispatch(Definition(), "a");

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                renderer.RenderAsync<object>(Alpha, nullModel ? null! : new Model(), null));

            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.False(Authored(error));
            Assert.Throws<WorkflowEngineException>(dispatch.ThrowIfFailed);
        }
    }
}