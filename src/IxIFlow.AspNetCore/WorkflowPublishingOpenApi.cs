using System.Text.Json.Nodes;
using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace IxIFlow.AspNetCore;

/// <summary>The resource policy and conditional request contract of a publishing endpoint.</summary>
public sealed record WorkflowPublishingEndpoint(string Policy, string? Precondition);

internal static class WorkflowPublishingOpenApi
{
    internal static void Configure(Microsoft.AspNetCore.OpenApi.OpenApiOptions options) => options.AddSchemaTransformer((schema, context, token) =>
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(WorkflowVersionResponse)) Member("state", Enum.GetNames<WorkflowVersionState>());
        if (type == typeof(WorkflowDependencyResponse)) Member("kind", Enum.GetNames<WorkflowDependencyKind>());
        if (type == typeof(ActivityProviderResponse)) Member("origin", Enum.GetNames<ActivityOrigin>());
        if (type == typeof(ActivityScopeResponse)) { Member("concurrency", Enum.GetNames<ScopeConcurrency>()); Member("threading", Enum.GetNames<ScopeThreading>()); }
        if (type == typeof(PackageResponse)) { Member("origin", Enum.GetNames<WorkflowPackageOrigin>()); Member("extraction", Enum.GetNames<PackageExtraction>()); }
        if (type == typeof(PackageActivitiesResponse)) Member("extraction", Enum.GetNames<PackageExtraction>());
        if (type == typeof(WorkflowPublicationResponse) || type == typeof(WorkflowPublicationSummary)) Member("state", Enum.GetNames<WorkflowPublicationState>());
        if (type == typeof(WorkflowDraftId) || type == typeof(WorkflowDraftSummary) || type == typeof(WorkflowValidationResponse)) Member("kind", ["workflow", "activity", "template"]);
        if (type == typeof(WorkflowDiagnosticResponse))
        {
            Member("class", Enum.GetNames<WorkflowPublishDiagnosticClass>()); Member("stage", Enum.GetNames<WorkflowPublishStage>());
            Member("severity", ["Info", "Warning", "Error"]);
        }
        return Task.CompletedTask;
        void Member(string name, string[] values) { if (schema.Properties?.ContainsKey(name) == true) schema.Properties[name] = Strings(values); }
    });
    internal static void Describe(RouteHandlerBuilder endpoint, string method, string route, string policy, Type? response,
        string[] queries, string? body, string? precondition)
    {
        endpoint.WithMetadata(new WorkflowPublishingEndpoint(policy, precondition)).WithTags("Workflow publishing");
        var contentType = route.EndsWith("/bundle", StringComparison.Ordinal) ? "application/zip" :
            route.EndsWith("/nupkg", StringComparison.Ordinal) || route.EndsWith("/{file}", StringComparison.Ordinal) ? "application/octet-stream" :
            route.EndsWith("/source", StringComparison.Ordinal) ? "application/yaml" : route == "/schemas/{kind}" ? "application/schema+json" : "application/json";
        var responseType = contentType is "application/zip" or "application/octet-stream" ? typeof(byte[]) :
            contentType == "application/schema+json" ? typeof(System.Text.Json.JsonElement) :
            contentType == "application/yaml" ? typeof(string) : response ?? typeof(void);
        if (route.EndsWith("/{file}", StringComparison.Ordinal)) endpoint.Produces(200, responseType, contentType, "application/xml");
        else endpoint.Produces(200, responseType, contentType);
        foreach (var status in new[] { 400, 401, 403, 404, 409, 412, 413, 415, 422, 428, 429, 500, 503 }) endpoint.ProducesProblem(status);
        if (route.EndsWith("/preview", StringComparison.Ordinal))
            foreach (var status in new[] { 409, 413, 422, 500, 503 }) endpoint.Produces(status, typeof(TemplatePreviewResponse), "application/json");
        if (method == "DELETE" && route.StartsWith("/drafts/", StringComparison.Ordinal)) endpoint.Produces(204);
        if (method == "PUT" || method == "POST" && route == "/catalog/packages") endpoint.Produces(201, response);
        if (route.EndsWith("/publish", StringComparison.Ordinal))
            foreach (var status in new[] { 202, 409, 413, 422, 500, 503 }) endpoint.Produces<WorkflowPublicationResponse>(status, "application/json");
        if (method == "GET" && !route.EndsWith("/holders", StringComparison.Ordinal) &&
            (!route.Contains("/nuget/", StringComparison.Ordinal) || route.EndsWith("/{file}", StringComparison.Ordinal))) endpoint.Produces(304);
        if (route.EndsWith("/preview", StringComparison.Ordinal)) endpoint.Accepts<TemplatePreviewRequest>("application/json");
        else if (body != null) endpoint.Accepts<string>(route.EndsWith("/validate", StringComparison.Ordinal), body);
        endpoint.AddOpenApiOperationTransformer((operation, context, token) =>
        {
            operation.Description = "Required policy: " + policy + ". Catalog scope is supplied by the application."
                + (route.EndsWith("/preview", StringComparison.Ordinal) ? " Trusted execution of model constructors, converters and rendering; bounded by publisher model/output limits and BuildTimeout. No writes." : "")
                + (precondition == null ? "" : " Required precondition: " + precondition + ". Missing 428; malformed 400; stale 412.");
            // Per-status Produces metadata selects one response type. Preserve the filter's problem variant too.
            if ((route.EndsWith("/preview", StringComparison.Ordinal) || route.EndsWith("/publish", StringComparison.Ordinal)) && operation.Responses?["400"] is OpenApiResponse badRequest &&
                badRequest.Content?.TryGetValue("application/problem+json", out var problemMedia) == true)
                foreach (var status in new[] { "409", "413", "422", "500", "503" })
                    if (operation.Responses[status] is OpenApiResponse result)
                    {
                        result.Content ??= new Dictionary<string, OpenApiMediaType>();
                        result.Content["application/problem+json"] = problemMedia;
                    }
            if (body == "application/octet-stream" && operation.RequestBody?.Content?.TryGetValue(body, out var media) == true)
                media.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            operation.Parameters ??= [];
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(route, @"\{([^}]+)\}"))
            {
                var name = match.Groups[1].Value;
                if (operation.Parameters.Any(parameter => parameter.Name == name && parameter.In == ParameterLocation.Path)) continue;
                operation.Parameters.Add(new OpenApiParameter { Name = name, In = ParameterLocation.Path, Required = true,
                    Schema = name == "kind" ? Strings("workflow", "activity", "template") : new OpenApiSchema { Type = name == "version" && !route.Contains("activities") && !route.Contains("packages") && !route.Contains("nuget") ? JsonSchemaType.Integer : JsonSchemaType.String } });
            }
            foreach (var query in queries)
            {
                var schema = query switch
                {
                    "state" => Strings(Enum.GetNames<WorkflowVersionState>()),
                    "origin" => Strings(route.Contains("activities") ? Enum.GetNames<ActivityOrigin>() : Enum.GetNames<WorkflowPackageOrigin>()),
                    "withdrawn" or "artifactPresent" => new OpenApiSchema { Type = JsonSchemaType.Boolean },
                    "limit" => new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "1000", Default = JsonValue.Create(100) },
                    _ => new OpenApiSchema { Type = JsonSchemaType.String }
                };
                operation.Parameters.Add(new OpenApiParameter { Name = query, In = ParameterLocation.Query,
                    Required = route.Contains("/activities/{key}/versions/{version}", StringComparison.Ordinal), Schema = schema });
            }
            if (precondition != null)
            {
                operation.Parameters.Add(new OpenApiParameter { Name = "If-Match", In = ParameterLocation.Header,
                    Required = precondition != "draft-create-or-update", Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    Description = precondition == "lifecycle" ? "Lifecycle ETag returned by detail: \"l-{revision}\"." : "Full draft ETag returned by save/detail: \"d-{generation:N}-{revision}\"." });
                if (precondition == "draft-create-or-update") operation.Parameters.Add(new OpenApiParameter
                { Name = "If-None-Match", In = ParameterLocation.Header, Schema = Strings("*"), Description = "Required for create; mutually exclusive with If-Match." });
            }
            operation.Parameters.Add(new OpenApiParameter { Name = "X-IxIFlow-Actor", In = ParameterLocation.Header, Schema = new OpenApiSchema { Type = JsonSchemaType.String }, Description = "Requires the configured ActForPolicy." });
            if (method == "GET") operation.Parameters.Add(new OpenApiParameter { Name = "If-None-Match", In = ParameterLocation.Header, Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
            foreach (var (status, responseValue) in operation.Responses ?? [])
            {
                if (responseValue is not OpenApiResponse value) continue;
                value.Headers ??= new Dictionary<string, IOpenApiHeader>();
                value.Headers["Cache-Control"] = Header("private, no-cache for revision validators; no-store for availability, liveness and attempts.");
                if (status is "200" or "201" or "304") value.Headers["ETag"] = Header("Draft, lifecycle, artifact digest, or W/\"{revision}-{scopeAndQueryHash}\" validator.");
                if (status is "201" or "202" || route.EndsWith("/publish", StringComparison.Ordinal) || method == "POST" && route == "/catalog/packages") value.Headers["Location"] = Header("Resolvable resource or exact publication attempt URL.");
                if (status is "202" or "429") value.Headers["Retry-After"] = Header("5 seconds.");
            }
            return Task.CompletedTask;
        });
    }
    private static OpenApiHeader Header(string description) => new() { Description = description, Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
    private static OpenApiSchema Strings(params string[] values) => new() { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
