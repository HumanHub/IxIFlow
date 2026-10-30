using System.Data;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Grpc.Core;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class PrereleaseSlice3LifetimeTests
{
    [Fact]
    public async Task SqliteSynchronousProviderDisposalReleasesFileOwnership()
    {
        var file = Path.Combine(Path.GetTempPath(), "slice3-" + Guid.NewGuid().ToString("N") + ".db");
        var connection = "Data Source=" + file;
        var ownedServices = new ServiceCollection();
        ownedServices.AddSingleton(_ => new SqliteWorkflowStateRepository(connection));

        using (var owned = ownedServices.BuildServiceProvider())
            await owned.GetRequiredService<SqliteWorkflowStateRepository>().GetCatalogRevisionAsync();

        using var reopened = new SqliteWorkflowStateRepository(connection);
        await reopened.GetCatalogRevisionAsync();
        await reopened.DisposeAsync();
        reopened.Dispose();
    }

    [Fact]
    public async Task SqliteGuidRowsDoNotNeedDappersGlobalGuidHandler()
    {
        var connection = "Data Source=" + Path.Combine(Path.GetTempPath(), "slice3-guid-" + Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteWorkflowStateRepository(connection);
        var draft = await store.SaveDefinitionAsync(new("GuidRows", 1, "workflow", "source"), null);
        var attempt = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(1));

        var instance = new WorkflowInstance
        {
            InstanceId = "guid-search", Status = WorkflowStatus.Running,
            ExecutionStateJson = IxIFlow.Tests.Infrastructure.StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
        };

        await IxIFlow.Tests.Infrastructure.StoreTestSupport.SeedAsync(store, instance);
        Assert.Equal(instance.Incarnation, Assert.Single((await store.QueryInstancesAsync(new InstanceQuery())).Items).Incarnation);
        Assert.Equal(draft.Generation, (await store.GetDefinitionAsync("workflow", draft.Name, 1))!.Generation);
        Assert.Equal(attempt.Id, (await store.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted))!.Id);

        Assert.Equal(attempt.Id,
            (await store.GetLatestPublicationAsync("workflow", draft.Name, 1, draft.Generation, CatalogScope.Unrestricted))!.Id);

        var admittedInstance = new WorkflowInstance
        {
            InstanceId = "guid-admitted", WorkflowName = "GuidAdmission", Status = WorkflowStatus.Running,
            ExecutionStateJson = IxIFlow.Tests.Infrastructure.StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
        };

        var admission = new WorkflowAdmission("sender", "trigger", "key", new string('A', 64), "owner");

        Assert.Equal(WorkflowCommitStatus.Applied,
            (await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(admittedInstance), admission)).Status);

        Assert.Equal(admittedInstance.Incarnation, (await store.GetWorkflowAdmissionAsync("sender", "trigger", "key"))!.Incarnation);
        Assert.Equal(admittedInstance.Incarnation, (await store.GetWorkflowOwnershipAsync(admittedInstance.InstanceId))!.Incarnation);

        // A provider must not install a global Guid handler when its type initializes.
        Assert.Null(typeof(SqliteWorkflowStateRepository).TypeInitializer);
    }

    [Fact]
    public void DisplacingALivePeerWarnsAndAbortsOnlyTheOldConnection()
    {
        var logger = new RecordingLogger<CoordinatorMatcher>();
        var matcher = new CoordinatorMatcher(new(), TimeProvider.System, logger);
        var hello = Hello();
        var aborted = 0;
        var old = matcher.Connect(hello, _ => true, () => aborted++);
        var current = matcher.Connect(hello, _ => true, () => throw new Exception("current peer was aborted"));
        matcher.Disconnect(old);
        Assert.Equal(1, aborted);

        Assert.Single(logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(hello.HostId.ToString(), StringComparison.Ordinal));

        matcher.Disconnect(current);
    }

    [Fact]
    public void PeerRegisteredWhileTheWriterStopsIsRemovedAfterTheReaderDrains()
    {
        using var cancellation = new CancellationTokenSource();
        var options = new CoordinatorOptions { MaxConnections = 1, LinkHeartbeatInterval = TimeSpan.FromMinutes(1) };
        using var pump = new LinkPump();

        var logger = new RecordingLogger<CoordinatorMatcher>
        {
            OnLog = () =>
            {
                cancellation.Cancel();

                // Run the writer's cancellation and then the outer Link continuation while
                // Connect has not returned. This is the precise registration/teardown gap.
                pump.UntilOuterLinkContinuation();
            }
        };

        var matcher = new CoordinatorMatcher(options, TimeProvider.System, logger);
        var hello = Hello();

        matcher.Connect(hello, _ => true, () =>
        {
        });

        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var input = new DelayedReader(new HostMessage(HostMessageKind.Hello, hello), first.Task,
            new(TaskCreationOptions.RunContinuationsAsynchronously));

        var context = new Mock<ServerCallContext>();
        context.Protected().SetupGet<CancellationToken>("CancellationTokenCore").Returns(cancellation.Token);
        var service = new CoordinatorRpcService(matcher, options, new(), TimeProvider.System);

        pump.Run(() =>
        {
            var link = service.Link(input, new FailedWriter(), context.Object);
            first.SetResult(true);
            pump.Until(() => link.IsCompleted);
            Assert.ThrowsAny<OperationCanceledException>(() => link.GetAwaiter().GetResult());
        });

        matcher.Disconnect(matcher.Connect(Hello(), _ => true, () =>
        {
        }));
    }

    [Fact]
    public async Task CoordinatorHasAnAsyncDisposalPathForItsNestedHost()
    {
        var options = new CoordinatorOptions { AllowInsecure = true, ListenEndpoint = new Uri("http://127.0.0.1:0") };

        var service = new CoordinatorService(options, new(), new(options, TimeProvider.System),
            new InMemoryWorkflowStateRepository(), TimeProvider.System, NullLogger<CoordinatorService>.Instance);

        await service.StartAsync(default);
        Assert.NotNull(service.Endpoint);
        await Assert.IsAssignableFrom<IAsyncDisposable>(service).DisposeAsync();

        // Repeated async and sync disposal must be harmless.
        await service.DisposeAsync();
        service.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedLeafIsRefusedBeforePinOrCaTrust(bool useCa)
    {
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Slice3Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Slice3Leaf", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16));

        var trust = new CoordinatorCertificateTrust();

        if (useCa)
            trust.CertificateAuthorities.Add(root);
        else
            trust.Thumbprints.Add(leaf.GetCertHashString(HashAlgorithmName.SHA256));

        Assert.True(CoordinatorSecurity.ValidateCertificate(leaf, null, trust, server: false));
        trust.RevokedThumbprints.Add(leaf.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());
        Assert.False(CoordinatorSecurity.ValidateCertificate(leaf, null, trust, server: false));
    }

    private static HostHello Hello() => new(Guid.NewGuid(), "host", new(), 1, LinkProtocol.Version);

    private sealed class LinkPump : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly AutoResetEvent _posted = new(false);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Enqueue((callback, state));
            _posted.Set();
        }

        internal void Run(Action action)
        {
            var previous = Current;
            SetSynchronizationContext(this);

            try
            {
                action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        internal void Until(Func<bool> complete)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (!complete())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Coordinator continuation was not posted");

                if (_queue.TryDequeue(out var item))
                    item.Callback(item.State);
                else
                    _posted.WaitOne(10);
            }
        }

        internal void UntilOuterLinkContinuation()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (!_queue.TryDequeue(out var item))
                {
                    _posted.WaitOne(10);

                    continue;
                }

                // The await infrastructure posts either an Action or the state-machine box.
                var target = item.State is Action action ? action.Target : item.State;
                var outer = target?.GetType().FullName?.Contains("+<Link>d__", StringComparison.Ordinal) == true;
                item.Callback(item.State);

                if (outer)
                    return;
            }

            throw new TimeoutException("Outer coordinator continuation was not posted");
        }

        public void Dispose() => _posted.Dispose();
    }

    private sealed class DelayedReader(HostMessage message, Task<bool> first, TaskCompletionSource stopped)
        : IAsyncStreamReader<HostMessage>
    {
        private int _calls;
        public HostMessage Current => message;

        public Task<bool> MoveNext(CancellationToken token)
        {
            token.Register(() => stopped.TrySetResult());

            return Interlocked.Increment(ref _calls) == 1 ? first : Task.FromCanceled<bool>(token);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FailedWriter : IServerStreamWriter<CoordinatorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(CoordinatorMessage message) => Task.FromException(new IOException("writer failed"));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];
        internal Action? OnLog { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((level, formatter(state, error)));
            OnLog?.Invoke();
        }
    }
}