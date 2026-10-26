using System.Security.Claims;
using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

public sealed class WorkflowPublishingOptions
{
    public string? ReadPolicy { get; set; }
    public string? AuthorPolicy { get; set; }
    public string? ExecutePolicy { get; set; }
    public string? PreviewPolicy { get; set; }
    public string? PublishPolicy { get; set; }
    public string? LifecyclePolicy { get; set; }
    public string? DownloadPolicy { get; set; }
    public string? ActForPolicy { get; set; }
    public TimeSpan PublishWait { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxDraftBytes { get; set; } = 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 2;
}

/// <summary>Application scope applied before catalog queries and proposed-resource mutations.</summary>
public interface ICatalogQueryScope
{
    ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
