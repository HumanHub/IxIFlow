using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using IxIFlow.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.AspNetCore.WorkflowHttp;

namespace IxIFlow.AspNetCore;

public static class WorkflowOperationsExtensions
{
    private static readonly object ActorKey = new();

    public static RouteGroupBuilder MapWorkflowOperations(this IEndpointRouteBuilder endpoints, string prefix,
        Action<WorkflowOperationsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var options = new WorkflowOperationsOptions();
        configure(options);
        foreach (var policy in new[] { options.ReadPolicy, options.ReplyPolicy, options.InspectPolicy, options.WritePolicy, options.OutboxPolicy })
            ValidatePolicy(endpoints, policy);
        if (options.ActForPolicy != null) ValidatePolicy(endpoints, options.ActForPolicy);
        if (options.MutationWait < TimeSpan.Zero || options.MutationWait > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options.MutationWait));
        _ = endpoints.ServiceProvider.GetRequiredService<IWorkflowOperations>();
        if (!endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IInstanceQueryScope)))
            throw new InvalidOperationException("Workflow operations require an application IInstanceQueryScope");

        var group = endpoints.MapGroup(prefix);
        Map("GET", "/unschedulable", options.ReadPolicy!, http => UnschedulableAsync(http, options.ReadPolicy!));
        Map("GET", "/instances", options.ReadPolicy!, async http =>
        {
            var q = new Query(http);
            var statuses = q.Text("status")?.Split(',').Select(ParseEnum<WorkflowStatus>).ToArray();
            var query = new InstanceQuery(statuses, q.Enum<WorkflowStopKind>("stop"), q.Text("workflow"), q.Int("version"),
                q.Text("correlationId"), q.Text("parentInstanceId"), q.Date("createdFrom"), q.Date("createdTo"),
                q.Date("endedFrom"), q.Date("endedTo"), q.Text("after"), q.Limit());
            var scope = http.RequestServices.GetRequiredService<IInstanceQueryScope>();
            var scoped = await scope.ScopeAsync(http.User, query, http.RequestAborted);
            return Results.Ok(await Operations(http).SearchAsync(scoped, http.RequestAborted));
        });
        MapInstance("GET", "", options.ReadPolicy!, (http, summary) => Task.FromResult<IResult>(Results.Ok(summary)));
        MapInstance("GET", "/waits", options.ReadPolicy!, async (http, summary) =>
        {
            if (!new Query(http).Bool("tokens"))
                return Results.Ok(await Operations(http).GetWaitsAsync([summary.InstanceId], http.RequestAborted));
            var q = new Query(http);
            return await WaitsAsync(http, new WaitQuery(InstanceId: summary.InstanceId, AfterWaitRef: WaitBoundary(q.Text("after")), Limit: q.Limit()));
        }, tokenWaits: true);
        Map("GET", "/waits", options.ReplyPolicy!, async http =>
        {
            var q = new Query(http);
            var query = new WaitQuery(Key: q.Text("key"), EventType: q.Text("eventType"),
                AfterWaitRef: WaitBoundary(q.Text("after")), Limit: q.Limit());
            var scope = http.RequestServices.GetRequiredService<IInstanceQueryScope>();
            return await WaitsAsync(http, await scope.ScopeAsync(http.User, query, http.RequestAborted));
        });
        MapInstance("GET", "/pending-activities", options.ReadPolicy!, async (http, summary) =>
        {
            var result = await Client(http).GetPendingActivitiesAsync(summary.InstanceId);
            return result.IsRemoved ? Removed() : Results.Ok(result.ToArray());
        });
        MapInstance("GET", "/trace", options.InspectPolicy!, async (http, summary) =>
        {
            var q = new Query(http);
            var after = q.Long("after") ?? 0;
            if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
            var limit = q.Limit();
            var entries = await Operations(http).GetTraceAsync(summary.InstanceId, after, limit, http.RequestAborted);
            if (await Client(http).GetStatusAsync(summary.InstanceId) == WorkflowExecutionStatus.Removed) return Removed();
            return Results.Ok(new WorkflowTracePage(entries.Select(e => new WorkflowTraceResponse(e.Sequence,
                e.Entry.TraceId, e.Entry.Timestamp, e.Entry.Clock, e.Entry.StepNumber, e.Entry.ActivityName,
                e.Entry.EntryType, e.Entry.Duration, e.Entry.ErrorMessage, e.Entry.Payload)).ToArray(),
                entries.Count == limit ? entries[^1].Sequence : null));
        }, inspect: true);
        MapInstance("GET", "/snapshot", options.InspectPolicy!, async (http, summary) =>
        {
            var snapshot = await Client(http).GetSnapshotAsync(summary.InstanceId);
            if (snapshot?.IsRemoved == true) return Removed();
            if (snapshot == null) return Unknown();
            return Results.Ok(new WorkflowSnapshotResponse(snapshot.InstanceId, snapshot.WorkflowName, snapshot.WorkflowVersion,
                snapshot.Status, snapshot.CapturedAtUtc, snapshot.Pointers.Select(p => new WorkflowPointerResponse(
                    p.PointerId, p.StepId, p.StepIndex, p.Status, p.FrameId)).ToArray(),
                snapshot.Frames.Select(f => new WorkflowFrameResponse(f.FrameId, f.Kind, f.StepId, f.ParentFrameId)).ToArray()));
        }, inspect: true);
        MapInstance("POST", "/resolutions", options.WritePolicy!, async (http, summary) =>
        {
            var request = await BodyAsync<ResolutionRequest>(http);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.InvocationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Note);
            var kind = ParseEnum<ActivityResolutionKind>(request.Kind);
            var resolution = kind == ActivityResolutionKind.Completed
                ? request.InvocationResult is { } result
                    ? ActivityResolution.CompletedInvocation(result, request.Note, Actor(http))
                    : ActivityResolution.Completed(new Dictionary<string, object?>(request.Outputs ?? [], StringComparer.OrdinalIgnoreCase), request.Note, Actor(http))
                : ActivityResolution.Failed(request.ErrorMessage ?? request.Note, Actor(http));
            try
            {
                return await MutateAsync(http, summary.InstanceId, options,
                    () => Operations(http).SubmitResolutionAsync(summary.InstanceId, request.InvocationId, resolution, summary.Incarnation));
            }
            catch (ArgumentException error) when (error.ParamName == "invocationResult")
            {
                return Results.Problem(statusCode: 400, title: "Invalid child workflow result", detail: error.Message);
            }
        });
        MapInstance("POST", "/release", options.WritePolicy!, (http, summary) => MutateAsync(http, summary.InstanceId, options,
            () => Operations(http).SubmitReleaseAsync(summary.InstanceId, Actor(http), summary.Incarnation)));
        MapInstance("POST", "/cancel", options.WritePolicy!, async (http, summary) =>
        {
            var request = await BodyAsync<ReasonRequest>(http);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ReasonCode);
            return await MutateAsync(http, summary.InstanceId, options, () => Operations(http).SubmitCancellationAsync(
                summary.InstanceId, new CancellationReason { ReasonCode = request.ReasonCode, Description = request.Description ?? "", RequestedBy = Actor(http) }, summary.Incarnation));
        });
        MapInstance("POST", "/terminate", options.WritePolicy!, async (http, summary) =>
        {
            var request = await BodyAsync<ReasonRequest>(http);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ReasonCode);
            return await MutateAsync(http, summary.InstanceId, options, () => Operations(http).SubmitTerminationAsync(
                summary.InstanceId, request.ReasonCode, request.Description, Actor(http), summary.Incarnation));
        });
        Map("GET", "/outbox/publishers", options.OutboxPolicy!, async http =>
            Results.Ok(await Operations(http).ListPublishersAsync(Actor(http), http.RequestAborted)));
        Map("POST", "/outbox/publishers/{name}/enable", options.OutboxPolicy!, async http =>
        {
            var ops = Operations(http);
            var name = Route(http, "name");
            var state = (await ops.ListPublishersAsync(Actor(http))).SingleOrDefault(p => p.Name == name);
            if (state == null) return Unknown();
            if (!await ops.EnablePublisherAsync(name, Actor(http))) return Conflict();
            return Results.Ok((await ops.ListPublishersAsync(Actor(http))).Single(p => p.Name == name));
        });
        Map("DELETE", "/outbox/publishers/{name}", options.OutboxPolicy!, async http =>
        {
            var ops = Operations(http);
            var name = Route(http, "name");
            if (!(await ops.ListPublishersAsync(Actor(http))).Any(p => p.Name == name)) return Unknown();
            await ops.UnregisterPublisherAsync(name, Actor(http));
            return Results.Ok((await ops.ListPublishersAsync(Actor(http))).Single(p => p.Name == name));
        });
        Map("GET", "/outbox/deliveries", options.OutboxPolicy!, async http =>
        {
            var q = new Query(http);
            var page = await Operations(http).ListDeliveriesAsync(new DeliveryQuery(q.Text("publisher"),
                q.Enum<OutboxDeliveryStatus>("status"), q.Text("instanceId"), q.Bool("neverAttempted"), q.Text("after"), q.Limit()),
                Actor(http), http.RequestAborted);
            return Results.Ok(new WorkflowDeliveryPage(page.Items.Select(d => new WorkflowDeliveryResponse(new(
                d.Event.Id, d.Event.Type, d.Event.InstanceId, d.Event.WorkflowName, d.Event.WorkflowVersion,
                d.Event.CorrelationId, d.Event.OccurredAtUtc), d.PublisherName, d.Status, d.Attempts, d.FirstAttemptAtUtc,
                d.NextAttemptAtUtc, d.LastError, d.GivenUpReason, d.RetriedAtUtc, d.RetriedBy)).ToArray(), page.NextCursor));
        });
        Map("POST", "/outbox/deliveries/{eventId}/{publisher}/retry", options.OutboxPolicy!, async http =>
        {
            var ops = Operations(http);
            var eventId = Route(http, "eventId");
            var publisher = Route(http, "publisher");
            if (await ops.GetDeliveryAsync(eventId, publisher, Actor(http)) == null) return Unknown();
            return await ops.RetryDeliveryAsync(eventId, publisher, Actor(http)) ? Results.Ok(new { eventId, publisher }) : Conflict();
        });
        ValidateAtStartup(endpoints);
        return group;

        void Map(string method, string route, string policy, Func<HttpContext, Task<IResult>> handler,
            bool instance = false, bool inspect = false, bool tokenWaits = false)
        {
            var endpoint = group.MapMethods(route, [method], (Delegate)handler);
            endpoint.WithMetadata(new WorkflowRoute("operations:" + prefix + ":" + method + ":" + route));
            endpoint.AddEndpointFilter(new OperationsFilter(options, policy, instance, inspect, tokenWaits, route == "/waits"));
        }
        void MapInstance(string method, string suffix, string policy, Func<HttpContext, InstanceSummary, Task<IResult>> handler,
            bool inspect = false, bool tokenWaits = false) => Map(method, "/instances/{id}" + suffix, policy,
                http => handler(http, (InstanceSummary)http.Items[typeof(InstanceSummary)]!), true, inspect, tokenWaits);
    }

    private sealed class OperationsFilter(WorkflowOperationsOptions options, string policy, bool instance, bool inspect, bool tokenWaits, bool globalWaits) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) => HandleErrorsAsync(async () =>
        {
            var http = invocation.HttpContext;
            var effective = tokenWaits && new Query(http).Bool("tokens") ? options.ReplyPolicy! : policy;
            if (await AuthorizeAsync(http, http, effective) is { } denied) return denied;
            if (inspect && await AuthorizeAsync(http, http, options.ReplyPolicy!) is { } noReply) return noReply;
            // Validate pagination cursors before any instance lookup or store query.
            if (globalWaits || tokenWaits && new Query(http).Bool("tokens"))
                _ = WaitBoundary(new Query(http).Text("after"));
            if (http.Request.Headers.TryGetValue("X-IxIFlow-Actor", out var header))
            {
                if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0])) throw new ArgumentException("Invalid actor");
                if (options.ActForPolicy == null) return Results.Problem(statusCode: 403, title: "Actor delegation is not allowed");
                if (await AuthorizeAsync(http, http, options.ActForPolicy) is { } noActor) return noActor;
                http.Items[ActorKey] = header[0];
            }
            if (instance)
            {
                var id = Route(http, "id");
                var summary = await Operations(http).GetAsync(id, http.RequestAborted);
                if (summary == null) return await Client(http).GetStatusAsync(id) == WorkflowExecutionStatus.Removed ? Removed() : Unknown();
                if (await AuthorizeAsync(http, summary, effective) is { } noInstance) return noInstance;
                if (inspect && await AuthorizeAsync(http, summary, options.ReplyPolicy!) is { } noInstanceReply) return noInstanceReply;
                http.Items[typeof(InstanceSummary)] = summary;
            }
            return await next(invocation);
        });
    }

    private static async Task<IResult> MutateAsync(HttpContext http, string id, WorkflowOperationsOptions options,
        Func<Task<WorkflowOperation>> submit)
    {
        var started = Stopwatch.GetTimestamp();
        var operation = await submit(); // No 202 until the decision is committed.
        var remaining = options.MutationWait - Stopwatch.GetElapsedTime(started);
        if (!operation.Completion.IsCompleted && remaining > TimeSpan.Zero)
        {
            try { return Results.Ok(await operation.Completion.WaitAsync(remaining, http.RequestAborted)); }
            catch (TimeoutException) { }
        }
        if (operation.Completion.IsCompleted) return Results.Ok(await operation.Completion);
        var path = http.Request.Path.Value!;
        var location = http.Request.PathBase + path[..path.LastIndexOf('/')];
        return Results.Accepted(location, new { instanceId = id });
    }

    private static async Task<IResult> WaitsAsync(HttpContext http, WaitQuery scoped)
    {
        var limit = scoped.Limit;
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(scoped.Limit));
        var waits = await Client(http).GetOpenWaitsAsync(scoped with { Limit = limit + 1 });
        if (waits.IsRemoved) return Removed();
        return Results.Ok(new WorkflowWaitPage(waits.Take(limit).Select(w => new WorkflowOpenWaitResponse(w.InstanceId,
            WaitReference.Of(w.InstanceId, w.WaitId), w.Kind, w.Key, w.EventType, w.EventSchema, w.DeadlineUtc,
            w.OpenedAtUtc, w.Token, w.DefaultPayloadJson)).ToArray(), waits.Count > limit
                ? WaitPageCursor.Create(WaitReference.Of(waits[limit - 1].InstanceId, waits[limit - 1].WaitId)) : null));
    }

    private static async Task<IResult> UnschedulableAsync(HttpContext http, string policy)
    {
        var query = new Query(http);
        var limit = query.Limit();
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("IxIFlow.Operations.UnschedulableCursor.v1");
        UnschedulableCursor? cursor = null;
        if (query.Text("cursor") is { } token)
        {
            if (token.Length > 4096) throw new ArgumentException("Invalid unschedulable cursor");
            try { cursor = JsonSerializer.Deserialize<UnschedulableCursor>(protector.Unprotect(token)); }
            catch (CryptographicException error) { throw new ArgumentException("Invalid unschedulable cursor", error); }
            if (cursor == null || cursor.RunnableAtUtc == default || cursor.RunnableAtUtc.Kind != DateTimeKind.Utc ||
                string.IsNullOrWhiteSpace(cursor.InstanceId) || cursor.InstanceId.Length > 400)
                throw new ArgumentException("Invalid unschedulable cursor");
        }
        var operations = Operations(http);
        var scope = http.RequestServices.GetRequiredService<IInstanceQueryScope>();
        var items = new List<UnschedulableInstance>();
        DateTime now;
        // Bound both store paging and per-resource scope/authorization work, even if all rows
        // are hidden. Protect the continuation because it may name an excluded candidate.
        var examined = 0;
        var budget = Math.Max(100, limit + 1);
        var more = false;
        do
        {
            var page = await operations.GetUnschedulableAsync(Math.Min(100, budget - examined), cursor, http.RequestAborted);
            now = page.StoreTimeUtc;
            more = false;
            foreach (var item in page.Items)
            {
                examined++;
                var boundary = new UnschedulableCursor(item.RunnableAtUtc, item.InstanceId);
                var scoped = await scope.ScopeAsync(http.User, new InstanceQuery(InstanceId: item.InstanceId, Limit: 1), http.RequestAborted);
                var summary = (await operations.SearchAsync(scoped, http.RequestAborted)).Items
                    .SingleOrDefault(row => row.InstanceId == item.InstanceId);
                if (summary == null || summary.Incarnation != item.Incarnation || await AuthorizeAsync(http, summary, policy) != null)
                {
                    cursor = boundary;
                    continue;
                }
                // Leave the extra visible candidate for the next request.
                if (items.Count == limit) { more = true; break; }
                items.Add(item);
                cursor = boundary;
            }
            if (more) break;
            more = page.NextCursor != null;
        } while (examined < budget && more);
        var next = more && cursor != null ? protector.Protect(JsonSerializer.Serialize(cursor)) : null;
        return Results.Ok(new WorkflowUnschedulablePage(items, next, now));
    }
    private static string? WaitBoundary(string? token)
    {
        if (token == null) return null;
        if (!WaitPageCursor.TryRead(token, out var waitRef))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown wait cursor");
        return waitRef;
    }
    private static IWorkflowOperations Operations(HttpContext http) => http.RequestServices.GetRequiredService<IWorkflowOperations>();
    private static IWorkflowClient Client(HttpContext http) => http.RequestServices.GetRequiredService<IWorkflowClient>();
    private static string Route(HttpContext http, string name) => http.Request.RouteValues[name]!.ToString()!;
    private static string Actor(HttpContext http)
    {
        var actor = http.Items[ActorKey] as string ?? http.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An actor name is required");
        return actor;
    }
    private static async Task<T> BodyAsync<T>(HttpContext http) => await JsonSerializer.DeserializeAsync<T>(http.Request.Body, JsonOptions, http.RequestAborted)
        ?? throw new ArgumentException("A body is required");
    private static IResult Unknown() => Results.Problem(statusCode: 404, title: "Unknown workflow resource");
    private static IResult Removed() => Results.Problem(statusCode: 410, title: "Workflow removed");
    private static IResult Conflict() => Results.Problem(statusCode: 409, title: "Invalid workflow state");
    private sealed record ReasonRequest(string ReasonCode, string? Description);
    private sealed record ResolutionRequest(string InvocationId, string Kind, Dictionary<string, object?>? Outputs, string Note, string? ErrorMessage, JsonElement? InvocationResult);
    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        System.Enum.TryParse<T>(value, true, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("Invalid enum value");
    private sealed class Query(HttpContext http)
    {
        public string? Text(string name)
        {
            if (!http.Request.Query.TryGetValue(name, out var value)) return null;
            if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0])) throw new ArgumentException("Invalid query value");
            return value[0];
        }
        public int? Int(string name) => Text(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
        public long? Long(string name) => Text(name) is { } value ? long.Parse(value, CultureInfo.InvariantCulture) : null;
        public bool Bool(string name) => Text(name) is { } value && bool.Parse(value);
        public T? Enum<T>(string name) where T : struct, Enum => Text(name) is { } value ? ParseEnum<T>(value) : null;
        public DateTime? Date(string name) => Text(name) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime : null;
        public int Limit()
        {
            var limit = Int("limit") ?? 100;
            if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            return limit;
        }
    }
}
