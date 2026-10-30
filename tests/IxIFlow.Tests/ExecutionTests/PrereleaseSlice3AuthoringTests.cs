using System.Security.Claims;
using System.Reflection;
using System.Text;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class PrereleaseSlice3AuthoringTests
{
    [Fact]
    public async Task UploadEndpointDenialPrecedesAdmissionAndAnyBodyRead()
    {
        var authorization = new Mock<IAuthorizationService>();

        authorization.Setup(item => item.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object>(), "publish"))
            .ReturnsAsync(AuthorizationResult.Failed());

        await using var services = new ServiceCollection().AddSingleton(authorization.Object).BuildServiceProvider();
        var store = new InMemoryWorkflowStateRepository();

        using var uploads = new WorkflowPackageUploadService(new(store, store), new(), new() { PublishPolicy = "publish" },
            NullLogger<WorkflowPackageUploadService>.Instance);

        var admission =
            (SemaphoreSlim)typeof(WorkflowPackageUploadService).GetField("_admission", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(uploads)!;

        await admission.WaitAsync();
        await admission.WaitAsync();

        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader")], "test"))
        };

        using var body = new UnreadableStream();
        http.Request.Body = body;
        var result = await uploads.UploadAsync(http, "/catalog/packages");
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, body.Reads);
        authorization.Verify(item => item.AuthorizeAsync(http.User, http, "publish"), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperationsBodyReaderBoundsKnownAndChunkedBodies(bool contentLength)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"note\":\"" + new string('x', 1000) + "\"}"));

        if (contentLength)
            http.Request.ContentLength = http.Request.Body.Length;

        var method = typeof(WorkflowOperationsExtensions).GetMethod("BodyAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Dictionary<string, string>));

        var read = (Task)method.Invoke(null, [http, 64])!;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => read);
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);

        if (contentLength)
            Assert.Equal(0, http.Request.Body.Position);
    }

    [Fact]
    public async Task PolicyMappingDefersAsyncProviderLookupsUntilStartupValidation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });

        builder.Services.AddIxIFlow().AddWorkflowPublishing(new()
        {
            ReadPolicy = "policy", AuthorPolicy = "policy", ExecutePolicy = "policy", PreviewPolicy = "policy",
            PublishPolicy = "policy", LifecyclePolicy = "policy", DownloadPolicy = "policy"
        });

        builder.Services.AddAuthorization();
        var policy = new Mock<IAuthorizationPolicyProvider>();
        var pending = new TaskCompletionSource<AuthorizationPolicy?>(TaskCreationOptions.RunContinuationsAsynchronously);
        policy.Setup(item => item.GetPolicyAsync("policy")).Returns(pending.Task);
        builder.Services.AddSingleton(policy.Object);
        await using var app = builder.Build();
        var missingScope = Assert.Throws<WorkflowEngineException>(() => app.MapWorkflowPublishing("/publishing"));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, missingScope.Kind);
        Assert.Contains("ICatalogQueryScope", missingScope.Message);
        policy.Verify(item => item.GetPolicyAsync("policy"), Times.Never);
        var shared = typeof(WorkflowOperationsExtensions).Assembly.GetType("IxIFlow.AspNetCore.WorkflowHttp")!;
        var validate = shared.GetMethod("ValidatePolicy", BindingFlags.NonPublic | BindingFlags.Static)!;

        var mapping = Task.Run(() =>
        {
            validate.Invoke(null, [app, "policy"]);
        });

        try
        {
            await mapping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            pending.TrySetResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            await mapping;

            throw;
        }

        policy.Verify(item => item.GetPolicyAsync("policy"), Times.Never);
        var starting = ((IHostedLifecycleService)app.Services.GetRequiredService<WorkflowHost>()).StartedAsync(default);
        Assert.False(starting.IsCompleted);
        pending.SetResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
        policy.Verify(item => item.GetPolicyAsync("policy"), Times.Once);
    }

    [Fact]
    public async Task TriggerDefinitionsAreRegisteredByAsyncStartupValidation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddIxIFlow();
        builder.Services.AddAuthorization(options => options.AddPolicy("policy", policy => policy.RequireAuthenticatedUser()));
        var registry = new Mock<IWorkflowCatalog>();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Setup(item => item.RegisterWorkflowAsync(It.IsAny<WorkflowDefinition>())).Returns(registered.Task);
        builder.Services.AddSingleton(registry.Object);
        await using var app = builder.Build();
        var definition = new WorkflowBuilder<Data, object>([], "Slice3Trigger").WaitFor<Go>("go").Build();

        var mapping = Task.Run(() =>
        {
            app.MapWorkflowTriggers(triggers =>
            {
                triggers.Start<Data>("start", "/start", definition).FromBody<Data>((body, _) => body).Authorize("policy");
                triggers.Status().Authorize("policy").AuthorizeInstance("policy");
            });
        });

        try
        {
            await mapping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            registered.TrySetResult();
            await mapping;

            throw;
        }

        registry.Verify(item => item.RegisterWorkflowAsync(It.IsAny<WorkflowDefinition>()), Times.Never);
        var startup = ((IHostedLifecycleService)app.Services.GetRequiredService<WorkflowHost>()).StartedAsync(default);
        Assert.False(startup.IsCompleted);
        registered.SetResult();
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Verify(item => item.RegisterWorkflowAsync(definition), Times.Once);
    }

    [Fact]
    public void IndexingSettingsFlowFromPublisherOptions()
    {
        var options = new WorkflowPublisherOptions { PackageIndexingInterval = TimeSpan.FromSeconds(7), MaxPackagesPerIndexingPass = 17 };
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow().AddPublishing(options);
        using var provider = services.BuildServiceProvider();
        var indexing = provider.GetRequiredService<PackageIndexingService>();
        Assert.Equal(options.PackageIndexingInterval, indexing.Interval);
        Assert.Equal(17, indexing.MaxPackagesPerPass);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddIxIFlow().AddPublishing(new() { PackageIndexingInterval = TimeSpan.Zero }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddIxIFlow().AddPublishing(new() { MaxPackagesPerIndexingPass = 1001 }));
    }

    [Fact]
    public void MissingTemplateEngineHasItsOwnTypedError()
    {
        var error = Assert.Throws<TemplateEngineNotFoundException>(() => new TemplateEngines([]).Get("liquid"));
        Assert.Equal("liquid", error.EngineName);
    }

    [Fact]
    public async Task PublisherKeepsTheCuratedMissingEngineDiagnostic()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store, templateEngines: new([new LiquidTemplateEngine()]));

        var report = await publisher.ValidateAsync(new("missing", 1, "template",
            "template: {key: missing, version: 1, engine: absent, format: text, model: System.String}\ncontent: hello\n"));

        Assert.Equal("TPL001", Assert.Single(report.Diagnostics).Code);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void LiquidLimitsAreValidatedBeforeResolvingTheEngine(int steps, int recursion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplatesBuilder(new ServiceCollection()).AddLiquid(options =>
        {
            options.MaxSteps = steps;
            options.MaxRecursion = recursion;
        }));
    }

    [Fact]
    public void OutboxAndWebhookEntryPointsCheckTheirBuilder()
    {
        IIxIFlowBuilder builder = null!;
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.AddOutboxPublisher<Publisher>()).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.AddOutboxPublisher(_ => new Publisher())).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.ConfigureOutbox()).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.ReplayRetainedOutboxEvents("publisher")).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.UnregisterOutboxPublisher("publisher")).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => builder.AddWebhook(null!)).ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckpointVerificationUsesThePublicOptionAndTheDefaultPath(bool verify)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.VerifyCheckpointParts = verify);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "Slice3Parts").WaitFor<Go>("go").Build();
        var started = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(started.InstanceId, "go", new Go())).Status);
    }

    [Fact]
    public async Task PublicationOwnerWaitsForItsTaskWithoutWaitingForAPollingTick()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Slice3Publication", 1, "workflow", "invalid: ["), null);
        using var service = new PublicationService(store, new WorkflowPublisher(store, store), timeProvider: clock);
        var result = await service.PublishAsync(draft.Id, TimeSpan.FromHours(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowPublicationState.Failed, result.State);
    }

    [Fact]
    public async Task FeedAuthenticationFailureKeepsTheTransportException()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var config = Path.Combine(Path.GetTempPath(), "slice3-feed-" + Guid.NewGuid().ToString("N") + ".config");

        await File.WriteAllTextAsync(config,
            $"<configuration><packageSources><clear/><add key=\"broken\" value=\"http://127.0.0.1:{port}/v3/index.json\" allowInsecureConnections=\"true\"/></packageSources></configuration>");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = Serve();

        try
        {
            var store = new InMemoryWorkflowStateRepository();
            var resolver = new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config });

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                resolver.ResolveAsync(["missing@1.0.0"], PackageAcquisition.Scratch, [], stop.Token));

            Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
            Assert.NotNull(error.InnerException);
            Assert.Contains("broken", error.Message);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving;
        }

        async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    var one = new byte[1];
                    var header = new StringBuilder();

                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0)
                            break;

                        header.Append((char)one[0]);
                    }

                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (System.Net.Sockets.SocketException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    public sealed class Data
    {
    }

    public sealed class Go
    {
    }

    private sealed class Publisher : IOutboxPublisher
    {
        public string Name => "test";
        public IReadOnlyCollection<string> EventTypes => [];

        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext message, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxPublishResult.Delivered());
    }

    private sealed class UnreadableStream : MemoryStream
    {
        internal int Reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;

            throw new IOException("Body must not be read");
        }
    }
}