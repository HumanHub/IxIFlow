using System.Security.Claims;
using System.Text;
using IxIFlow.AspNetCore;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class IncomingWebhookEndpointTests
{
    private sealed class Slice4ReplyHarness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public IWorkflowStateRepository Store { get; }
        public IWorkflowClient Client => App.Services.GetRequiredService<IWorkflowClient>();
        public TimeProvider Time { get; }
        public Slice4ReplyProbe Probe => App.Services.GetRequiredService<Slice4ReplyProbe>();

        public Slice4ReplyHarness(
            IWorkflowStateRepository? store = null,
            TimeProvider? time = null,
            IWorkflowCatalog? registry = null,
            Action<WorkflowTriggerBuilder>? registrations = null)
        {
            Time = time ?? TimeProvider.System;
            Store = store ?? new InMemoryWorkflowStateRepository(Time);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.Services.AddIxIFlow();
            builder.Services.AddTransient<Noop>();
            builder.Services.AddTransient<Slice4Request>();
            builder.Services.AddSingleton<Slice4ReplyProbe>();
            builder.Services.AddTransient<Failing>();
            builder.Services.Replace(ServiceDescriptor.Singleton(Store));
            builder.Services.Replace(ServiceDescriptor.Singleton(Time));

            if (registry != null)
                builder.Services.Replace(ServiceDescriptor.Singleton(registry));

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("api", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy("vendor", policy => policy.RequireClaim("ixiflow:sender", "vendor"));

                options.AddPolicy(
                    "instance", policy => policy.RequireAssertion(context =>
                        context.Resource is WorkflowOwnership ownership &&
                        (ownership.Owner == context.User.FindFirstValue(ClaimTypes.NameIdentifier) ||
                            ownership.Tenant != null && ownership.Tenant == context.User.FindFirstValue("tenant"))));
            });

            App = builder.Build();
            App.MapWorkflowTriggers(registrations!);
            App.UseRouting();

            App.UseEndpoints(_ =>
            {
            });
        }

        public async Task<Response> Send(
            string pattern,
            string body = "{}",
            string? user = "alice",
            string? key = null,
            string orderId = "1",
            string? instanceId = null,
            string eventKey = "approval",
            string query = "",
            string contentType = "application/json",
            string? secret = null,
            string deliveryId = "delivery",
            DateTimeOffset? timestamp = null,
            string? tenant = null,
            Dictionary<string, string>? headers = null,
            bool contentLength = true,
            CancellationToken requestAborted = default)
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

            if (pattern.Contains("{orderId}"))
                http.Request.RouteValues["orderId"] = orderId;

            if (pattern.Contains("{id}"))
                http.Request.RouteValues["id"] = instanceId;

            if (pattern.Contains("{key}"))
                http.Request.RouteValues["key"] = eventKey;

            http.Request.QueryString = new QueryString(query);
            http.Request.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);

            if (contentLength)
                http.Request.ContentLength = bytes.Length;

            http.RequestAborted = requestAborted;
            http.Response.Body = new MemoryStream();

            if (user != null)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };

                if (tenant != null)
                    claims.Add(new("tenant", tenant));

                http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            }

            if (key != null)
                http.Request.Headers["Idempotency-Key"] = key;

            if (secret != null)
            {
                var seconds = (timestamp ?? Time.GetUtcNow()).ToUnixTimeSeconds().ToString();
                http.Request.Headers["webhook-id"] = deliveryId;
                http.Request.Headers["webhook-timestamp"] = seconds;

                http.Request.Headers["webhook-signature"] =
                    StandardWebhookSignature.Sign(StandardWebhookSignature.DecodeSecret(secret), deliveryId, seconds, bytes);
            }

            if (headers != null)
                foreach (var header in headers)
                    http.Request.Headers[header.Key] = header.Value;

            await endpoint.RequestDelegate!(http);
            http.Response.Body.Position = 0;

            return new(
                http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync(),
                http.Response.Headers.Location.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await App.Services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }
}