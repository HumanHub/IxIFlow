using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.AspNetCore;

internal static class WorkflowPublishingHttp
{
    private static readonly object ScopeKey = new(), ActorKey = new();
    internal static IWorkflowCatalogStore Store(HttpContext http) => http.RequestServices.GetRequiredService<IWorkflowCatalogStore>();
    internal static IWorkflowCatalog Catalog(HttpContext http) => http.RequestServices.GetRequiredService<IWorkflowCatalog>();
    internal static CatalogScope Scope(HttpContext http) => (CatalogScope)http.Items[ScopeKey]!;
    internal static string? Actor(HttpContext http) => http.Items.TryGetValue(ActorKey, out var actor) ? (string?)actor : http.User.Identity?.Name;
    internal static string Route(HttpContext http, string key) => Convert.ToString(http.Request.RouteValues[key], CultureInfo.InvariantCulture)
        ?? throw new ArgumentException("Missing route value");
    internal static int Version(HttpContext http) => PositiveInt(Route(http, "version"));
    internal static int PositiveInt(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
        ? number : throw new ArgumentException("Invalid positive integer");
    internal static (string Kind, string Name, int Version) DraftKey(HttpContext http)
    {
        var key = (Kind: Route(http, "kind"), Name: Route(http, "name"), Version: Version(http));
        WorkflowPublicationRules.Key(key.Kind, key.Name, key.Version);
        return key;
    }
    internal static string? Query(HttpContext http, string name)
    {
        if (!http.Request.Query.TryGetValue(name, out var value)) return null;
        if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0])) throw new ArgumentException("Invalid query value");
        return WorkflowCatalogRules.ValidateText(value[0]);
    }
    internal static int Limit(HttpContext http)
    {
        var limit = Query(http, "limit") is { } value ? PositiveInt(value) : 100;
        if (limit > 1000) throw new ArgumentException("Invalid page limit");
        return limit;
    }
    internal static T? EnumQuery<T>(HttpContext http, string name) where T : struct, Enum => Query(http, name) is { } value
        ? Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value) : throw new ArgumentException("Invalid enum value") : null;
    internal static bool? BoolQuery(HttpContext http, string name) => Query(http, name) switch
    { null => null, "true" => true, "false" => false, _ => throw new ArgumentException("Invalid boolean") };
    internal static CatalogVersionQuery Versions(HttpContext http) => new(Scope(http), Query(http, "after"), Limit(http));
    internal static IResult Missing() => Results.Problem(statusCode: 404, title: "Catalog resource not found");
    internal static IResult Problem(int status, string title, string? code = null, string? @class = null) =>
        Results.Problem(statusCode: status, title: title, extensions: code == null ? null : new Dictionary<string, object?> { ["code"] = code, ["class"] = @class });
    internal static string DraftTag(WorkflowDraftId id) => $"\"d-{id.Generation:N}-{id.Revision.ToString(CultureInfo.InvariantCulture)}\"";
    internal static string LifecycleTag(long revision) => $"\"l-{revision.ToString(CultureInfo.InvariantCulture)}\"";

    internal static WorkflowDraftId? DraftPrecondition(HttpContext http, bool createAllowed)
    {
        var key = DraftKey(http);
        var hasMatch = http.Request.Headers.ContainsKey("If-Match");
        var hasNone = http.Request.Headers.ContainsKey("If-None-Match");
        if (!hasMatch && !hasNone) throw new PreconditionRequiredException();
        if (hasNone)
        {
            if (!createAllowed || hasMatch || http.Request.Headers.IfNoneMatch.Count != 1 || http.Request.Headers.IfNoneMatch[0] != "*")
                throw new ArgumentException("Invalid create precondition");
            return null;
        }
        var tag = http.Request.Headers.IfMatch;
        if (tag.Count != 1) throw new ArgumentException("One draft ETag is required");
        var text = tag[0]!;
        if (text.Length < 38 || !text.StartsWith("\"d-", StringComparison.Ordinal) || text[^1] != '"' || text[35] != '-' ||
            !Guid.TryParseExact(text.AsSpan(3, 32), "N", out var generation) || generation == Guid.Empty ||
            !long.TryParse(text.AsSpan(36, text.Length - 37), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
            throw new ArgumentException("Invalid draft ETag");
        return new(key.Kind, key.Name, key.Version, generation, revision);
    }
    internal static CatalogMutationToken LifecyclePrecondition(HttpContext http)
    {
        if (!http.Request.Headers.ContainsKey("If-Match")) throw new PreconditionRequiredException();
        var tags = http.Request.Headers.IfMatch;
        var text = tags.Count == 1 ? tags[0]! : "";
        if (http.Request.Headers.ContainsKey("If-None-Match") || !text.StartsWith("\"l-", StringComparison.Ordinal) || text.Length < 5 || text[^1] != '"' ||
            !long.TryParse(text.AsSpan(3, text.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new ArgumentException("Invalid lifecycle ETag");
        return new(revision);
    }
    internal static async Task<string?> SourceAsync(HttpContext http, int maximum, bool optional = false)
    {
        if (http.Request.ContentLength > maximum) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Draft exceeds the configured limit");
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(maximum, 81920)];
        int count;
        while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) != 0)
        {
            if (output.Length + count > maximum) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Draft exceeds the configured limit");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 && optional) return null;
        try { return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length)); }
        catch (DecoderFallbackException) { throw new ArgumentException("Source must be UTF-8"); }
    }
    internal static void Identity(string source, string kind, string name, int version)
    {
        var identity = WorkflowDocumentKinds.Identify(source);
        if ((identity.Kind, identity.Name, identity.Version) != (kind, name, version)) throw new ArgumentException("Document identity differs from route");
    }
    internal static WorkflowDraftSummary Summary(WorkflowDraft draft) => new(draft.Kind, draft.Name, draft.Version, draft.Generation,
        draft.Revision, draft.Source.Length, draft.UpdatedAtUtc!.Value, draft.UpdatedBy);
    internal static async Task<WorkflowDraftResponse> DraftResponseAsync(HttpContext http, WorkflowDraft draft)
    {
        // History is ascending. Stream its keyset pages so metadata reports the latest attempt,
        // including several attempts sharing a store-clock timestamp, without retaining the history.
        WorkflowPublication? latest = null;
        string? cursor = null;
        do
        {
            http.RequestAborted.ThrowIfCancellationRequested();
            var attempts = await Store(http).GetPublicationsAsync(new(Scope(http), draft.Kind, draft.Name, draft.Version, draft.Generation, cursor, 100));
            foreach (var attempt in attempts.Items)
                if (latest == null || attempt.StartedAtUtc > latest.StartedAtUtc || attempt.StartedAtUtc == latest.StartedAtUtc &&
                    (attempt.Draft.Revision > latest.Draft.Revision || attempt.Draft.Revision == latest.Draft.Revision && attempt.Attempt > latest.Attempt)) latest = attempt;
            cursor = attempts.NextCursor;
        } while (cursor != null);
        return new(Summary(draft), latest == null ? null : new(latest.Id, latest.State.ToString(), latest.Attempt, latest.StartedAtUtc));
    }
    internal static IResult Json<T>(T value, int status = 200) => Results.Json(value, WorkflowHttp.JsonOptions, statusCode: status);
    internal static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
    internal static IResult Cached<T>(HttpContext http, T value, long revision, object query, bool noStore = false)
    {
        if (noStore) { NoStore(http); return Json(value); }
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Scope(http).Hash, Query = query })));
        var tag = $"W/\"{revision.ToString(CultureInfo.InvariantCulture)}-{hash}\"";
        http.Response.Headers.ETag = tag;
        http.Response.Headers.CacheControl = "private, no-cache";
        return Matches(http, tag) ? Results.StatusCode(304) : Json(value);
    }
    internal static bool Matches(HttpContext http, string tag) => http.Request.Headers.IfNoneMatch.ToString().Split(',').Any(value =>
        value.Trim() == "*" || value.Trim().Replace("W/", "", StringComparison.Ordinal) == tag.Replace("W/", "", StringComparison.Ordinal));
    internal static CatalogPage<U> Project<T, U>(CatalogPage<T> page, Func<T, U> project) => new(page.Items.Select(project).ToArray(), page.NextCursor, page.CatalogRevision);

    internal static async Task<WorkflowPublicationResponse> PublicationAsync(HttpContext http, WorkflowPublication attempt)
    {
        WorkflowVersionDetailResponse? workflow = null; PackageDetailResponse? package = null; CatalogTemplateDetail? template = null;
        if (attempt.Outcome?.Receipt is { } receipt)
        {
            if (receipt.Draft != attempt.Draft) throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Publication receipt identity mismatch");
            WorkflowPublishResult decoded;
            try { decoded = WorkflowPublishResult.FromReceipt(receipt); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid stored publication receipt", error); }
            var store = Store(http); var scope = Scope(http);
            if (decoded.Workflow is { } w && await store.GetWorkflowVersionDetailAsync(w.Name, w.Version, scope) is { } wd) workflow = PublishingResponses.Project(wd);
            if (decoded.Package is { } p && await store.GetPackageDetailAsync(p.Id, p.Version, scope) is { } pd) package = PublishingResponses.Project(pd);
            if (decoded.Template is { } t) template = await store.GetTemplateDetailAsync(t.Version.Key, t.Version.Version, scope);
        }
        return new(attempt.Id, attempt.Draft, attempt.Attempt, attempt.State.ToString(), workflow, package, template,
            attempt.Outcome?.Diagnostics.Select(PublishingResponses.Project).ToArray() ?? []);
    }
    internal sealed class PreconditionRequiredException : Exception;
    internal sealed class Filter(WorkflowPublishingOptions options) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            NoStore(http);
            try
            {
                if (http.User.Identity?.IsAuthenticated != true) return Problem(401, "Authentication required");
                http.Items[ScopeKey] = await http.RequestServices.GetRequiredService<ICatalogQueryScope>().ScopeAsync(http.User, http.RequestAborted)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "The catalog scope is required");
                if (http.Request.Headers.TryGetValue("X-IxIFlow-Actor", out var header))
                {
                    if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0])) throw new ArgumentException("Invalid actor");
                    WorkflowPublicationRules.Actor(header[0]);
                    if (options.ActForPolicy == null) return Problem(403, "Actor delegation is not allowed");
                    if (await WorkflowHttp.AuthorizeAsync(http, http, options.ActForPolicy) is { } denied) return denied;
                    http.Items[ActorKey] = header[0];
                }
                return await next(context);
            }
            catch (PreconditionRequiredException) { return Problem(428, "A resource precondition is required"); }
            catch (CatalogPreconditionException) { return Problem(412, "The resource precondition is stale"); }
            catch (BadHttpRequestException error) { return Problem(error.StatusCode, "Invalid publishing request"); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or JsonException or IxIFlow.Generators.WorkflowSourceException or YamlDotNet.Core.YamlException)
            { return Problem(400, "Invalid publishing request"); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                http.RequestServices.GetRequiredService<ILogger<WorkflowPublishingOptions>>().LogError(error, "Publishing HTTP request failed");
                var diagnostic = WorkflowPublishFailures.Classify(error);
                var status = error is WorkflowEngineException { Kind: WorkflowErrorKind.InvalidRequest } ? 400 : Status(diagnostic.Class);
                return Problem(status, diagnostic.Message, diagnostic.Code, diagnostic.Class.ToString());
            }
        }
    }
    internal static int Status(WorkflowPublishDiagnosticClass kind) => kind switch
    { WorkflowPublishDiagnosticClass.Authoring => 422, WorkflowPublishDiagnosticClass.Conflict => 409, WorkflowPublishDiagnosticClass.Unavailable => 503,
        WorkflowPublishDiagnosticClass.Limit => 413, _ => 500 };
}
