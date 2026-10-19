using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-15")]
[Trait("Feature", "durability")]
public sealed class IncomingWebhookScenarios
{
    private const string Secret = "whsec_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task StartIsDurableBeforeAcceptedWithLocationAndPinnedVersion(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var response = await Start(http);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = await Id(response);
        Assert.Equal("/workflows/" + id, response.Headers.Location!.OriginalString);
        var saved = await factory.Repository.GetWorkflowInstanceAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("HttpApproval", saved.WorkflowName);
        Assert.Equal(1, saved.WorkflowVersion);
        Assert.Equal("alice", (await factory.Services.GetRequiredService<IWorkflowIngressClient>().GetOwnershipAsync(id))!.Owner);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SynchronousReplyCompletesOrReturnsADurableStatusLocation(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var response = await Start(http, route: "/sync");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        var id = await Id(response);
        Assert.NotNull(await factory.Repository.GetWorkflowInstanceAsync(id));
        if (response.StatusCode == HttpStatusCode.Accepted) Assert.NotNull(response.Headers.Location);
        else Assert.DoesNotContain("Secret", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task IdempotentRepeatAfterALostResponseReturnsTheWinner(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var first = await Start(http, key: "lost-response");
        var id = await Id(first);
        using var retry = await Start(http, key: "lost-response");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(id, await Id(retry));
        Assert.Single(await Task.WhenAll((await factory.Repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "HttpApproval", Limit: int.MaxValue))).Items.Select(async row => (await factory.Repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ReusingAKeyWithDifferentInputIsAConflict(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var first = await Start(http, key: "conflict");
        using var conflict = await Start(http, key: "conflict", value: 9);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(await Task.WhenAll((await factory.Repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "HttpApproval", Limit: int.MaxValue))).Items.Select(async row => (await factory.Repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BadSignatureDoesNotMapOrAdmitAStart(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/signed") { Content = JsonContent.Create(new { value = 1 }) };
        request.Headers.Add("webhook-id", "bad");
        request.Headers.Add("webhook-timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        request.Headers.Add("webhook-signature", "v1,invalid");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await Task.WhenAll((await factory.Repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "HttpApproval", Limit: int.MaxValue))).Items.Select(async row => (await factory.Repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EventIsInTheDurableInboxBeforeAccepted(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var start = await Start(http);
        var id = await Id(start);
        await factory.Services.GetRequiredService<IWorkflowClient>().AwaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
        using var request = Event(id, "approval");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var eventId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { "urn:conformance", "approval-1" }))));
        Assert.NotNull(await factory.Repository.GetEventAsync(id, eventId));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EventToAnUnopenedKeyIsRefusedWithTheReason(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var http = factory.AuthenticatedClient();
        using var start = await Start(http);
        var id = await Id(start);
        await factory.Services.GetRequiredService<IWorkflowClient>().AwaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
        using var request = Event(id, "wrong");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("NoOpenWait", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AllEndpointsRequireAuthenticationAndStatusRequiresTheInstanceOwner(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new IngressFactory(store.NewRepository());
        using var alice = factory.AuthenticatedClient();
        using var anonymous = factory.CreateClient();
        using var start = await Start(alice);
        var id = await Id(start);
        using var unauthorizedStart = await Start(anonymous);
        using var unauthorizedStatus = await anonymous.GetAsync("/workflows/" + id);
        using var eventRequest = Event(id, "approval");
        using var unauthorizedEvent = await anonymous.SendAsync(eventRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedStart.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedEvent.StatusCode);
        using var bob = factory.AuthenticatedClient("bob");
        using var forbidden = await bob.GetAsync("/workflows/" + id);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task AStartOnOneHostCompletesThroughAnEventOnAnotherHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        await using var first = new IngressFactory(store.NewRepository(), registry);
        await using var second = new IngressFactory(store.NewRepository(), registry);
        using var firstHttp = first.AuthenticatedClient();
        using var secondHttp = second.AuthenticatedClient();
        using var start = await Start(firstHttp);
        var id = await Id(start);
        await first.Services.GetRequiredService<IWorkflowClient>().AwaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
        using var request = Event(id, "approval");
        using var response = await secondHttp.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await first.Services.GetRequiredService<IWorkflowClient>().AwaitAsync(id, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task TwoRepositoriesRaceForOneAdmissionAndCannotLeaveAnOrphanedKey(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var first = store.NewRepository();
        var second = store.NewRepository();
        var request = new WorkflowAdmission("sender", "trigger", "key", new string('A', 64), "owner");
        CheckpointCommit Commit() => CheckpointCommit.CreateAdmission(new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Race", WorkflowVersion = 1,
            Status = WorkflowStatus.Running, WorkflowDataType = typeof(Data).AssemblyQualifiedName!, WorkflowDataJson = "{}",
            ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint
            {
                StructureHash = "race", Continuations = [new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] }]
            })
        });
        var left = Commit();
        var right = Commit();
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(async () => { barrier.SignalAndWait(); return await ((IWorkflowAdmissionStore)first).AdmitWorkflowInstanceAsync(left, request); }),
            Task.Run(async () => { barrier.SignalAndWait(); return await ((IWorkflowAdmissionStore)second).AdmitWorkflowInstanceAsync(right, request); }));
        var winner = Assert.Single(results, r => r.Status == WorkflowCommitStatus.Applied).Admission!;
        Assert.All(results, result => Assert.Equal(winner, result.Admission));
        Assert.NotNull(await second.GetWorkflowInstanceAsync(winner.InstanceId));
        Assert.Single(await first.GetRunnableInstancesAsync(100));
        Assert.Null(await first.GetWorkflowInstanceAsync(winner.InstanceId == left.InstanceId ? right.InstanceId : left.InstanceId));
    }

    private static Task<HttpResponseMessage> Start(HttpClient http, string? key = null, int value = 1, string route = "/orders")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(new { value }) };
        if (key != null) request.Headers.Add("Idempotency-Key", key);
        return http.SendAsync(request);
    }

    private static HttpRequestMessage Event(string id, string key) => new(HttpMethod.Post, $"/workflows/{id}/events/{key}")
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            specversion = "1.0", source = "urn:conformance", id = "approval-1", type = "approved", data = new { approved = true }
        }), Encoding.UTF8, "application/cloudevents+json")
    };
    private static async Task<string> Id(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<WorkflowStatusResponse>())!.Id;

    public sealed class Data { public int Value { get; set; } public string Secret { get; set; } = "private"; }
    public sealed class Approval { public bool Approved { get; set; } }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Application { }

    private sealed class IngressFactory(IWorkflowStateRepository repository, IWorkflowVersionRegistry? registry = null)
        : WebApplicationFactory<Application>
    {
        public IWorkflowStateRepository Repository => repository;
        public HttpClient AuthenticatedClient(string user = "alice")
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-User", user);
            return client;
        }

        protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web => web.ConfigureServices(services =>
            {
                services.AddIxIFlow(options => options.PollInterval = TimeSpan.FromMilliseconds(50));
                services.Replace(ServiceDescriptor.Singleton(repository));
                services.Replace(ServiceDescriptor.Singleton(registry ?? new WorkflowVersionRegistry()));
                services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
                services.AddTransient<Noop>();
                services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("api", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("instance", policy => policy.RequireAssertion(context =>
                        context.Resource is WorkflowOwnership ownership && ownership.Owner == context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
                });
            }).Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapWorkflowTriggers(triggers =>
                {
                    var wait = Workflow.Create<Data>("HttpApproval").Step<Noop>().WaitFor<Approval>("approval").Build();
                    var complete = Workflow.Create<Data>("HttpComplete").Step<Noop>().Build();
                    triggers.Start<Data>("orders", "/orders", wait).FromBody<Data>((data, _) => data).Authorize("api");
                    triggers.Start<Data>("sync", "/sync", complete).FromBody<Data>((data, _) => data).Authorize("api")
                        .Synchronous(TimeSpan.FromSeconds(1)).ProjectResult(data => new { data.Value });
                    triggers.Start<Data>("signed", "/signed", wait).FromBody<Data>((data, _) => data)
                        .VerifyStandardWebhook("vendor", Secret).Authorize("api");
                    triggers.Events("events").Allow<Approval>("approved").Authorize("api").AuthorizeInstance("instance");
                    triggers.Status().Authorize("api").AuthorizeInstance("instance");
                }));
            }));

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory);
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(user) ? AuthenticateResult.NoResult() : AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")), "test")));
        }
    }
}
