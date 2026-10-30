using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IxIFlow.AspNetCore;

/// <summary>Bounded upload admission and authorization of proposed and existing packages.</summary>
public sealed class WorkflowPackageUploadService : IDisposable
{
    private readonly WorkflowPublisher _publisher;
    private readonly WorkflowPublisherOptions _publisherOptions;
    private readonly WorkflowPublishingOptions _options;
    private readonly ILogger<WorkflowPackageUploadService> _logger;
    private readonly SemaphoreSlim _admission;

    public WorkflowPackageUploadService(WorkflowPublisher publisher, WorkflowPublisherOptions publisherOptions,
        WorkflowPublishingOptions options, ILogger<WorkflowPackageUploadService> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentUploads, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PublishPolicy);
        _publisher = publisher;
        _publisherOptions = publisherOptions;
        _options = options;
        _logger = logger;
        _admission = new(options.MaxConcurrentUploads, options.MaxConcurrentUploads);
    }

    /// <param name="catalogPath">The mapped group's package catalog path, including any route prefix.</param>
    /// <param name="http">The current HTTP request context.</param>
    public async Task<IResult> UploadAsync(HttpContext http, PathString catalogPath)
    {
        if (await WorkflowHttp.AuthorizeAsync(http, http, _options.PublishPolicy!) is { } endpointDenied)
            return endpointDenied;

        if (!await _admission.WaitAsync(0, http.RequestAborted))
        {
            http.Response.Headers.RetryAfter = "5";

            return Results.Problem(statusCode: 429, title: "Package upload capacity is busy");
        }

        try
        {
            if (http.Request.ContentLength > _publisherOptions.PackageReadLimits.MaxArchiveBytes)
                return Results.Problem(statusCode: 413, title: "Package exceeds the configured limit",
                    extensions: new Dictionary<string, object?> { ["code"] = "PUB042", ["class"] = "Limit" });

            // The library counts raw bytes even when Content-Length is absent. Archive entry,
            // expanded-byte and resolver-closure limits apply independently. Admission covers
            // compressed buffers, expanded entries and verification/store copies for the full call.
            var candidate = await _publisher.ReadPackageAsync(http.Request.Body, http.RequestAborted);
            var scope = await http.RequestServices.GetRequiredService<ICatalogQueryScope>().ScopeAsync(http.User, http.RequestAborted);

            if (!scope.Allows("package", candidate.Package.Id))
                return Results.NotFound();

            if (await WorkflowHttp.AuthorizeAsync(http, candidate.Package, _options.PublishPolicy!) is { } denied)
                return denied;

            var existing = await _publisher.Catalog.GetPackageAsync(candidate.Package.Id, candidate.Package.Version,
                cancellationToken: http.RequestAborted);

            if (existing != null && await WorkflowHttp.AuthorizeAsync(http, existing, _options.PublishPolicy!) is { } existingDenied)
                return existingDenied;

            var actor = http.User.Identity?.Name;

            if (http.Request.Headers.TryGetValue("X-IxIFlow-Actor", out var header))
            {
                if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
                    throw new ArgumentException("Invalid actor");

                if (_options.ActForPolicy == null)
                    return Results.Problem(statusCode: 403, title: "Actor delegation is not allowed");

                if (await WorkflowHttp.AuthorizeAsync(http, http, _options.ActForPolicy) is { } noActor)
                    return noActor;

                actor = header[0];
            }

            var receipt = await _publisher.StorePackageAsync(candidate, actor, http.RequestAborted);
            var package = receipt.Stored;

            var detail = await http.RequestServices.GetRequiredService<IWorkflowCatalogStore>()
                    .GetPackageDetailAsync(package.Id, package.Version, scope, cancellationToken: http.RequestAborted)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "The accepted package is unavailable");

            var response = new WorkflowPackageResponse(PublishingResponses.Project(detail), receipt.Created);

            var location = http.Request.PathBase.Add(catalogPath).ToString().TrimEnd('/') + "/" +
                Uri.EscapeDataString(package.Id) + "/" + Uri.EscapeDataString(package.Version);

            http.Response.Headers.Location = location;

            return WorkflowPublishingHttp.Json(response, receipt.Created ? 201 : 200);
        }
        catch (WorkflowEngineException error)
        {
            var status = error.Kind switch
            {
                WorkflowErrorKind.InvalidRequest => 400,
                WorkflowErrorKind.InvalidDefinition => 422,
                WorkflowErrorKind.InvalidState => 409,
                WorkflowErrorKind.ResourceExhausted => 413,
                _ => 503
            };

            _logger.LogWarning(error, "Package upload failed");
            var diagnostic = WorkflowPublishFailures.Classify(error);

            return Results.Problem(statusCode: status, title: diagnostic.Class == WorkflowPublishDiagnosticClass.Authoring
                ? diagnostic.Message
                : status switch
                {
                    400 => "Invalid package upload", 409 => "Package identity is immutable",
                    413 => "Package exceeds the configured limit", _ => "Package service unavailable"
                }, extensions: new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["class"] = diagnostic.Class.ToString() });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid package upload");
        }
        catch (WorkflowPublishException error)
        {
            _logger.LogWarning(error, "Package extraction failed");

            var status = WorkflowPublishFailures.ClassFor(error.Code) switch
            {
                WorkflowPublishDiagnosticClass.Authoring => 422,
                WorkflowPublishDiagnosticClass.Conflict => 409,
                WorkflowPublishDiagnosticClass.Unavailable => 503,
                WorkflowPublishDiagnosticClass.Limit => 413,
                _ => 500
            };

            return Results.Problem(statusCode: status, title: error.Message, extensions: new Dictionary<string, object?>
                { ["code"] = error.Code, ["class"] = WorkflowPublishFailures.ClassFor(error.Code).ToString() });
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(error, "Unexpected package upload failure");
            var diagnostic = WorkflowPublishFailures.Classify(error);

            return Results.Problem(statusCode: diagnostic.Class == WorkflowPublishDiagnosticClass.Unavailable ? 503 : 500,
                title: diagnostic.Message,
                extensions: new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["class"] = diagnostic.Class.ToString() });
        }
        finally
        {
            _admission.Release();
        }
    }

    public void Dispose() => _admission.Dispose();
}

public static class WorkflowPublishingServiceExtensions
{
    /// <summary>Registers publishing and HTTP upload orchestration. Route mapping is separate.</summary>
    public static IIxIFlowBuilder AddWorkflowPublishing(this IIxIFlowBuilder builder, WorkflowPublishingOptions options,
        WorkflowPublisherOptions? publisherOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        builder.AddPublishing(publisherOptions);
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<WorkflowPackageUploadService>();
        builder.Services.ConfigureAll<Microsoft.AspNetCore.OpenApi.OpenApiOptions>(WorkflowPublishingOpenApi.Configure);

        return builder;
    }
}