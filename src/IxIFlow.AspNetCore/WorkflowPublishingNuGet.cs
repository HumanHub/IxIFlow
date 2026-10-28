using IxIFlow.Core;
using Microsoft.AspNetCore.Http;
using static IxIFlow.AspNetCore.WorkflowPublishingHttp;

namespace IxIFlow.AspNetCore;

internal static class WorkflowPublishingNuGet
{
    internal static async Task<IResult> IndexAsync(HttpContext http, WorkflowPublishingOptions options)
    {
        if (await WorkflowHttp.AuthorizeAsync(http, http, options.DownloadPolicy!) is { } denied) return denied;
        var path = http.Request.Path.Value!;
        var root = http.Request.Scheme + "://" + http.Request.Host + http.Request.PathBase + path[..^"index.json".Length];
        return Json(new NuGetServiceIndexResponse("3.0.0", [new(root + "flatcontainer/", "PackageBaseAddress/3.0.0")]));
    }

    internal static async Task<IResult> VersionsAsync(HttpContext http, WorkflowPublishingOptions options)
    {
        var id = Route(http, "id");
        if (!Scope(http).Allows("package", id)) return Missing();
        if (await WorkflowHttp.AuthorizeAsync(http, http, options.DownloadPolicy!) is { } denied) return denied;
        var versions = new List<string>(); string? cursor = null;
        do
        {
            http.RequestAborted.ThrowIfCancellationRequested();
            var page = await Store(http).ListPackageVersionsAsync(id, new(Scope(http), cursor, 100));
            foreach (var row in page.Items.Where(row => row.ArtifactPresent))
            {
                var detail = await Store(http).GetPackageDetailAsync(row.Id, row.Version, Scope(http));
                if (detail?.Summary.ArtifactPresent == true && await WorkflowHttp.AuthorizeAsync(http, detail, options.DownloadPolicy!) == null)
                    versions.Add(row.Version);
            }
            cursor = page.NextCursor;
        } while (cursor != null);
        // This is the NuGet content index, so retained withdrawn bytes remain discoverable.
        return Json(new NuGetVersionsResponse(versions.OrderBy(value => NuGet.Versioning.NuGetVersion.Parse(value)).ToArray()));
    }

    internal static async Task<IResult> FileAsync(HttpContext http, WorkflowPublishingOptions options)
    {
        var id = Route(http, "id"); var version = Route(http, "version"); var file = Route(http, "file");
        if (!Scope(http).Allows("package", id)) return Missing();
        try
        {
            if (WorkflowPackageRules.NormalizeId(id) != id || WorkflowPackageRules.NormalizeVersion(version) != version) return Missing();
        }
        catch (WorkflowEngineException) { return Missing(); }
        var nupkg = file == id + "." + version + ".nupkg";
        if (!nupkg && file != id + ".nuspec") return Missing();
        var detail = await Store(http).GetPackageDetailAsync(id, version, Scope(http));
        if (detail == null) return Missing();
        if (await WorkflowHttp.AuthorizeAsync(http, detail, options.DownloadPolicy!) is { } denied) return denied;
        return nupkg ? await WorkflowPublishingDownloads.PackageAsync(http, detail)
            : await WorkflowPublishingDownloads.PackageEntryAsync(http, detail, "*.nuspec", "application/xml");
    }
}
