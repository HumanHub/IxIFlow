using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class ReplyOperationsEndpointTests
{
    [Theory]
    [InlineData("/ops/waits"), InlineData("/ops/instances/{id}/waits")]
    public async Task OperationsRejectMalformedAndReplyTokenCursorsBeforeAnyStoreAccess(string path)
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddIxIFlow();
        builder.Services.AddSingleton(store.Object);
        builder.Services.AddSingleton<IInstanceQueryScope, Scope>();
        builder.Services.AddAuthorization(options => options.AddPolicy("operator", policy => policy.RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.MapWorkflowOperations("/ops", options =>
        {
            options.ReadPolicy = options.ReplyPolicy = options.InspectPolicy = options.WritePolicy = options.OutboxPolicy = "operator";
        });
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == path);
        foreach (var token in new[] { "raw-wait-id", ReplyToken.Create("instance", "wait") })
        {
            var http = new DefaultHttpContext { RequestServices = app.Services,
                User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test")) };
            http.SetEndpoint(endpoint); http.Request.Method = "GET";
            http.Request.RouteValues["id"] = "instance";
            http.Request.Path = path.Replace("{id}", "instance");
            http.Request.QueryString = new("?tokens=true&after=" + token);
            http.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(http);
            Assert.Equal(400, http.Response.StatusCode);
        }
        store.VerifyNoOtherCalls();
    }

    private sealed class Scope : IInstanceQueryScope
    {
        public ValueTask<InstanceQuery> ScopeAsync(ClaimsPrincipal principal, InstanceQuery query, CancellationToken token = default) => new(query);
        public ValueTask<WaitQuery> ScopeAsync(ClaimsPrincipal principal, WaitQuery query, CancellationToken token = default) => new(query);
    }
}
