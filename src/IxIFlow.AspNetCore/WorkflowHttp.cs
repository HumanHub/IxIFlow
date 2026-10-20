using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace IxIFlow.AspNetCore;

// Hosting, authorization and failure handling shared by triggers and operations.
internal static class WorkflowHttp
{
    internal sealed record WorkflowRoute(string Identity);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void ValidatePolicy(IEndpointRouteBuilder endpoints, string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            throw new InvalidOperationException("Every workflow endpoint requires a policy");
        if (endpoints.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>()
                .GetPolicyAsync(policy).GetAwaiter().GetResult() == null)
            throw new InvalidOperationException("Unknown workflow authorization policy: " + policy);
    }

    internal static async Task<IResult?> AuthorizeAsync(HttpContext http, object resource, string policy)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Problem(statusCode: 401, title: "Authentication required");
        return (await http.RequestServices.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(http.User, resource, policy)).Succeeded
            ? null : Results.Problem(statusCode: 403, title: "Workflow access denied");
    }

    internal static async ValueTask<object?> HandleErrorsAsync(Func<ValueTask<object?>> next, bool inspectionRead = false)
    {
        try { return await next(); }
        catch (BadHttpRequestException error)
        { return Results.Problem(statusCode: error.StatusCode, title: "Invalid workflow request"); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        { return Results.Problem(statusCode: 400, title: "Invalid workflow request"); }
        catch (WorkflowEngineException error)
        {
            var (status, title) = error.Kind switch
            {
                WorkflowErrorKind.InvalidRequest => (400, "Invalid workflow request"),
                WorkflowErrorKind.InvalidState => (409, "Invalid workflow state"),
                WorkflowErrorKind.InstanceRemoved => (410, "Workflow removed"),
                WorkflowErrorKind.ResourceExhausted when inspectionRead => (503, "Workflow bundle exceeds the configured read limit"),
                WorkflowErrorKind.ResourceExhausted => (413, "Workflow request exceeds the configured limit"),
                WorkflowErrorKind.ArtifactUnavailable => (503, "Workflow version unavailable"),
                _ => (503, "Workflow service unavailable")
            };
            return Results.Problem(statusCode: status, title: title);
        }
    }

    internal static string Shape(string route) => Regex.Replace(route.TrimEnd('/'), @"\{(\*{0,2})[^}:?=]+([^}]*)\}", "{$1$2}");

    internal static void ValidateAtStartup(IEndpointRouteBuilder endpoints)
    {
        if (endpoints.DataSources.OfType<RegistrationValidator>().Any()) return;
        var validator = new RegistrationValidator(endpoints);
        endpoints.DataSources.Add(validator);
        endpoints.ServiceProvider.GetRequiredService<WorkflowHost>().RegisterStartupValidation(validator.Validate);
    }

    private sealed class RegistrationValidator(IEndpointRouteBuilder endpoints) : EndpointDataSource
    {
        private readonly object _gate = new();
        private bool _checking;
        public void Validate() => _ = Endpoints;
        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                lock (_gate)
                {
                    if (_checking) return [];
                    _checking = true;
                    try
                    {
                        var routes = endpoints.DataSources.Where(s => s is not RegistrationValidator)
                            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
                        if (routes.Select(r => r.Metadata.GetMetadata<WorkflowRoute>()).OfType<WorkflowRoute>()
                            .GroupBy(r => r.Identity, StringComparer.Ordinal).Any(g => g.Count() > 1))
                            throw new InvalidOperationException("Workflow endpoint identities must be unique");
                        foreach (var group in routes.GroupBy(r => Shape(r.RoutePattern.RawText ?? ""), StringComparer.OrdinalIgnoreCase))
                        {
                            if (!group.Any(r => r.Metadata.GetMetadata<WorkflowRoute>() != null)) continue;
                            var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var route in group)
                                foreach (var method in route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                                    if (!verbs.Add(method) || method == "*" && verbs.Count > 1 || method != "*" && verbs.Contains("*"))
                                        throw new InvalidOperationException("Conflicting routes at " + group.Key);
                        }
                        return [];
                    }
                    finally { _checking = false; }
                }
            }
        }
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
