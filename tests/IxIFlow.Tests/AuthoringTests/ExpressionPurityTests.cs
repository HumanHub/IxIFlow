using System.Data;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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
        },
        { "IDbCommand.ExecuteScalar", () => Mapping(_ => Command.ExecuteScalar()!.ToString()!) },
        { "new StreamReader(path)", () => Mapping(_ => new StreamReader("settings.json").ReadToEnd()) },
        // Wait predicates and loop conditions are delegates; their code is read from its IL.
        {
            "DateTime.UtcNow", () => Workflow.Create<Data>("ImpureWaitPredicate")
                .Step<Takes>()
                .WaitFor<Go>("go", (_, _) => DateTime.UtcNow.Hour < 12)
                .Build()
        },
        {
            "Guid.NewGuid", () => Workflow.Create<Data>("ImpureLoopCondition")
                .Step<Takes>()
                .WhileDo(_ => Guid.NewGuid() == Guid.Empty, body => body.Step<Takes>())
                .Build()
        }
    };

    private static readonly IDbCommand Command = null!;

    [Theory]
    [MemberData(nameof(Impure))]
    public void AnImpureExpressionFailsTheBuild(string member, Func<WorkflowDefinition> build)
    {
        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => build());

        Assert.Contains($"uses {member}", error.Message);
        Assert.Contains("Step '", error.Message);
    }

    // Pure members of the same namespaces build, as does reading the data, a stream in memory, or the run's clock.
    [Fact]
    public void APureExpressionBuilds()
    {
        Mapping(ctx => Path.Combine(ctx.WorkflowData.Folder, "x") + DateTime.MinValue.Year + ctx.WorkflowData.When.Hour);
        Mapping(ctx => new StreamReader(new MemoryStream(ctx.WorkflowData.Bytes)).ReadToEnd());
        Workflow.Create<Data>("PureWaitPredicate")
            .Step<Takes>()
            .WaitFor<Go>("go", (go, ctx) => go.Hour > ctx.WorkflowData.When.Hour && ctx.UtcNow.Hour > 0)
            .Build();
    }

    // An expression reads time from the run's clock, the engine's TimeProvider, so tests control it.
    [Theory]
    [InlineData(8, "morning")]
    [InlineData(20, "evening")]
    public async Task AConditionReadsTheRunsClock(int hour, string expected)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, hour, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<Data>("ReadsTheRunsClock")
            .Step<Takes>()
            .If(ctx => ctx.UtcNow.Hour < 12,
                then => then.Step<Says>(step => step.Input(activity => activity.Word).From(_ => "morning")
                    .Output(activity => activity.Word).To(ctx => ctx.WorkflowData.Folder)),
                otherwise => otherwise.Step<Says>(step => step.Input(activity => activity.Word).From(_ => "evening")
                    .Output(activity => activity.Word).To(ctx => ctx.WorkflowData.Folder)))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(expected, Assert.IsType<Data>(result.WorkflowData).Folder);
    }

    private static WorkflowDefinition Mapping(System.Linq.Expressions.Expression<Func<WorkflowContext<Data>, string>> source) =>
        Workflow.Create<Data>("ImpureMapping")
            .Step<Takes>(step => step.Input(activity => activity.Text).From(source))
            .Build();

    public sealed class Data
    {
        public string Folder { get; set; } = "";
        public DateTime When { get; set; }
        public byte[] Bytes { get; set; } = [];
    }

    public sealed class Go
    {
        public int Hour { get; set; }
    }

    public sealed class Says : IAsyncActivity
    {
        public string Word { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Takes : IAsyncActivity
    {
        public string Text { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
