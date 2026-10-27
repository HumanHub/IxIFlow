using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Moq;

namespace IxIFlow.Tests.TemplateTests;

[Trait("Feature", "templates"), Trait("Spec", "F-16"), Trait("Spec", "F-17")]
public sealed class TemplateSettlementReviewTests
{
    private static bool IsAuthored(Exception error) => (bool)typeof(WorkflowEngineException)
        .GetProperty("RaisedByWorkflowCode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(error)!;
    public sealed class Model { public string Value { get; set; } = "Ana"; }
    private static WorkflowDefinition Definition() => new()
    {
        BundleDigest = ArtifactDigest.Of([1]),
        Templates = new([TemplateVersion.Create<Model>("alpha", 1, "liquid", TemplateFormat.Text, new TemplateVariant("", "Hello {{ Value }}"))]),
        TemplateBindings = [new("output", [new("alpha", 1)])]
    };

    [Theory, InlineData(false), InlineData(true)]
    public void ResumedAccessorViolationCreatesAFreshUnmarkedFailureEvenIfTheOriginalWasMarked(bool swallow)
    {
        var definition = Definition();
        WorkflowDefinitionScope dispatch;
        using (dispatch = WorkflowDefinitionScope.Dispatch(definition, "output")) { }
        using var enclosing = WorkflowDefinitionScope.Enter(definition);
        using var resumed = dispatch.Resume();
        var output = new Getter(new(new Mock<IWorkflowCatalogStore>(MockBehavior.Strict).Object, new([new LiquidTemplateEngine()])), swallow);
        var error = Assert.Throws<WorkflowEngineException>(() => ActivityPropertyAccess.GetValue(typeof(Getter).GetProperty(nameof(Getter.Value))!, output));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.False(IsAuthored(error));
        Assert.NotSame(output.Original, error);
        Assert.NotSame(error, Assert.Throws<WorkflowEngineException>(dispatch.ThrowIfFailed));
    }

    [Fact]
    public void AuthoredEngineExceptionWithoutAGuardViolationKeepsAccessorTreatment()
    {
        using var dispatch = WorkflowDefinitionScope.Dispatch(Definition(), "output");
        var error = Assert.Throws<WorkflowEngineException>(() => ActivityPropertyAccess.GetValue(typeof(Authored).GetProperty(nameof(Authored.Value))!, new Authored()));
        Assert.True(IsAuthored(error));
        dispatch.ThrowIfFailed();
    }

    private sealed class Getter(TemplateRenderer renderer, bool swallow)
    {
        public WorkflowEngineException? Original;
        public string Value
        {
            get
            {
                try { return renderer.RenderAsync(new("beta", 1), new Model(), null).GetAwaiter().GetResult().Content; }
                catch (WorkflowEngineException error)
                {
                    Original = error;
                    typeof(WorkflowEngineException).GetMethod("FromWorkflowCode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [error]);
                    if (!swallow) throw;
                    return "swallowed";
                }
            }
        }
    }
    private sealed class Authored
    {
        public string Value => throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "authored");
    }
}
