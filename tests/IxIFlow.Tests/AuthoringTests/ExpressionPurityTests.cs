using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.AuthoringTests;

/// <summary>
/// Conditions and mappings are evaluated again after a recovery, so an expression that reads the clock or the
/// environment, makes values that differ per run, waits, or does I/O fails the build, naming the step and the member
/// (D-07, D-42).
/// </summary>
[Trait("Feature", "authoring")]
[Trait("Spec", "A-05")]
public sealed class ExpressionPurityTests
{
    public static TheoryData<string, Func<WorkflowDefinition>> Impure => new()
    {
        { "DateTime.Now", () => Mapping(_ => DateTime.Now.ToString("O")) },
        { "DateTimeOffset.UtcNow", () => Mapping(_ => DateTimeOffset.UtcNow.ToString("O")) },
        { "Guid.NewGuid", () => Mapping(_ => Guid.NewGuid().ToString()) },
        { "Environment.MachineName", () => Mapping(_ => Environment.MachineName) },
        { "Random.Next", () => Mapping(_ => new Random().Next().ToString()) },
        { "new HttpClient(...)", () => Mapping(_ => new HttpClient() == null ? "" : "client") },
        { "File.ReadAllText", () => Mapping(_ => File.ReadAllText("settings.json")) },
        { "TimeProvider.System", () => Mapping(_ => TimeProvider.System.GetUtcNow().ToString("O")) },
        {
            "DateTime.UtcNow", () => Workflow.Create<Data>("ImpureCondition")
                .Step<Takes>()
                .If(_ => DateTime.UtcNow.Hour < 12, then => then.Step<Takes>())
                .Build()
        }
    };

    [Theory]
    [MemberData(nameof(Impure))]
    public void AnImpureExpressionFailsTheBuild(string member, Func<WorkflowDefinition> build)
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => build());

        Assert.Contains($"uses {member}", error.Message);
        Assert.Contains("Step '", error.Message);
    }

    // Pure members of the same namespaces build, as does reading the data.
    [Fact]
    public void APureExpressionBuilds()
    {
        Mapping(ctx => Path.Combine(ctx.WorkflowData.Folder, "x") + DateTime.MinValue.Year + ctx.WorkflowData.When.Hour);
    }

    private static WorkflowDefinition Mapping(System.Linq.Expressions.Expression<Func<WorkflowContext<Data>, string>> source) =>
        Workflow.Create<Data>("ImpureMapping")
            .Step<Takes>(step => step.Input(activity => activity.Text).From(source))
            .Build();

    public sealed class Data
    {
        public string Folder { get; set; } = "";
        public DateTime When { get; set; }
    }

    public sealed class Takes : IAsyncActivity
    {
        public string Text { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
