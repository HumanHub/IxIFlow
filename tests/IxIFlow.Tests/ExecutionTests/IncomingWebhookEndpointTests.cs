using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>Invokes the real endpoint delegates, filters, policies, host and store without a network socket.</summary>
[Trait("Spec", "D-10")]
[Trait("Feature", "durability")]
public sealed class IncomingWebhookEndpointTests
{
    public const string OldSecret = "whsec_AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    public const string NewSecret = "whsec_AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=";

    public sealed class Data
    {
        public int Value { get; set; }
        public string Secret { get; set; } = "internal-data";
        public string? Source { get; set; }
    }
    public sealed class Approval { public bool Approved { get; set; } }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Failing : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private-stack-and-error");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public IWorkflowStateRepository Store { get; }
        public IWorkflowClient Client => App.Services.GetRequiredService<IWorkflowClient>();
        public int Mappings;
        public TimeProvider Time { get; }
        public Harness(IWorkflowStateRepository? store = null, TimeProvider? time = null,
            IWorkflowVersionRegistry? registry = null, Action<WorkflowTriggerBuilder>? registrations = null)
        {
            Time = time ?? TimeProvider.System;
            Store = store ?? new InMemoryWorkflowStateRepository(Time);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.Services.AddIxIFlow();
            builder.Services.AddTransient<Noop>();
            builder.Services.AddTransient<Failing>();
            builder.Services.Replace(ServiceDescriptor.Singleton(Store));
            builder.Services.Replace(ServiceDescriptor.Singleton(Time));
            if (registry != null) builder.Services.Replace(ServiceDescriptor.Singleton(registry));
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("api", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy("vendor", policy => policy.RequireClaim("ixiflow:sender", "vendor"));
                options.AddPolicy("instance", policy => policy.RequireAssertion(context => context.Resource is WorkflowOwnership ownership &&
                    (ownership.Owner == context.User.FindFirstValue(ClaimTypes.NameIdentifier) ||
                     ownership.Tenant != null && ownership.Tenant == context.User.FindFirstValue("tenant"))));
            });
            App = builder.Build();
            App.MapWorkflowTriggers(registrations ?? Configure);
            App.UseRouting();
            App.UseEndpoints(_ => { });
        }

        private void Configure(WorkflowTriggerBuilder triggers)
        {
            var wait = Workflow.Create<Data>("IncomingWait").Step<Noop>().WaitFor<Approval>("approval").Build();
            var complete = Workflow.Create<Data>("IncomingComplete").Step<Noop>().Build();
            var fail = Workflow.Create<Data>("IncomingFail").Step<Failing>().Build();
            foreach (var (id, path, definition, sync) in new[]
            {
                ("orders", "/orders/{orderId}", wait, false),
                ("complete", "/complete/{orderId}", complete, true),
                ("slow", "/slow/{orderId}", wait, true),
                ("fail", "/fail/{orderId}", fail, true),
                ("signed", "/signed/{orderId}", wait, false)
            })
            {
                var start = triggers.Start<Data>(id, path, definition)
                    .FromBody<Data>((body, context) =>
                    {
                        Interlocked.Increment(ref Mappings);
                        body.Value += int.Parse(context.Route("orderId")!) + int.Parse(context.Query("delta") ?? "0");
                        body.Source = context.CloudEvent?.Source;
                        return body;
                    }).Authorize("api").ProjectResult(data => new { data.Value });
                if (sync) start.Synchronous(id == "slow" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2));
                if (id == "signed") start.VerifyStandardWebhook("vendor", NewSecret, OldSecret).Authorize("vendor");
            }
            triggers.Events("events").Allow<Approval>("approved").Authorize("api").AuthorizeInstance("instance");
            triggers.Events("signed-events", "/signed/workflows/{id}/events/{key}").Allow<Approval>("approved")
                .VerifyStandardWebhook("vendor", NewSecret, OldSecret).Authorize("vendor").AuthorizeInstance("instance");
            triggers.Status().Authorize("api").AuthorizeInstance("instance");
        }

        public async Task<Response> Send(string pattern, string body = "{}", string? user = "alice", string? key = null,
            string orderId = "1", string? instanceId = null, string eventKey = "approval", string query = "",
            string contentType = "application/json", string? secret = null, string deliveryId = "delivery",
            DateTimeOffset? timestamp = null, string? tenant = null, Dictionary<string, string>? headers = null)
        {
            var endpoint = ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == pattern);
            await using var scope = App.Services.CreateAsyncScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.SetEndpoint(endpoint);
            http.Request.Method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost");
            http.Request.Path = pattern.Replace("{orderId}", orderId).Replace("{id}", instanceId).Replace("{key}", eventKey);
            if (pattern.Contains("{orderId}")) http.Request.RouteValues["orderId"] = orderId;
            if (pattern.Contains("{id}")) http.Request.RouteValues["id"] = instanceId;
            if (pattern.Contains("{key}")) http.Request.RouteValues["key"] = eventKey;
            http.Request.QueryString = new QueryString(query);
            http.Request.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
            http.Response.Body = new MemoryStream();
            if (user != null)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
                if (tenant != null) claims.Add(new("tenant", tenant));
                http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            }
            if (key != null) http.Request.Headers["Idempotency-Key"] = key;
            if (secret != null)
            {
                var seconds = (timestamp ?? Time.GetUtcNow()).ToUnixTimeSeconds().ToString();
                http.Request.Headers["webhook-id"] = deliveryId;
                http.Request.Headers["webhook-timestamp"] = seconds;
                http.Request.Headers["webhook-signature"] = StandardWebhookSignature.Sign(StandardWebhookSignature.DecodeSecret(secret), deliveryId, seconds, bytes);
            }
            if (headers != null) foreach (var header in headers) http.Request.Headers[header.Key] = header.Value;
            await endpoint.RequestDelegate!(http);
            http.Response.Body.Position = 0;
            return new(http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync(), http.Response.Headers.Location.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await App.Services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }

    private sealed record Response(int Status, string Body, string Location)
    {
        public string Id => JsonDocument.Parse(Body).RootElement.GetProperty("id").GetString()!;
    }
    private static string Cloud(string id = "event", string source = "urn:test", string type = "approved", string data = "{\"approved\":true}") =>
        JsonSerializer.Serialize(new { specversion = "1.0", id, source, type, data = JsonSerializer.Deserialize<JsonElement>(data) });

    [Fact]
    public async Task StartRepeatLostResponseAndConflictAreDurableAndScoped()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        await using var firstHost = new Harness(repository, registry: registry);
        var first = await firstHost.Send("/orders/{orderId}", "{\"value\":2}", key: "once");
        Assert.Equal(202, first.Status);
        Assert.Equal("/workflows/" + first.Id, first.Location);
        Assert.NotNull(await repository.GetWorkflowInstanceAsync(first.Id));
        await using var secondHost = new Harness(repository, registry: registry);
        var retry = await secondHost.Send("/orders/{orderId}", "{\"value\":2}", key: "once");
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(409, (await secondHost.Send("/orders/{orderId}", "{\"value\":3}", key: "once")).Status);
        Assert.Equal(409, (await secondHost.Send("/orders/{orderId}", "{\"value\":2}", key: "once", orderId: "2")).Status);
        Assert.Equal(409, (await secondHost.Send("/orders/{orderId}", "{\"value\":2}", key: "once", query: "?delta=1")).Status);
        Assert.NotEqual(first.Id, (await secondHost.Send("/orders/{orderId}", "{\"value\":2}", key: "once", user: "bob")).Id);
        Assert.NotEqual(first.Id, (await secondHost.Send("/slow/{orderId}", "{\"value\":2}", key: "once")).Id);
        Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(first.Id))!.WorkflowVersion);
    }

    [Fact]
    public async Task KeysExpireOnTheStoreClockWithoutReusingTheOldInstance()
    {
        var clock = new FakeTimeProvider();
        await using var host = new Harness(time: clock);
        var first = await host.Send("/orders/{orderId}", key: "expires");
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(first.Id, (await host.Send("/orders/{orderId}", key: "expires")).Id);
        clock.Advance(TimeSpan.FromHours(1));
        var fresh = await host.Send("/orders/{orderId}", "{\"value\":4}", key: "expires");
        Assert.Equal(202, fresh.Status);
        Assert.NotEqual(first.Id, fresh.Id);
        Assert.NotNull(await host.Store.GetWorkflowInstanceAsync(first.Id));
    }

    [Fact]
    public async Task RenamingARouteAndDeployingANewVersionKeepsTheOriginalAdmissionPinned()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        await using var firstHost = new Harness(repository, registry: registry);
        var first = await firstHost.Send("/orders/{orderId}", key: "deployment");
        var nextVersion = Workflow.Create<Data>("IncomingWait", 2).Step<Noop>().WaitFor<Approval>("approval").Build();
        await using var secondHost = new Harness(repository, registry: registry, registrations: triggers =>
        {
            triggers.Start<Data>("orders", "/renamed/{orderId}", nextVersion)
                .FromBody<Data>((data, context) => { data.Value += int.Parse(context.Route("orderId")!); return data; })
                .Authorize("api");
            triggers.Status().Authorize("api").AuthorizeInstance("instance");
        });
        var retry = await secondHost.Send("/renamed/{orderId}", key: "deployment");
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(first.Id))!.WorkflowVersion);
        var fresh = await secondHost.Send("/renamed/{orderId}", key: "new-deployment");
        Assert.Equal(2, (await repository.GetWorkflowInstanceAsync(fresh.Id))!.WorkflowVersion);
    }

    [Fact]
    public async Task DeactivationRefusesNewStartsButStillAnswersAnAlreadyAdmittedRetry()
    {
        var registry = new WorkflowVersionRegistry();
        await using var host = new Harness(registry: registry);
        var first = await host.Send("/orders/{orderId}", key: "deactivate");
        await registry.DeactivateWorkflowVersionAsync("IncomingWait", 1);
        var retry = await host.Send("/orders/{orderId}", key: "deactivate");
        Assert.Equal(202, retry.Status);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(503, (await host.Send("/orders/{orderId}", key: "new-key")).Status);
    }

    [Fact]
    public async Task SignedBytesAreVerifiedBeforeMappingWithRotationToleranceAndSignedDeliveryDeduplication()
    {
        await using var host = new Harness();
        Assert.Equal(401, (await host.Send("/signed/{orderId}", "not-json", user: null)).Status);
        Assert.Equal(0, host.Mappings);
        var first = await host.Send("/signed/{orderId}", " {\"value\":1} ", user: null, secret: OldSecret);
        Assert.Equal(202, first.Status);
        var retry = await host.Send("/signed/{orderId}", " {\"value\":1} ", user: null, secret: NewSecret, key: "ignored-unsigned-key");
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(409, (await host.Send("/signed/{orderId}", "{\"value\":2}", user: null, secret: NewSecret)).Status);
        Assert.Equal(401, (await host.Send("/signed/{orderId}", user: null, secret: NewSecret,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-6))).Status);
        Assert.Equal(401, (await host.Send("/signed/{orderId}", user: null, secret: NewSecret,
            headers: new() { ["webhook-signature"] = "v1,wrong" })).Status);
        Assert.Equal(400, (await host.Send("/signed/{orderId}", "invalid-json", user: null, secret: NewSecret)).Status);
        Assert.Equal(401, (await host.Send("/signed/{orderId}", user: null, secret: NewSecret,
            headers: new() { ["webhook-timestamp"] = "invalid" })).Status);
    }

    [Fact]
    public async Task EveryEndpointRejectsUnauthenticatedCallersAndStatusChecksTheSavedOwnerOrTenant()
    {
        await using var host = new Harness();
        Assert.Equal(401, (await host.Send("/orders/{orderId}", user: null)).Status);
        Assert.Equal(401, (await host.Send("/workflows/{id}", user: null, instanceId: "unknown")).Status);
        Assert.Equal(401, (await host.Send("/workflows/{id}/events/{key}", user: null, instanceId: "unknown")).Status);
        var first = await host.Send("/orders/{orderId}", tenant: "tenant-a");
        Assert.Equal(403, (await host.Send("/workflows/{id}", user: "bob", instanceId: first.Id)).Status);
        Assert.Equal(403, (await host.Send("/workflows/{id}/events/{key}", Cloud(), user: "bob", instanceId: first.Id,
            contentType: "application/cloudevents+json")).Status);
        Assert.Equal(200, (await host.Send("/workflows/{id}", user: "bob", tenant: "tenant-a", instanceId: first.Id)).Status);
        await host.Store.DeleteWorkflowInstanceAsync(first.Id);
        Assert.Equal(410, (await host.Send("/workflows/{id}", instanceId: first.Id)).Status);
        Assert.Equal(403, (await host.Send("/workflows/{id}", user: "bob", instanceId: first.Id)).Status);
        Assert.Equal(404, (await host.Send("/workflows/{id}", instanceId: "unknown")).Status);
    }

    [Fact]
    public async Task SynchronousCompletionProjectsOnlyPublicDataAndTimeoutDoesNotCancelWork()
    {
        await using var host = new Harness();
        var complete = await host.Send("/complete/{orderId}", "{\"value\":5}");
        Assert.Equal(200, complete.Status);
        Assert.Contains("\"value\":6", complete.Body);
        Assert.DoesNotContain("internal-data", complete.Body);
        var retained = await host.Send("/workflows/{id}", instanceId: complete.Id);
        Assert.Equal(200, retained.Status);
        Assert.DoesNotContain("workflowData", retained.Body);
        var failed = await host.Send("/fail/{orderId}");
        Assert.Equal(200, failed.Status);
        Assert.Contains("Failed", failed.Body);
        Assert.DoesNotContain("private-stack", failed.Body);
        var timeout = await host.Send("/slow/{orderId}");
        Assert.Equal(202, timeout.Status);
        Assert.Equal(WorkflowExecutionStatus.Suspended, await host.Client.GetStatusAsync(timeout.Id));
        var accepted = await host.Send("/workflows/{id}/events/{key}", Cloud(), instanceId: timeout.Id,
            contentType: "application/cloudevents+json");
        Assert.Equal(202, accepted.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await host.Client.AwaitAsync(timeout.Id, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task StructuredAndBinaryEventsAreDurableAllowlistedAndIdentifiedBySourceAndId()
    {
        await using var host = new Harness();
        var first = await host.Send("/orders/{orderId}", Cloud(type: "order", data: "{\"value\":8}"), contentType: "application/cloudevents+json");
        await host.Client.AwaitAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("urn:test", JsonSerializer.Deserialize<Data>((await host.Store.GetWorkflowInstanceAsync(first.Id))!.WorkflowDataJson)!.Source);
        Assert.Equal(400, (await host.Send("/workflows/{id}/events/{key}", Cloud(type: "forbidden"), instanceId: first.Id,
            contentType: "application/cloudevents+json")).Status);
        var refused = await host.Send("/workflows/{id}/events/{key}", Cloud(), instanceId: first.Id, eventKey: "wrong",
            contentType: "application/cloudevents+json");
        Assert.Equal(409, refused.Status);
        Assert.Contains("NoOpenWait", refused.Body);
        // Same source/id at another key is a conflict, so a new delivery needs a new event identity.
        Assert.Equal(409, (await host.Send("/workflows/{id}/events/{key}", Cloud(), instanceId: first.Id,
            contentType: "application/cloudevents+json")).Status);
        var accepted = await host.Send("/workflows/{id}/events/{key}", "{\"approved\":true}", instanceId: first.Id,
            headers: new() { ["ce-specversion"] = "1.0", ["ce-source"] = "urn:binary", ["ce-id"] = "event", ["ce-type"] = "approved" });
        Assert.Equal(202, accepted.Status);
        var eventId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { "urn:binary", "event" }))));
        Assert.NotNull(await host.Store.GetEventAsync(first.Id, eventId));
        Assert.Equal(202, (await host.Send("/workflows/{id}/events/{key}", "{\"approved\":true}", instanceId: first.Id,
            headers: new() { ["ce-specversion"] = "1.0", ["ce-source"] = "urn:binary", ["ce-id"] = "event", ["ce-type"] = "approved" })).Status);
        Assert.Equal(409, (await host.Send("/workflows/{id}/events/{key}", "{\"approved\":true}", instanceId: first.Id,
            headers: new() { ["ce-specversion"] = "1.0", ["ce-source"] = "urn:binary", ["ce-id"] = "event", ["ce-type"] = "approved", ["ce-subject"] = "changed" })).Status);
    }

    [Theory]
    [InlineData("{\"specversion\":\"0.3\",\"id\":\"e\",\"source\":\"urn:x\",\"type\":\"approved\",\"data\":{}}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":1,\"source\":\"urn:x\",\"type\":\"approved\",\"data\":{}}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"e\",\"source\":\"bad source\",\"type\":\"approved\",\"data\":{}}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"e\",\"source\":\"urn:x\",\"type\":\"approved\",\"time\":\"yesterday\",\"data\":{}}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"e\",\"source\":\"urn:x\",\"type\":\"approved\",\"type\":\"other\",\"data\":{}}")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"e\",\"source\":\"urn:x\",\"type\":\"approved\",\"data_base64\":1}")]
    public async Task MalformedEnvelopesAreRefusedBeforeInboxAdmission(string body)
    {
        await using var host = new Harness();
        var first = await host.Send("/orders/{orderId}");
        Assert.Equal(400, (await host.Send("/workflows/{id}/events/{key}", body, instanceId: first.Id,
            contentType: "application/cloudevents+json")).Status);
    }

    [Fact]
    public async Task BinaryEnvelopeHeadersAreInStartFingerprintsAndSignedBinaryIsRefused()
    {
        await using var host = new Harness();
        var headers = new Dictionary<string, string> { ["ce-specversion"] = "1.0", ["ce-source"] = "urn:binary", ["ce-id"] = "order", ["ce-type"] = "created" };
        var first = await host.Send("/orders/{orderId}", key: "binary", headers: headers);
        Assert.Equal(202, first.Status);
        headers["ce-type"] = "changed";
        Assert.Equal(409, (await host.Send("/orders/{orderId}", key: "binary", headers: headers)).Status);
        Assert.Equal(400, (await host.Send("/signed/{orderId}", user: null, secret: NewSecret, headers: headers)).Status);
        Assert.Equal(413, (await host.Send("/orders/{orderId}", new string('x', 1024 * 1024 + 1))).Status);
    }

    [Fact]
    public async Task SignedEventDeliveryIdsCannotBeReusedForADifferentCloudEvent()
    {
        await using var host = new Harness();
        var first = await host.Send("/signed/{orderId}", user: null, secret: OldSecret);
        await host.Client.AwaitAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var accepted = await host.Send("/signed/workflows/{id}/events/{key}", Cloud(), user: null,
            instanceId: first.Id, contentType: "application/cloudevents+json", secret: OldSecret);
        Assert.Equal(202, accepted.Status);
        Assert.Equal(202, (await host.Send("/signed/workflows/{id}/events/{key}", Cloud(), user: null,
            instanceId: first.Id, contentType: "application/cloudevents+json", secret: NewSecret)).Status);
        Assert.Equal(409, (await host.Send("/signed/workflows/{id}/events/{key}", Cloud(id: "another"), user: null,
            instanceId: first.Id, contentType: "application/cloudevents+json", secret: NewSecret)).Status);
    }

    [Fact]
    public async Task SqlitePersistsOwnershipAndSignedEventDeduplicationInTheInboxTransaction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.HttpIngress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var repository = new SqliteWorkflowStateRepository($"Data Source={Path.Combine(folder, "state.db")}");
            await using var host = new Harness(repository);
            var first = await host.Send("/signed/{orderId}", user: null, secret: OldSecret);
            Assert.Equal(202, first.Status);
            await host.Client.AwaitAsync(first.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("vendor", (await ((IWorkflowAdmissionStore)repository).GetWorkflowOwnershipAsync(first.Id))!.Owner);
            var accepted = await host.Send("/signed/workflows/{id}/events/{key}", Cloud(), user: null,
                instanceId: first.Id, contentType: "application/cloudevents+json", secret: OldSecret);
            Assert.Equal(202, accepted.Status);
            Assert.Equal(202, (await host.Send("/signed/workflows/{id}/events/{key}", Cloud(), user: null,
                instanceId: first.Id, contentType: "application/cloudevents+json", secret: NewSecret)).Status);
            Assert.Equal(409, (await host.Send("/signed/workflows/{id}/events/{key}", Cloud(id: "other"), user: null,
                instanceId: first.Id, contentType: "application/cloudevents+json", secret: NewSecret)).Status);
        }
        finally { await Infrastructure.SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public void MissingPoliciesAndConflictingStableIdentitiesOrRoutesAreRefusedAtRegistration()
    {
        var definition = Workflow.Create<Data>("Registration").Step<Noop>().Build();
        Assert.Throws<InvalidOperationException>(() => new Harness(registrations: triggers =>
        {
            triggers.Start<Data>("unprotected", "/unprotected", definition).FromBody<Data>((data, _) => data);
            triggers.Status().Authorize("api").AuthorizeInstance("instance");
        }));
        Assert.Throws<InvalidOperationException>(() => new Harness(registrations: triggers =>
        {
            triggers.Start<Data>("duplicate", "/one", definition).FromBody<Data>((data, _) => data).Authorize("api");
            triggers.Start<Data>("duplicate", "/two", definition).FromBody<Data>((data, _) => data).Authorize("api");
            triggers.Status().Authorize("api").AuthorizeInstance("instance");
        }));
        Assert.Throws<InvalidOperationException>(() => new Harness(registrations: triggers =>
        {
            triggers.Start<Data>("one", "/same/{id}", definition).FromBody<Data>((data, _) => data).Authorize("api");
            triggers.Start<Data>("two", "/same/{other}", definition).FromBody<Data>((data, _) => data).Authorize("api");
            triggers.Status().Authorize("api").AuthorizeInstance("instance");
        }));
    }
}
