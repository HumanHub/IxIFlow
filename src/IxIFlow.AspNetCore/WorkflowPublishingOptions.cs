using System.Security.Claims;
using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

/// <summary>Authorization policies and request limits for workflow publishing HTTP endpoints.</summary>
public sealed class WorkflowPublishingOptions
{
    /// <summary>Required ASP.NET authorization policy for catalog or operational reads.</summary>
    public string? ReadPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for creating and editing draft documents.</summary>
    public string? AuthorPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for validation that executes authored code.</summary>
    public string? ExecutePolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for rendering template previews.</summary>
    public string? PreviewPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for publishing definitions and uploading packages.</summary>
    public string? PublishPolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for publication lifecycle changes.</summary>
    public string? LifecyclePolicy { get; set; }

    /// <summary>Required ASP.NET authorization policy for downloading source and artifacts.</summary>
    public string? DownloadPolicy { get; set; }

    /// <summary>ASP.NET authorization policy required to supply an actor on behalf of another principal.</summary>
    public string? ActForPolicy { get; set; }

    /// <summary>Maximum initial wait for a publication result; defaults to ten seconds.</summary>
    public TimeSpan PublishWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum UTF-8 source bytes per draft; defaults to one MiB.</summary>
    public int MaxDraftBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum admitted concurrent package uploads; defaults to two.</summary>
    public int MaxConcurrentUploads { get; set; } = 2;
}

/// <summary>Application scope applied before catalog queries and proposed-resource mutations.</summary>
public interface ICatalogQueryScope
{
    ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}