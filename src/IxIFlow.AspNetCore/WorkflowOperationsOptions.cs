using System.Security.Claims;
using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

public sealed class WorkflowOperationsOptions
{
    public string? ReadPolicy { get; set; }
    public string? ReplyPolicy { get; set; }
    public string? InspectPolicy { get; set; }
    public string? WritePolicy { get; set; }
    public string? OutboxPolicy { get; set; }
    public string? ActForPolicy { get; set; }
    public TimeSpan MutationWait { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Application authorization applied in the store query, before paging. Register explicitly, even for unrestricted operators.</summary>
public interface IInstanceQueryScope
{
    ValueTask<InstanceQuery> ScopeAsync(ClaimsPrincipal user, InstanceQuery query, CancellationToken cancellationToken = default);
    ValueTask<WaitQuery> ScopeAsync(ClaimsPrincipal user, WaitQuery query, CancellationToken cancellationToken = default);
}
