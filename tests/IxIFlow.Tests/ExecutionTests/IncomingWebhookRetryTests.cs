using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-10")]
[Trait("Feature", "durability")]
public sealed class IncomingWebhookRetryTests
{
    private static readonly WorkflowDefinition Definition = Workflow.Create<Data>("RetryAdmission").Step<Noop>().Build();
    private static readonly WorkflowAdmission Admission = new("sender", "trigger", "key", new string('A', 64), "owner");
    public sealed class Data { }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CaptureLogger : ILogger<WorkflowIngressClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RetriesOnlyStoreClassifiedRetryResultsLogsThemAndKeepsTheExactCommit()
    {
        await using var services = Services();
        var store = new Mock<IWorkflowStateRepository>();
        var commits = new List<CheckpointCommit>();
        store.Setup(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission))
            .Returns<CheckpointCommit, WorkflowAdmission>((commit, _) =>
            {
                commits.Add(commit);
                return Task.FromResult(commits.Count < 3 ? new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0)
                    : new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1)
                    {
                        Admission = new("sender", "trigger", "key", Admission.Fingerprint, commit.InstanceId,
                            Definition.Name, Definition.Version, DateTime.UtcNow.AddDays(1), commit.Incarnation)
                    });
            });
        var log = new CaptureLogger();
        var ingress = new WorkflowIngressClient(store.Object, new WorkflowVersionRegistry(), services.GetRequiredService<WorkflowHost>(),
            ExecutionLimits.Default, TimeProvider.System, log);
        var result = await ingress.AdmitAsync(Definition, new Data(), Admission);
        Assert.Equal(3, commits.Count);
        Assert.All(commits, commit => Assert.Same(commits[0], commit));
        Assert.Equal(commits[0].InstanceId, result.InstanceId);
        Assert.Equal(2, log.Messages.Count);
        Assert.All(log.Messages, message => { Assert.Contains("Retry", message); Assert.Contains("trigger", message); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ArbitraryStoreExceptionsIncludingCancellationAndInfrastructureAreNeverRetried(int kind)
    {
        await using var services = Services();
        Exception error = kind switch
        {
            0 => new InvalidOperationException("bug"),
            1 => new OperationCanceledException(),
            2 => new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "unclassified infrastructure failure"),
            3 => new ArgumentException("invalid"),
            _ => new IOException("connection lost")
        };
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission)).ThrowsAsync(error);
        var log = new CaptureLogger();
        var ingress = new WorkflowIngressClient(store.Object, new WorkflowVersionRegistry(), services.GetRequiredService<WorkflowHost>(),
            ExecutionLimits.Default, TimeProvider.System, log);
        var thrown = await Record.ExceptionAsync(() => ingress.AdmitAsync(Definition, new Data(), Admission));
        Assert.Same(error, thrown);
        store.Verify(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission), Times.Once);
        Assert.Empty(log.Messages);
    }

    [Theory]
    [InlineData(WorkflowCommitStatus.Conflict)]
    [InlineData(WorkflowCommitStatus.StaleEpoch)]
    [InlineData(WorkflowCommitStatus.EventsChanged)]
    public async Task OtherCommitStatusesAreNeverRetried(WorkflowCommitStatus status)
    {
        await using var services = Services();
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission)).ReturnsAsync(new WorkflowCommitResult(status, 0));
        var ingress = new WorkflowIngressClient(store.Object, new WorkflowVersionRegistry(), services.GetRequiredService<WorkflowHost>(),
            ExecutionLimits.Default, TimeProvider.System);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ingress.AdmitAsync(Definition, new Data(), Admission));
        store.Verify(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission), Times.Once);
    }

    [Fact]
    public async Task CancellationAfterAStoreRetryDoesNotMakeAnotherAttempt()
    {
        await using var services = Services();
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission)).Returns(() =>
        {
            cancellation.Cancel();
            return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0));
        });
        var log = new CaptureLogger();
        var ingress = new WorkflowIngressClient(store.Object, new WorkflowVersionRegistry(), services.GetRequiredService<WorkflowHost>(),
            ExecutionLimits.Default, TimeProvider.System, log);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingress.AdmitAsync(Definition, new Data(), Admission, cancellation.Token));
        store.Verify(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission), Times.Once);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public async Task RetryExhaustionStopsAfterThreeAttempts()
    {
        await using var services = Services();
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission))
            .ReturnsAsync(new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0));
        var log = new CaptureLogger();
        var ingress = new WorkflowIngressClient(store.Object, new WorkflowVersionRegistry(), services.GetRequiredService<WorkflowHost>(),
            ExecutionLimits.Default, TimeProvider.System, log);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ingress.AdmitAsync(Definition, new Data(), Admission));
        store.Verify(s => s.AdmitWorkflowInstanceAsync(It.IsAny<CheckpointCommit>(), Admission), Times.Exactly(3));
        Assert.Equal(2, log.Messages.Count);
    }
}
