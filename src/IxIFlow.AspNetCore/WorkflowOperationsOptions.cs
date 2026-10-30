using System.Security.Claims;
using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

/// <summary>Authorization policies, body limits and mutation waits for workflow operations HTTP endpoints.</summary>
public sealed class WorkflowOperationsOptions
{
    /// <summary>Required ASP.NET authorization policy for catalog or operational reads.</summary>
    public string? ReadPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for reading waits that contain reply capabilities.</summary>
    public string? ReplyPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for inspecting saved workflow data.</summary>
    public string? InspectPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for workflow mutations.</summary>
    public string? WritePolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for outbox and publisher administration.</summary>
    public string? OutboxPolicy { get; set; }

    /// <summary>ASP.NET authorization policy required to supply an actor on behalf of another principal.</summary>
    public string? ActForPolicy { get; set; }

    /// <summary>Maximum bytes in any operations request body; defaults to one MiB.</summary>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum initial wait for an accepted operation; defaults to ten seconds.</summary>
    public TimeSpan MutationWait { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Application authorization applied in the store query, before paging. Register explicitly, even for unrestricted operators.</summary>
public interface IInstanceQueryScope
{
    ValueTask<InstanceQuery> ScopeAsync(ClaimsPrincipal user, InstanceQuery query, CancellationToken cancellationToken = default);
    ValueTask<WaitQuery> ScopeAsync(ClaimsPrincipal user, WaitQuery query, CancellationToken cancellationToken = default);
}