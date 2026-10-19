using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace IxIFlow.AspNetCore;

/// <summary>Trusted request context available to a typed body mapping.</summary>
public sealed class WorkflowTriggerContext
{
    private readonly Dictionary<string, string?> _route;
    private readonly Dictionary<string, string?> _query;
    internal WorkflowTriggerContext(HttpContext http, CloudEventEnvelope? cloudEvent)
    {
        CloudEvent = cloudEvent;
        _route = http.Request.RouteValues.ToDictionary(p => p.Key, p => p.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        _query = http.Request.Query.ToDictionary(p => p.Key, p => p.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
    }
    public CloudEventEnvelope? CloudEvent { get; }
    public string? Route(string name) => _route.GetValueOrDefault(name);
    public string? Query(string name) => _query.GetValueOrDefault(name);
}

public sealed record WorkflowStatusResponse(string Id, string Status, object? Result = null);

internal sealed class TriggerRegistration(string id, string pattern, string method)
{
    public string Id { get; } = id;
    public string Pattern { get; } = pattern;
    public string Method { get; } = method;
    public string? Policy { get; set; }
    public string? InstancePolicy { get; set; }
    public int BodyLimit { get; set; } = 1024 * 1024;
    public string? SignedSender { get; set; }
    public byte[][] Secrets { get; set; } = [];
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan? SyncWait { get; set; }
    public Func<HttpContext, (string Owner, string? Tenant)>? Ownership { get; set; }
    public Func<WorkflowExecutionResult, object?> Projection { get; set; } = _ => null;
    public Func<HttpContext, ParsedBody, WorkflowAdmission, Task<WorkflowAdmissionRecord>>? Start { get; set; }
    public Dictionary<string, Func<JsonElement, object>> EventTypes { get; } = new(StringComparer.Ordinal);
    public WorkflowDefinition? Definition { get; init; }
    public bool Status { get; init; }
    public bool Events { get; init; }
}

public abstract class WorkflowEndpointBuilder<TSelf> where TSelf : WorkflowEndpointBuilder<TSelf>
{
    internal TriggerRegistration Registration { get; }
    internal WorkflowEndpointBuilder(TriggerRegistration registration) => Registration = registration;

    public TSelf Authorize(string policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        Registration.Policy = policy;
        return (TSelf)this;
    }

    /// <summary>The resource passed to this ASP.NET policy is WorkflowOwnership.</summary>
    public TSelf AuthorizeInstance(string policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        Registration.InstancePolicy = policy;
        return (TSelf)this;
    }

    public TSelf VerifyStandardWebhook(string sender, params string[] secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        if (sender.Length > 128 || sender != sender.Trim() || secrets.Length == 0)
            throw new ArgumentException("A sender and at least one signing secret are required");
        Registration.SignedSender = sender;
        Registration.Secrets = secrets.Select(StandardWebhookSignature.DecodeSecret).ToArray();
        return (TSelf)this;
    }

    public TSelf TimestampTolerance(TimeSpan tolerance)
    {
        if (tolerance <= TimeSpan.Zero || tolerance > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        Registration.Tolerance = tolerance;
        return (TSelf)this;
    }

    public TSelf MaxBodyBytes(int bytes)
    {
        if (bytes <= 0 || bytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytes));
        Registration.BodyLimit = bytes;
        return (TSelf)this;
    }
}

public sealed class WorkflowStartTriggerBuilder<TData> : WorkflowEndpointBuilder<WorkflowStartTriggerBuilder<TData>> where TData : class
{
    private readonly WorkflowDefinition _definition;
    internal WorkflowStartTriggerBuilder(TriggerRegistration registration, WorkflowDefinition definition) : base(registration) =>
        _definition = definition;

    public WorkflowStartTriggerBuilder<TData> FromBody<TBody>(Func<TBody, WorkflowTriggerContext, TData> map) where TBody : class
    {
        ArgumentNullException.ThrowIfNull(map);
        Registration.Start = (http, parsed, admission) =>
        {
            var body = parsed.Data.Deserialize<TBody>(WorkflowTriggerExtensions.JsonOptions)
                ?? throw new ArgumentException("A request body is required");
            var data = map(body, new(http, parsed.Envelope)) ?? throw new ArgumentException("Body mapping returned null");
            return http.RequestServices.GetRequiredService<IWorkflowIngressClient>()
                .AdmitAsync(_definition, data, admission, http.RequestAborted);
        };
        return this;
    }

    public WorkflowStartTriggerBuilder<TData> OwnedBy(Func<HttpContext, (string Owner, string? Tenant)> ownership)
    {
        Registration.Ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        return this;
    }

    public WorkflowStartTriggerBuilder<TData> IdempotencyRetention(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero || retention > CheckpointCommit.MaxDuration) throw new ArgumentOutOfRangeException(nameof(retention));
        Registration.Retention = retention;
        return this;
    }

    public WorkflowStartTriggerBuilder<TData> Synchronous(TimeSpan maximumWait)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(maximumWait));
        Registration.SyncWait = maximumWait;
        return this;
    }

    public WorkflowStartTriggerBuilder<TData> ProjectResult<TPublic>(Func<TData, TPublic> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        Registration.Projection = result => result.Status == WorkflowExecutionStatus.Success && result.WorkflowData is TData data
            ? projection(data) : null;
        return this;
    }
}

public sealed class WorkflowEventTriggerBuilder : WorkflowEndpointBuilder<WorkflowEventTriggerBuilder>
{
    internal WorkflowEventTriggerBuilder(TriggerRegistration registration) : base(registration) { }

    /// <summary>How long signed delivery receipts are retained; defaults to 24 hours.</summary>
    public WorkflowEventTriggerBuilder DeliveryRetention(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero || retention > CheckpointCommit.MaxDuration) throw new ArgumentOutOfRangeException(nameof(retention));
        Registration.Retention = retention;
        return this;
    }

    public WorkflowEventTriggerBuilder Allow<TEvent>(string type) where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!Registration.EventTypes.TryAdd(type, data => data.Deserialize<TEvent>(WorkflowTriggerExtensions.JsonOptions)
            ?? throw new ArgumentException("Event data is required")))
            throw new InvalidOperationException("A CloudEvents type may only be mapped once");
        return this;
    }
}

public sealed class WorkflowStatusEndpointBuilder : WorkflowEndpointBuilder<WorkflowStatusEndpointBuilder>
{
    internal WorkflowStatusEndpointBuilder(TriggerRegistration registration) : base(registration) { }
}

public sealed class WorkflowTriggerBuilder
{
    internal List<TriggerRegistration> Registrations { get; } = [];

    public WorkflowStartTriggerBuilder<TData> Start<TData>(string identity, string pattern, WorkflowDefinition definition) where TData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.WorkflowDataType != typeof(TData))
            throw new ArgumentException("The trigger data type must match its workflow definition");
        var registration = new TriggerRegistration(identity, pattern, "POST") { Definition = definition };
        Registrations.Add(registration);
        return new(registration, definition);
    }

    public WorkflowEventTriggerBuilder Events(string identity, string pattern = "/workflows/{id}/events/{key}")
    {
        var registration = new TriggerRegistration(identity, pattern, "POST") { Events = true };
        Registrations.Add(registration);
        return new(registration);
    }

    public WorkflowStatusEndpointBuilder Status()
    {
        var registration = new TriggerRegistration("ixiflow.status", "/workflows/{id}", "GET") { Status = true };
        Registrations.Add(registration);
        return new(registration);
    }
}
