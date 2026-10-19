using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.AspNetCore.WorkflowHttp;

namespace IxIFlow.AspNetCore;

public static class WorkflowTriggerExtensions
{
    internal static readonly JsonSerializerOptions JsonOptions = WorkflowHttp.JsonOptions;
    private static readonly object BodyKey = new();
    private static readonly object WebhookIdKey = new();
    private static readonly ConditionalWeakTable<IEndpointRouteBuilder, Catalog> Catalogs = new();
    private const string StatusName = "IxIFlow.WorkflowStatus";
    private sealed class Catalog { public List<TriggerRegistration> Registrations { get; } = []; }

    public static IEndpointRouteBuilder MapWorkflowTriggers(this IEndpointRouteBuilder endpoints,
        Action<WorkflowTriggerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new WorkflowTriggerBuilder();
        configure(builder);
        var catalog = Catalogs.GetValue(endpoints, _ => new());
        var all = catalog.Registrations.Concat(builder.Registrations).ToArray();
        if (all.Count(r => r.Status) != 1) throw new InvalidOperationException("Map exactly one authorized workflow status endpoint");
        _ = endpoints.ServiceProvider.GetRequiredService<IWorkflowIngressClient>();
        foreach (var registration in builder.Registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.Id) || registration.Id.Length > 128 || registration.Id != registration.Id.Trim())
                throw new InvalidOperationException("Each workflow trigger requires a stable identity of at most 128 characters");
            if (registration.Policy == null || (registration.Status || registration.Events) && registration.InstancePolicy == null)
                throw new InvalidOperationException("Every workflow endpoint requires a policy; status and events also require an instance policy");
            foreach (var policy in new[] { registration.Policy, registration.InstancePolicy }.OfType<string>())
                ValidatePolicy(endpoints, policy);
            var pattern = RoutePatternFactory.Parse(registration.Pattern);
            if ((registration.Status || registration.Events) && !pattern.Parameters.Any(p => p.Name == "id") ||
                registration.Events && !pattern.Parameters.Any(p => p.Name == "key"))
                throw new InvalidOperationException("Instance routes require id, and event routes require key");
            if (!registration.Status && !registration.Events && registration.Start == null)
                throw new InvalidOperationException("Start triggers require typed FromBody mapping");
            if (registration.Events && registration.EventTypes.Count == 0)
                throw new InvalidOperationException("Event triggers require an explicit CloudEvents type allowlist");
            if (registration.SignedSender != null && !registration.Events && !registration.Status && registration.Retention < registration.Tolerance)
                throw new InvalidOperationException("Signed start idempotency retention must be at least the timestamp tolerance");
        }
        var signingKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var registration in all)
            foreach (var secret in registration.Secrets)
                if (signingKeys.TryGetValue(Convert.ToBase64String(secret), out var trigger) && trigger != registration.Id)
                    throw new InvalidOperationException("A signing secret cannot be shared by two workflow triggers");
                else signingKeys[Convert.ToBase64String(secret)] = registration.Id;
        if (all.GroupBy(r => r.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Workflow trigger identities must be unique");
        if (all.GroupBy(r => r.Method + " " + Shape(r.Pattern), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Conflicting workflow routes");
        var registry = endpoints.ServiceProvider.GetRequiredService<IWorkflowCatalog>();
        foreach (var definition in builder.Registrations.Select(r => r.Definition).OfType<WorkflowDefinition>())
            registry.RegisterWorkflowAsync(definition).GetAwaiter().GetResult();
        catalog.Registrations.AddRange(builder.Registrations);
        foreach (var registration in builder.Registrations)
        {
            var route = endpoints.MapMethods(registration.Pattern, [registration.Method],
                (Delegate)((HttpContext context) => HandleAsync(context, registration, catalog)));
            route.WithMetadata(new WorkflowRoute(registration.Id));
            route.AddEndpointFilter(new IngressFilter(registration));
            if (registration.Status) route.WithName(StatusName);
        }
        ValidateAtStartup(endpoints);
        return endpoints;
    }

    private sealed class IngressFilter(TriggerRegistration registration) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        {
            var http = invocation.HttpContext;
            return await HandleErrorsAsync(async () =>
            {
                if (registration.SignedSender != null)
                {
                    var bytes = await ReadBodyAsync(http, registration.BodyLimit);
                    var id = SingleHeader(http, "webhook-id");
                    var timestamp = SingleHeader(http, "webhook-timestamp");
                    var signatures = SingleHeader(http, "webhook-signature");
                    var time = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                    if (!StandardWebhookSignature.Verify(registration.Secrets, id, timestamp, signatures, bytes,
                            time.GetUtcNow(), registration.Tolerance))
                        return Results.Problem(statusCode: 401, title: "Invalid webhook signature");
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, registration.SignedSender),
                        new Claim("ixiflow:sender", registration.SignedSender)
                    ], "StandardWebhooks"));
                    http.Items[WebhookIdKey] = id;
                }
                if (await WorkflowHttp.AuthorizeAsync(http, http, registration.Policy!) is { } denied)
                    return denied;
                return await next(invocation);
            });
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, int limit)
    {
        if (http.Items.TryGetValue(BodyKey, out var saved)) return (byte[])saved!;
        if (http.Request.ContentLength > limit) throw new BadHttpRequestException("Body limit exceeded", 413);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted);
            if (count == 0) break;
            if (memory.Length + count > limit) throw new BadHttpRequestException("Body limit exceeded", 413);
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        http.Items[BodyKey] = bytes;
        return bytes;
    }

    private static string SingleHeader(HttpContext http, string name)
    {
        var values = http.Request.Headers[name];
        if (values.Count > 1) throw new ArgumentException("Repeated request header");
        return values.ToString();
    }

    private static string Fingerprint(HttpRequest request, byte[] body, bool binary)
    {
        // Length-delimited JSON prevents ambiguities; key order is independent of dictionary iteration.
        var context = JsonSerializer.SerializeToUtf8Bytes(new
        {
            route = request.RouteValues.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, Value = p.Value?.ToString() }),
            query = request.Query.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, Value = p.Value.ToArray() }),
            contentType = request.ContentType?.ToLowerInvariant(),
            cloudEvents = binary ? request.Headers.Where(h => h.Key.StartsWith("ce-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => new { Key = h.Key.ToLowerInvariant(), Value = h.Value.ToString() }).ToArray() : null
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SHA256.HashData(body));
        hash.AppendData(context);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<IResult> HandleAsync(HttpContext http, TriggerRegistration registration, Catalog catalog)
    {
        var ingress = http.RequestServices.GetRequiredService<IWorkflowIngressClient>();
        var client = http.RequestServices.GetRequiredService<IWorkflowClient>();
        if (registration.Status || registration.Events)
        {
            var id = http.Request.RouteValues["id"]!.ToString()!;
            if (registration.Events && http.Request.Headers.ContainsKey("X-IxIFlow-Reply-Token") &&
                (!ReplyToken.TryRead(
                    SingleHeader(http, "X-IxIFlow-Reply-Token"), out var tokenInstance, out _, out _) || tokenInstance != id))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown reply token");
            var ownership = await ingress.GetOwnershipAsync(id);
            if (ownership == null) return Results.Problem(statusCode: 404, title: "Unknown workflow");
            var ownerKind = registration.SignedSender != null ? WorkflowOwnerKind.SignedSender : WorkflowOwnerKind.Principal;
            if (ownership.OwnerKind != ownerKind || !(await http.RequestServices.GetRequiredService<IAuthorizationService>()
                    .AuthorizeAsync(http.User, ownership, registration.InstancePolicy!)).Succeeded)
                return Results.Problem(statusCode: 404, title: "Unknown workflow");
            if (registration.Status) return await StatusAsync(http, client, id, ownership, catalog);
            if (await client.GetStatusAsync(id) == WorkflowExecutionStatus.Removed)
                return Results.Problem(statusCode: 409, title: "Event refused", detail: "InstanceRemoved");
            return await EventAsync(http, ingress, id, registration);
        }
        var body = await ReadBodyAsync(http, registration.BodyLimit);
        var parsed = ParsedBody.Parse(http.Request, body, required: false);
        if (parsed.Binary && registration.SignedSender != null)
            return Results.Problem(statusCode: 400, title: "Signed CloudEvents require structured mode");
        var fingerprint = Fingerprint(http.Request, body, parsed.Binary);
        var principalId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(principalId)) return Results.Problem(statusCode: 403, title: "A stable sender identity is required");
        // An issuer and subject form the authenticated sender namespace. Signed senders have an explicit stable name.
        var sender = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            registration.SignedSender != null ? new[] { "sender", registration.SignedSender }
                : new[] { "principal", http.User.FindFirst(ClaimTypes.NameIdentifier)!.Issuer, principalId, http.User.FindFirstValue("tenant") }))));
        var key = http.Items.TryGetValue(WebhookIdKey, out var signedId) ? (string)signedId! : SingleHeader(http, "Idempotency-Key");
        if (string.IsNullOrEmpty(key)) key = Guid.NewGuid().ToString("N");
        var (owner, tenant) = registration.Ownership?.Invoke(http) ?? (principalId, http.User.FindFirstValue("tenant"));
        var admission = new WorkflowAdmission(sender, registration.Id, key, fingerprint, owner!, tenant, registration.Retention,
            registration.SignedSender != null ? WorkflowOwnerKind.SignedSender : WorkflowOwnerKind.Principal);
        admission.Validate();
        var winner = await registration.Start!(http, parsed, admission);
        if (!string.Equals(winner.Fingerprint, fingerprint, StringComparison.Ordinal))
            return Results.Problem(statusCode: 409, title: "Idempotency key was used for a different request");
        if ((await ingress.GetOwnershipAsync(winner.InstanceId))?.Incarnation != winner.Incarnation ||
            await client.GetStatusAsync(winner.InstanceId) == WorkflowExecutionStatus.Removed)
            return Results.Problem(statusCode: 410, title: "The admitted workflow was removed");
        var location = http.RequestServices.GetRequiredService<LinkGenerator>()
            .GetPathByName(http, StatusName, new { id = winner.InstanceId })
            ?? throw new InvalidOperationException("The workflow status route is unavailable");
        if (registration.SyncWait is { } wait)
        {
            using var timeout = new CancellationTokenSource(wait);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, http.RequestAborted);
            try
            {
                var result = await client.AwaitAsync(winner.InstanceId, WorkflowAwaitCondition.Finished, waiting.Token);
                if (result.Status == WorkflowExecutionStatus.Removed ||
                    (await ingress.GetOwnershipAsync(winner.InstanceId))?.Incarnation != winner.Incarnation)
                    return Results.Problem(statusCode: 410, title: "The admitted workflow was removed");
                var projection = catalog.Registrations.Single(r => r.Id == winner.Trigger).Projection;
                return Results.Ok(PublicResult(result, projection));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested) { }
        }
        var status = await client.GetStatusAsync(winner.InstanceId);
        if (status == WorkflowExecutionStatus.Removed)
            return Results.Problem(statusCode: 410, title: "The admitted workflow was removed");
        return Results.Accepted(location, new WorkflowStatusResponse(winner.InstanceId, PublicStatus(status)));
    }

    private static async Task<IResult> StatusAsync(HttpContext http, IWorkflowClient client, string id, WorkflowOwnership ownership, Catalog catalog)
    {
        var status = await client.GetStatusAsync(id);
        if (status == null) return Results.Problem(statusCode: 404, title: "Unknown workflow");
        if (status == WorkflowExecutionStatus.Removed) return Results.Json(new WorkflowStatusResponse(id, "Removed"), statusCode: 410);
        if (IsTerminal(status.Value))
        {
            var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, http.RequestAborted);
            if (result.Status == WorkflowExecutionStatus.Removed) return Results.Json(new WorkflowStatusResponse(id, "Removed"), statusCode: 410);
            if ((await http.RequestServices.GetRequiredService<IWorkflowIngressClient>().GetOwnershipAsync(id))?.Incarnation != ownership.Incarnation)
                return Results.Problem(statusCode: 404, title: "Unknown workflow");
            var projection = catalog.Registrations.SingleOrDefault(r => r.Id == ownership.Trigger)?.Projection ?? (_ => null);
            return Results.Ok(PublicResult(result, projection));
        }
        return Results.Ok(new WorkflowStatusResponse(id, PublicStatus(status)));
    }

    private static WorkflowStatusResponse PublicResult(WorkflowExecutionResult result, Func<WorkflowExecutionResult, object?> projection) =>
        new(result.InstanceId, PublicStatus(result.Status), projection(result));

    private static bool IsTerminal(WorkflowExecutionStatus status) => status is WorkflowExecutionStatus.Success or
        WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled or WorkflowExecutionStatus.TimedOut or WorkflowExecutionStatus.Terminated;

    private static string PublicStatus(WorkflowExecutionStatus? status) => status switch
    {
        WorkflowExecutionStatus.Success => "Completed",
        WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.Failed => "Failed",
        WorkflowExecutionStatus.Cancelled => "Cancelled",
        WorkflowExecutionStatus.Terminated => "Terminated",
        WorkflowExecutionStatus.TimedOut => "TimedOut",
        WorkflowExecutionStatus.Removed => "Removed",
        WorkflowExecutionStatus.Suspended => "Waiting",
        WorkflowExecutionStatus.NeedsResolution => "Stopped",
        _ => "Running"
    };

    private static async Task<IResult> EventAsync(HttpContext http, IWorkflowIngressClient ingress, string instanceId,
        TriggerRegistration registration)
    {
        var body = await ReadBodyAsync(http, registration.BodyLimit);
        var parsed = ParsedBody.Parse(http.Request, body, required: true);
        if (parsed.Binary && registration.SignedSender != null)
            return Results.Problem(statusCode: 400, title: "Signed CloudEvents require structured mode");
        var envelope = parsed.Envelope!;
        if (!registration.EventTypes.TryGetValue(envelope.Type, out var map))
            return Results.Problem(statusCode: 400, title: "CloudEvents type is not allowlisted");
        var data = map(parsed.Data);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { envelope.Source, envelope.Id }))));
        var workflowEvent = new WorkflowEvent(instanceId, identity, data.GetType().AssemblyQualifiedName!,
            JsonSerializer.Serialize(data, data.GetType()), http.Request.RouteValues["key"]!.ToString(),
            DeliveryIdentity: http.Items.TryGetValue(WebhookIdKey, out var webhookId)
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new[] { registration.SignedSender, registration.Id, (string)webhookId! })))) : null,
            Source: envelope.Source, DeliveryRetention: registration.Retention);
        var receipt = http.Request.Headers.ContainsKey("X-IxIFlow-Reply-Token")
            ? await ingress.AcceptReplyAsync(SingleHeader(http, "X-IxIFlow-Reply-Token"), workflowEvent, http.RequestAborted)
            : await ingress.AcceptEventAsync(workflowEvent, http.RequestAborted);
        if (receipt.Status == EventAppendStatus.Duplicate)
        {
            if (receipt.Rejection != null)
                return Results.Problem(statusCode: 409, title: "Event refused", detail: receipt.Rejection.ToString());
            var stored = await ingress.GetEventAsync(instanceId, identity);
            if (stored?.Outcome is EventOutcome.Rejected or EventOutcome.Closed)
                return Results.Problem(statusCode: 409, title: "Event refused", detail: stored.Rejection?.ToString() ?? "InstanceFinished");
        }
        else if (receipt.Status != EventAppendStatus.Stored)
            return Results.Problem(statusCode: 409, title: "Event refused", detail: receipt.Rejection?.ToString() ?? receipt.Status.ToString());
        return Results.Accepted(value: new { id = envelope.Id, status = "Accepted" });
    }
}
