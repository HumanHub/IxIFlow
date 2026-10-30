using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IxIFlow.AspNetCore.WorkflowPublishingHttp;

namespace IxIFlow.AspNetCore;

internal static class WorkflowPublishingDownloads
{
    internal static Task<IResult> WorkflowAsync(HttpContext http, CatalogWorkflowVersionDetail detail, bool source) =>
        detail.Summary.ArtifactDigest is { } digest
            ? VerifiedAsync(http, digest, package: false, source ? WorkflowBundleArchive.YamlPath : null)
            : Task.FromResult(Missing());

    internal static Task<IResult> PackageAsync(HttpContext http, CatalogPackageDetail detail) =>
        VerifiedAsync(http, detail.Summary.Digest, package: true, entry: null);

    internal static Task<IResult> PackageEntryAsync(HttpContext http, CatalogPackageDetail detail, string entry,
        string media = "application/yaml") =>
        VerifiedAsync(http, detail.Summary.Digest, package: true, entry, media);

    private static async Task<IResult> VerifiedAsync(HttpContext http, ArtifactDigest digest, bool package, string? entry,
        string media = "application/yaml")
    {
        FileStream? verified = null;

        try
        {
            var limits = package
                ? http.RequestServices.GetRequiredService<WorkflowPublisherOptions>().PackageReadLimits
                : http.RequestServices.GetService<WorkflowBundleOptions>()?.ReadLimits ?? new WorkflowBundleReadLimits();

            await using var original =
                await http.RequestServices.GetRequiredService<IArtifactStore>().OpenAsync(digest, http.RequestAborted)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Artifact unavailable");

            verified = new FileStream(Path.Combine(Path.GetTempPath(), "ixiflow-download-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            await WorkflowBundleArchive.CopyArchiveAsync(original, verified, digest, limits, http.RequestAborted);

            // Validate expanded sizes and entries too. No response body or successful headers are sent yet.
            var entries = package
                ? await WorkflowPackageArchive.ReadVerifiedAsync(verified, limits, http.RequestAborted)
                : (await WorkflowBundleArchive.ReadVerifiedAsync(verified, limits, http.RequestAborted)).Entries;

            if (entry == "*.nuspec")
            {
                var paths = entries.Keys.Where(path => !path.Contains('/') && path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (paths.Length != 1)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Package nuspec is missing or ambiguous");

                entry = paths[0];
            }

            if (entry != null && !entries.ContainsKey(entry))
                return Missing();

            var tag = "\"" + digest.Value + "\"";
            http.Response.Headers.ETag = tag;
            http.Response.Headers.CacheControl = "private, no-cache";

            if (Matches(http, tag))
                return Results.StatusCode(304);

            if (entry != null)
                return Results.Bytes(entries[entry].ToArray(), media);

            verified.Position = 0;
            var result = Results.Stream(verified, package ? "application/octet-stream" : "application/zip", enableRangeProcessing: false);
            verified = null; // The response owns the verified, delete-on-close stream.

            return result;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            http.RequestServices.GetRequiredService<ILogger<WorkflowPublishingOptions>>()
                .LogWarning(error, "Artifact download verification failed for {Digest}", digest);

            http.Response.Headers.Remove("ETag");
            NoStore(http);

            return Problem(503, "The requested artifact is unavailable or corrupt", "PUB041", "Unavailable");
        }
        finally
        {
            if (verified != null)
                await verified.DisposeAsync();
        }
    }
}