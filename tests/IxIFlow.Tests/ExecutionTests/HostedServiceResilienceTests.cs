using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using IxIFlow.Authoring;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class HostedServiceResilienceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CoordinatorHeartbeatLogsAnUnrelatedCancellationAndContinues()
    {
        var store = new Mock<IWorkflowStateRepository>();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        store.Setup(item =>
                item.HeartbeatAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new OperationCanceledException("store timeout");

                continued.TrySetResult();

                return Task.FromResult(true);
            });

        var options = new CoordinatorOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(10) };
        var logger = new RecordingLogger<CoordinatorService>();

        using var service = new CoordinatorService(options, new CoordinatorIdentity(),
            new CoordinatorMatcher(options, TimeProvider.System), store.Object, TimeProvider.System, logger);

        using var stopping = new CancellationTokenSource();
        var loop = ExecuteLoop(service, stopping.Token);

        try
        {
            await continued.Task.WaitAsync(Patience);

            Assert.Contains(logger.Entries,
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains("heartbeat coordinator", StringComparison.Ordinal));

            Assert.False(loop.IsCompleted);
        }
        finally
        {
            await stopping.CancelAsync();
            await loop.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task CoordinatorDiscoveryLogsAnUnrelatedCancellationAndContinues()
    {
        var store = new Mock<IWorkflowStateRepository>();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        store.Setup(item => item.GetLiveMembersAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new OperationCanceledException("store timeout");

            continued.TrySetResult();

            return Task.FromResult<IReadOnlyList<StoreMember>>([]);
        });

        var services = new ServiceCollection();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<CoordinatorLinks>();

        using var service = new CoordinatorLinks(new CoordinatorClientOptions { DiscoveryInterval = TimeSpan.FromMilliseconds(10) },
            store.Object, provider.GetRequiredService<WorkflowHost>(), HostSettings.Default, new WorkflowCatalog(),
            new CoordinationState(), TimeProvider.System, logger);

        using var stopping = new CancellationTokenSource();
        var loop = ExecuteLoop(service, stopping.Token);

        try
        {
            await continued.Task.WaitAsync(Patience);

            Assert.Contains(logger.Entries,
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains("refresh coordinator links", StringComparison.Ordinal));

            Assert.False(loop.IsCompleted);
        }
        finally
        {
            await stopping.CancelAsync();
            await loop.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task RecoveryLogsAnUnrelatedCancellationAndRunsAnotherPass()
    {
        var store = new Mock<IWorkflowStateRepository>();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        store.Setup(item => item.GetRunnableInstancesAsync(It.IsAny<ClaimQuery>(), cancellationToken: Moq.It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new OperationCanceledException("store timeout");

                continued.TrySetResult();

                return Task.FromResult<IReadOnlyList<RunnableInstance>>([]);
            });

        store.Setup(item => item.GetLiveMembersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var provider = new ServiceCollection().AddSingleton<IWorkflowCatalog>(new WorkflowCatalog()).BuildServiceProvider();
        var logger = new RecordingLogger<WorkflowRecoveryService>();

        using var service = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store.Object,
            logger, settings: HostSettings.Default with { PollInterval = TimeSpan.FromMilliseconds(10) });

        await service.StartAsync(CancellationToken.None);

        try
        {
            await continued.Task.WaitAsync(Patience);

            Assert.Contains(logger.Entries,
                entry => entry.Level == LogLevel.Error && entry.Message.Contains("scan running workflows", StringComparison.Ordinal));

            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        }

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // Exercise the coordinator loops without starting an unrelated nested Kestrel host.
    private static Task ExecuteLoop(BackgroundService service, CancellationToken token) =>
        (Task)service.GetType().GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [token])!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageIndexingLogsFailureAndRunsAnotherPass(bool unrelatedCancellation)
    {
        Exception failure = unrelatedCancellation ? new OperationCanceledException("store timeout") : new IOException("store unavailable");
        var catalog = new Mock<IWorkflowCatalogStore>();
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        catalog.Setup(store =>
                store.GetPackageExtractionPageAsync(PackageExtraction.Pending, null, 64,
                    cancellationToken: Moq.It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw failure;

                continued.TrySetResult();

                return Task.FromResult<IReadOnlyList<WorkflowPackage>>([]);
            });

        var logger = new RecordingLogger<PackageIndexingService>();

        using var service = new PackageIndexingService(catalog.Object, Mock.Of<IArtifactStore>(), logger: logger)
            { Interval = TimeSpan.FromMilliseconds(10) };

        await service.StartAsync(CancellationToken.None);

        try
        {
            await continued.Task.WaitAsync(Patience);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, failure));
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        }

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DebugPipeLogsFailureAndAcceptsAfterTheFailedPipeIsReleased()
    {
        var name = "ixiflow-resilience-" + Guid.NewGuid().ToString("N");

        // A one-instance server makes creation of the service's next server fail in the OS.
        using var occupied = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var logger = new RecordingLogger<DebugAdapterService>();
        var endpoint = new DebugEndpoint(name, null, 0, "pipe-user", null, null, (_, _) => ValueTask.FromResult(true));
        using var service = new DebugAdapterService(Mock.Of<IWorkflowOperations>(), endpoint, logger);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await logger.Warning.Task.WaitAsync(Patience);
            Assert.Contains(logger.Entries, entry => entry.Error is IOException);
            occupied.Dispose();
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(Patience);
            await client.ConnectAsync(timeout.Token);
            await InitializeAsync(client, timeout.Token);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        }

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DebugTcpLogsListenerFailureAndAcceptsAfterThePortIsReleased()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var logger = new RecordingLogger<DebugAdapterService>();

        var endpoint = new DebugEndpoint(null, IPAddress.Loopback, port, "tcp-user", [], "token",
            (_, _) => ValueTask.FromResult(true));

        using var service = new DebugAdapterService(Mock.Of<IWorkflowOperations>(), endpoint, logger);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await logger.Warning.Task.WaitAsync(Patience);
            Assert.Contains(logger.Entries, entry => entry.Error is SocketException);
            occupied.Stop();
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(Patience);

            // ConnectAsync can complete before the next accept; the socket backlog retains it.
            await ConnectAsync(client, port, timeout.Token);
            await InitializeAsync(client.GetStream(), timeout.Token);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        }

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static async Task ConnectAsync(TcpClient client, int port, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, token);

                return;
            }
            catch (SocketException)
            {
                await Task.Delay(20, token);
            }
        }
    }

    private static async Task InitializeAsync(Stream stream, CancellationToken token)
    {
        const string request = "{\"seq\":1,\"type\":\"request\",\"command\":\"initialize\",\"arguments\":{}}";
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(request)}\r\n\r\n{request}");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
        var received = new StringBuilder();
        var buffer = new byte[4096];

        while (!received.ToString().Contains("\"success\":true", StringComparison.Ordinal))
        {
            var length = await stream.ReadAsync(buffer, token);
            Assert.NotEqual(0, length);
            received.Append(Encoding.UTF8.GetString(buffer, 0, length));
        }

        Assert.Contains("initialize", received.ToString());
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();
        internal TaskCompletionSource Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, exception, formatter(state, exception)));

            if (logLevel >= LogLevel.Warning)
                Warning.TrySetResult();
        }
    }
}