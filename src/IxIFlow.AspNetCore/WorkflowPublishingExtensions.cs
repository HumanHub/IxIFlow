using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.AspNetCore.WorkflowPublishingHttp;

namespace IxIFlow.AspNetCore;

/// <summary>Maps authorized workflow publishing and catalog endpoints.</summary>
public static class WorkflowPublishingExtensions
{
    /// <summary>Maps the publishing resource model. Requires AddWorkflowPublishing and an application catalog scope.</summary>
    public static RouteGroupBuilder MapWorkflowPublishing(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var services = endpoints.ServiceProvider;

        var options = services.GetService<WorkflowPublishingOptions>() ??
            throw Configuration("Register AddWorkflowPublishing before mapping publishing");

        ValidateStructure();
        services.GetRequiredService<WorkflowHost>().RegisterStartupValidation(Validate);
        var group = endpoints.MapGroup(prefix);
        var publicationEndpoint = "ixiflow-publication-" + Guid.NewGuid().ToString("N");
        var packageCatalogEndpoint = "ixiflow-package-catalog-" + Guid.NewGuid().ToString("N");
        group.AddEndpointFilter(new Filter(options));

        Map("GET", "/drafts", options.ReadPolicy!, async http =>
        {
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var query = new WorkflowDraftQuery(Scope(http), Query(http, "kind"), Query(http, "namePrefix"), Query(http, "after"),
                Limit(http));

            var page = await Store(http).GetDefinitionsAsync(query, cancellationToken: http.RequestAborted);

            return Cached(http, page, page.CatalogRevision, query);
        }, typeof(CatalogPage<WorkflowDraftSummary>), queries: ["kind", "namePrefix", "after", "limit"]);

        Map("GET", "/drafts/{kind}/{name}/{version}", options.ReadPolicy!, async http =>
        {
            var key = DraftKey(http);

            if (!Scope(http).Allows(key.Kind, key.Name))
                return Missing();

            var draft = await Store(http).GetDefinitionAsync(key.Kind, key.Name, key.Version, cancellationToken: http.RequestAborted);

            if (draft == null)
                return Missing();

            if (await Allow(http, Summary(draft), options.ReadPolicy!) is { } denied)
                return denied;

            http.Response.Headers.ETag = DraftTag(draft.Id);

            return Json(await DraftResponseAsync(http, draft));
        }, typeof(WorkflowDraftResponse));

        Map("GET", "/drafts/{kind}/{name}/{version}/source", options.DownloadPolicy!, async http =>
        {
            var key = DraftKey(http);

            if (!Scope(http).Allows(key.Kind, key.Name))
                return Missing();

            var draft = await Store(http).GetDefinitionAsync(key.Kind, key.Name, key.Version, cancellationToken: http.RequestAborted);

            if (draft == null)
                return Missing();

            if (await Allow(http, Summary(draft), options.DownloadPolicy!) is { } denied)
                return denied;

            var tag = DraftTag(draft.Id);
            http.Response.Headers.ETag = tag;
            http.Response.Headers.CacheControl = "private, no-cache";

            return Matches(http, tag) ? Results.StatusCode(304) : Results.Text(draft.Source, "application/yaml", System.Text.Encoding.UTF8);
        }, typeof(string));

        Map("PUT", "/drafts/{kind}/{name}/{version}", options.AuthorPolicy!, async http =>
        {
            var expected = DraftPrecondition(http, createAllowed: true);
            var key = DraftKey(http);

            if (!Scope(http).Allows(key.Kind, key.Name))
                return Missing();

            var source = (await SourceAsync(http, options.MaxDraftBytes))!;
            Identity(source, key.Kind, key.Name, key.Version);
            var proposed = new WorkflowDraft(key.Name, key.Version, key.Kind, source);

            // Proposed-resource authorization precedes the first catalog mutation. Updates also authorize the actual record.
            if (await Allow(http, proposed, options.AuthorPolicy!) is { } denied)
                return denied;

            var current = await Store(http).GetDefinitionAsync(key.Kind, key.Name, key.Version, cancellationToken: http.RequestAborted);

            if (current != null && await Allow(http, Summary(current), options.AuthorPolicy!) is { } actualDenied)
                return actualDenied;

            var saved = await Store(http).SaveDefinitionAsync(proposed, expected, Actor(http), cancellationToken: http.RequestAborted);
            http.Response.Headers.ETag = DraftTag(saved.Id);
            http.Response.Headers.Location = (http.Request.PathBase + http.Request.Path).ToString();

            return Json(await DraftResponseAsync(http, saved), expected == null ? 201 : 200);
        }, typeof(WorkflowDraftResponse), body: "application/yaml", precondition: "draft-create-or-update");

        Map("DELETE", "/drafts/{kind}/{name}/{version}", options.AuthorPolicy!, async http =>
        {
            var expected = DraftPrecondition(http, createAllowed: false)!;

            if (!Scope(http).Allows(expected.Kind, expected.Name))
                return Missing();

            var current = await Store(http)
                .GetDefinitionAsync(expected.Kind, expected.Name, expected.Version, cancellationToken: http.RequestAborted);

            if (current == null)
                return Missing();

            if (await Allow(http, Summary(current), options.AuthorPolicy!) is { } denied)
                return denied;

            return await Store(http).DeleteDefinitionAsync(expected, cancellationToken: http.RequestAborted)
                ? Results.NoContent()
                : Missing();
        }, precondition: "draft");

        Map("POST", "/drafts/{kind}/{name}/{version}/validate", options.ExecutePolicy!, async http =>
        {
            var key = DraftKey(http);

            if (!Scope(http).Allows(key.Kind, key.Name))
                return Missing();

            var source = await SourceAsync(http, options.MaxDraftBytes, optional: true);
            WorkflowDraft? draft;

            if (source == null)
                draft = await Store(http).GetDefinitionAsync(key.Kind, key.Name, key.Version, cancellationToken: http.RequestAborted);
            else
                draft = new(key.Name, key.Version, key.Kind, source);

            if (draft == null)
                return Missing();

            if (await Allow(http, draft, options.ExecutePolicy!) is { } denied)
                return denied;

            var report = await http.RequestServices.GetRequiredService<WorkflowPublisher>().ValidateAsync(draft, http.RequestAborted);

            return Json(new WorkflowValidationResponse(report.Kind, report.IsValid,
                report.Diagnostics.Select(PublishingResponses.Project).ToArray()));
        }, typeof(WorkflowValidationResponse), body: "application/yaml");

        Map("POST", "/drafts/{kind}/{name}/{version}/publish", options.PublishPolicy!, async http =>
        {
            var expected = DraftPrecondition(http, createAllowed: false)!;

            if (!Scope(http).Allows(expected.Kind, expected.Name) ||
                expected.Kind == WorkflowDraftKinds.Activity && !Scope(http).Allows("package", expected.Name))
                return Missing();

            var store = Store(http);

            var draft = await store.GetDefinitionAsync(expected.Kind, expected.Name, expected.Version,
                cancellationToken: http.RequestAborted);

            object? resource = draft == null ? null : Summary(draft);

            // A lost-response retry authorizes the exact immutable receipt even after draft consumption.
            if (resource == null)
                resource = (await http.RequestServices.GetRequiredService<WorkflowPublisher>().FindPublishedAsync(expected))?.Receipt;

            if (resource == null)
                return Missing();

            if (await Allow(http, resource, options.PublishPolicy!) is { } denied)
                return denied;

            object? existing = expected.Kind switch
            {
                WorkflowDraftKinds.Workflow => await store.GetWorkflowVersionAsync(expected.Name, expected.Version,
                    cancellationToken: http.RequestAborted),
                WorkflowDraftKinds.Activity => await store.GetPackageAsync(expected.Name, PackageIndexRules.PublishedVersion(expected),
                    cancellationToken: http.RequestAborted),
                WorkflowDraftKinds.Template => await store.GetTemplateVersionAsync(expected.Name, expected.Version,
                    cancellationToken: http.RequestAborted),
                _ => null
            };

            if (existing != null && await Allow(http, existing, options.PublishPolicy!) is { } existingDenied)
                return existingDenied;

            var attempt = await http.RequestServices.GetRequiredService<PublicationService>()
                .PublishAsync(expected, options.PublishWait, Actor(http), http.RequestAborted);

            var location = http.RequestServices.GetRequiredService<LinkGenerator>()
                    .GetPathByName(http, publicationEndpoint, new { id = attempt.Id })
                ?? throw Configuration("The publication endpoint cannot be linked");

            http.Response.Headers.Location = location;

            var status = attempt.State == WorkflowPublicationState.Running ? 202 :
                attempt.Outcome?.FailureClass is { } failure ? Status(failure) : 200;

            if (status == 202)
                http.Response.Headers.RetryAfter = "5";

            return Json(await PublicationAsync(http, attempt), status);
        }, typeof(WorkflowPublicationResponse), precondition: "draft");

        Map("GET", "/drafts/{kind}/{name}/{version}/publications", options.ReadPolicy!, async http =>
        {
            var key = DraftKey(http);

            if (!Scope(http).Allows(key.Kind, key.Name))
                return Missing();

            var generationText = Query(http, "generation");

            Guid? generation = generationText == null ? null :
                Guid.TryParseExact(generationText, "D", out var parsed) && parsed != Guid.Empty ? parsed :
                throw new ArgumentException("Invalid generation");

            var page = await Store(http)
                .GetPublicationsAsync(new(Scope(http), key.Kind, key.Name, key.Version, generation, Query(http, "after"), Limit(http)),
                    cancellationToken: http.RequestAborted);

            var items = new List<WorkflowPublicationResponse>();

            // History authorization uses stored attempts and does not require a live draft.
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            foreach (var item in page.Items)
            {
                if (await Allow(http, item, options.ReadPolicy!) is { } itemDenied)
                    return itemDenied;

                items.Add(await PublicationAsync(http, item));
            }

            return Json(new CatalogPage<WorkflowPublicationResponse>(items, page.NextCursor, page.CatalogRevision));
        }, typeof(CatalogPage<WorkflowPublicationResponse>), queries: ["generation", "after", "limit"]);

        Map("GET", "/publications/{id}", options.ReadPolicy!, async http =>
        {
            if (!Guid.TryParseExact(Route(http, "id"), "D", out var id))
                throw new ArgumentException("Invalid publication ID");

            var attempt = await Store(http).GetPublicationAsync(id, Scope(http), cancellationToken: http.RequestAborted);

            if (attempt == null)
                return Missing();

            if (await Allow(http, attempt, options.ReadPolicy!) is { } denied)
                return denied;

            return Json(await PublicationAsync(http, attempt));
        }, typeof(WorkflowPublicationResponse)).WithName(publicationEndpoint);

        Map("GET", "/catalog/workflows", options.ReadPolicy!, async http =>
        {
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var query = new CatalogWorkflowQuery(Scope(http), CatalogActivityRules.Text(Query(http, "text")), Query(http, "tag"),
                Query(http, "category"), EnumQuery<WorkflowVersionState>(http, "state"), Query(http, "after"), Limit(http));

            var page = await Store(http).ListWorkflowsAsync(query, cancellationToken: http.RequestAborted);

            return Cached(http, page, page.CatalogRevision, query);
        }, typeof(CatalogPage<CatalogWorkflow>), queries: ["text", "tag", "category", "state", "after", "limit"]);

        Map("GET", "/catalog/workflows/{name}/versions", options.ReadPolicy!, async http =>
        {
            var name = Route(http, "name");

            if (!Scope(http).Allows("workflow", name))
                return Missing();

            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var query = Versions(http);
            var page = await Store(http).ListWorkflowVersionsAsync(name, query, cancellationToken: http.RequestAborted);

            return Cached(http, Project(page, PublishingResponses.Project), page.CatalogRevision, new { name, query });
        }, typeof(CatalogPage<WorkflowVersionResponse>), queries: ["after", "limit"]);

        MapWorkflow("GET", "", options.ReadPolicy!, (http, detail) =>
        {
            http.Response.Headers.ETag = LifecycleTag(detail.Summary.LifecycleRevision);

            return Task.FromResult(Json(PublishingResponses.Project(detail)));
        }, typeof(WorkflowVersionDetailResponse));

        MapWorkflow("GET", "/hosts", options.ReadPolicy!, async (http, detail) =>
                Json(await Catalog(http).GetWorkflowVersionHostsAsync(detail.Summary.Name, detail.Summary.Version, Scope(http))),
            typeof(IReadOnlyList<CatalogHost>));

        foreach (var action in new[] { "deprecate", "retire" })
        {
            var state = action == "retire" ? WorkflowVersionState.Retired : WorkflowVersionState.Deprecated;

            MapWorkflow("POST", "/" + action, options.LifecyclePolicy!, async (http, detail) =>
            {
                var expected = LifecyclePrecondition(http);

                try
                {
                    var row = await Store(http).SetWorkflowVersionStateAsync(detail.Summary.Name, detail.Summary.Version, state, expected,
                        http.RequestServices.GetRequiredService<IArtifactStore>(), Actor(http), cancellationToken: http.RequestAborted);

                    http.Response.Headers.ETag = LifecycleTag(row.LifecycleRevision);

                    return Json(PublishingResponses.Project((await Store(http)
                        .GetWorkflowVersionDetailAsync(row.Name, row.Version, Scope(http), cancellationToken: http.RequestAborted))!));
                }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidState &&
                                                            state == WorkflowVersionState.Retired)
                {
                    var blockers = await Store(http).GetRetirementBlockersAsync(detail.Summary.Name, detail.Summary.Version, Scope(http),
                        cancellationToken: http.RequestAborted);

                    return Results.Problem(statusCode: 409, title: "Workflow retirement is blocked",
                        extensions: new Dictionary<string, object?>
                            { ["code"] = "PUB040", ["class"] = "Conflict", ["blockers"] = blockers });
                }
            }, typeof(WorkflowVersionDetailResponse), precondition: "lifecycle");
        }

        MapWorkflow("GET", "/bundle", options.DownloadPolicy!,
            (http, detail) => WorkflowPublishingDownloads.WorkflowAsync(http, detail, source: false));

        MapWorkflow("GET", "/source", options.DownloadPolicy!,
            (http, detail) => WorkflowPublishingDownloads.WorkflowAsync(http, detail, source: true));

        Map("GET", "/catalog/packages", options.ReadPolicy!, async http =>
            {
                if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                    return denied;

                var query = new CatalogPackageQuery(Scope(http), Query(http, "text"), EnumQuery<WorkflowPackageOrigin>(http, "origin"),
                    BoolQuery(http, "withdrawn"), BoolQuery(http, "artifactPresent"), Query(http, "after"), Limit(http));

                return Json(Project(await Store(http).ListPackagesAsync(query, cancellationToken: http.RequestAborted),
                    PublishingResponses.Project));
            }, typeof(CatalogPage<PackageResponse>), queries: ["text", "origin", "withdrawn", "artifactPresent", "after", "limit"])
            .WithName(packageCatalogEndpoint);

        Map("GET", "/catalog/packages/{id}/versions", options.ReadPolicy!, async http =>
        {
            var id = Route(http, "id");

            if (!Scope(http).Allows("package", id))
                return Missing();

            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            return Json(Project(await Store(http).ListPackageVersionsAsync(id, Versions(http), cancellationToken: http.RequestAborted),
                PublishingResponses.Project));
        }, typeof(CatalogPage<PackageResponse>), queries: ["after", "limit"]);

        MapPackage("", async (http, detail) =>
        {
            http.Response.Headers.ETag = LifecycleTag(detail.Summary.LifecycleRevision);

            return await Task.FromResult(Json(PublishingResponses.Project(detail)));
        }, typeof(PackageDetailResponse));

        MapPackage("/activities",
            async (http, detail) =>
                Json(Project(
                    await Store(http).GetPackageActivitiesAsync(detail.Summary.Id, detail.Summary.Version, Versions(http),
                        cancellationToken: http.RequestAborted), PublishingResponses.Project)),
            typeof(CatalogPage<ActivityResponse>), queries: ["after", "limit"]);

        MapPackage("/hosts",
            async (http, detail) => Json(await Catalog(http)
                .GetPackageHostsAsync(detail.Summary.Id, detail.Summary.Version, Query(http, "build"), Scope(http))),
            typeof(IReadOnlyList<CatalogHost>), queries: ["build"]);

        MapPackage("/nupkg", WorkflowPublishingDownloads.PackageAsync, download: true);

        MapPackage("/source", (http, detail) => WorkflowPublishingDownloads.PackageEntryAsync(http, detail, "ixiflow/activity.yaml"),
            download: true);

        Map("GET", "/catalog/packages/{id}/{version}/holders", options.LifecyclePolicy!, async http =>
        {
            var detail = await PackageResource(http, options.LifecyclePolicy!);

            if (detail.Result != null)
                return detail.Result;

            return Json(await Store(http).GetPackageHoldersAsync(detail.Package!.Summary.Id, detail.Package.Summary.Version, Scope(http),
                cancellationToken: http.RequestAborted));
        }, typeof(PackageHolders));

        Map("DELETE", "/catalog/packages/{id}/{version}", options.LifecyclePolicy!, async http =>
        {
            var expected = LifecyclePrecondition(http);
            var resource = await PackageResource(http, options.LifecyclePolicy!);

            if (resource.Result != null)
                return resource.Result;

            var package = resource.Package!.Summary;

            var row = await Store(http).SetPackageWithdrawnAsync(package.Id, package.Version, true, expected, Actor(http),
                cancellationToken: http.RequestAborted);

            http.Response.Headers.ETag = LifecycleTag(row.LifecycleRevision);

            return Json(new PackageWithdrawalResponse(
                PublishingResponses.Project((await Store(http)
                    .GetPackageDetailAsync(row.Id, row.Version, Scope(http), cancellationToken: http.RequestAborted))!),
                await Store(http).GetPackageHoldersAsync(row.Id, row.Version, Scope(http), cancellationToken: http.RequestAborted)));
        }, typeof(PackageWithdrawalResponse), precondition: "lifecycle");

        Map("POST", "/catalog/packages", options.PublishPolicy!, http => http.RequestServices
                .GetRequiredService<WorkflowPackageUploadService>().UploadAsync(http,
                    http.RequestServices.GetRequiredService<LinkGenerator>().GetPathByName(packageCatalogEndpoint, http.Request.RouteValues)
                    ?? throw Configuration("The package catalog endpoint cannot be linked")),
            typeof(WorkflowPackageResponse), body: "application/octet-stream");

        Map("GET", "/catalog/activities", options.ReadPolicy!, async http =>
            {
                if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                    return denied;

                var query = new CatalogActivityQuery(Scope(http), Query(http, "text"), Query(http, "category"), Query(http, "packageId"),
                    EnumQuery<ActivityOrigin>(http, "origin"), BoolQuery(http, "withdrawn"), BoolQuery(http, "artifactPresent"),
                    Query(http, "after"), Limit(http));

                return Json(Project(await Store(http).ListActivitiesAsync(query, cancellationToken: http.RequestAborted),
                    PublishingResponses.Project));
            }, typeof(CatalogPage<ActivityResponse>),
            queries: ["text", "category", "packageId", "origin", "withdrawn", "artifactPresent", "after", "limit"]);

        Map("GET", "/catalog/activities/{key}/versions", options.ReadPolicy!, async http =>
        {
            var key = Route(http, "key");

            if (!Scope(http).Allows("activity", key))
                return Missing();

            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            return Json(Project(await Store(http).ListActivityVersionsAsync(key, Versions(http), cancellationToken: http.RequestAborted),
                PublishingResponses.Project));
        }, typeof(CatalogPage<ActivityResponse>), queries: ["after", "limit"]);

        Map("GET", "/catalog/activities/{key}/versions/{version}", options.ReadPolicy!, async http =>
        {
            var key = Route(http, "key");

            var provider = new ActivityProviderSelector(Query(http, "packageId") ?? throw new ArgumentException("packageId required"),
                Query(http, "packageVersion") ?? throw new ArgumentException("packageVersion required"),
                Query(http, "build") ?? throw new ArgumentException("build required"));

            if (!Scope(http).Allows("activity", key) || !Scope(http).Allows("package", provider.PackageId))
                return Missing();

            var detail = await Store(http).GetActivityAsync(key, Route(http, "version"), provider, Scope(http),
                cancellationToken: http.RequestAborted);

            if (detail == null)
                return Missing();

            if (await Allow(http, detail, options.ReadPolicy!) is { } denied)
                return denied;

            return Json(new ActivityDetailResponse(PublishingResponses.Project(detail.Activity), detail.Hosts));
        }, typeof(ActivityDetailResponse), queries: ["packageId", "packageVersion", "build"]);

        Map("GET", "/schemas/{kind}", options.ReadPolicy!, async http =>
        {
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var type = Route(http, "kind") switch
            {
                "workflow" => typeof(WorkflowDocument), "activity" => typeof(ActivityDocument), "template" => typeof(TemplateDocument),
                _ => null
            };

            return type == null ? Missing() : Results.Text(DocumentSchema.Write(type), "application/schema+json");
        }, typeof(string));

        Map("POST", "/drafts/template/{name}/{version}/preview", options.PreviewPolicy!,
            http => WorkflowPublishingTemplates.PreviewAsync(http, options),
            typeof(TemplatePreviewResponse), body: "application/json");

        Map("GET", "/catalog/templates", options.ReadPolicy!, async http =>
        {
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var query = new CatalogTemplateQuery(Scope(http), CatalogActivityRules.Text(Query(http, "text")),
                CatalogTemplateRules.Engine(Query(http, "engine")), Query(http, "after"), Limit(http));

            var page = await Store(http).ListTemplatesAsync(query, cancellationToken: http.RequestAborted);

            return Cached(http, page, page.CatalogRevision, query);
        }, typeof(CatalogPage<CatalogTemplate>), queries: ["text", "engine", "after", "limit"]);

        Map("GET", "/catalog/templates/{key}/versions", options.ReadPolicy!, async http =>
        {
            var key = Route(http, "key");

            if (!Scope(http).Allows("template", key))
                return Missing();

            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var query = Versions(http);
            var page = await Store(http).ListTemplateVersionsAsync(key, query, cancellationToken: http.RequestAborted);

            return Cached(http, page, page.CatalogRevision, new { key, query });
        }, typeof(CatalogPage<CatalogTemplate>), queries: ["after", "limit"]);

        foreach (var source in new[] { false, true })
        {
            var policy = source ? options.DownloadPolicy! : options.ReadPolicy!;

            Map("GET", "/catalog/templates/{key}/versions/{version}" + (source ? "/source" : ""), policy, async http =>
            {
                var key = Route(http, "key");
                var version = Version(http);

                if (!Scope(http).Allows("template", key))
                    return Missing();

                var detail = await Store(http).GetTemplateDetailAsync(key, version, Scope(http), cancellationToken: http.RequestAborted);

                if (detail == null)
                    return Missing();

                if (await Allow(http, detail, policy) is { } denied)
                    return denied;

                return source ? await WorkflowPublishingTemplates.SourceAsync(http, key, version) : Json(detail);
            }, source ? typeof(string) : typeof(CatalogTemplateDetail));
        }

        Map("GET", "/catalog/steps", options.ReadPolicy!, async http =>
        {
            if (await Allow(http, http, options.ReadPolicy!) is { } denied)
                return denied;

            var rows = await Store(http).ListStepDescriptorsAsync(Scope(http), cancellationToken: http.RequestAborted);

            return Cached(http, rows, await Store(http).GetCatalogRevisionAsync(cancellationToken: http.RequestAborted),
                new { steps = true });
        }, typeof(IReadOnlyList<CatalogStepDescriptor>));

        Map("GET", "/nuget/index.json", options.DownloadPolicy!, http => WorkflowPublishingNuGet.IndexAsync(http, options),
            typeof(NuGetServiceIndexResponse));

        Map("GET", "/nuget/flatcontainer/{id}/index.json", options.DownloadPolicy!,
            http => WorkflowPublishingNuGet.VersionsAsync(http, options), typeof(NuGetVersionsResponse));

        Map("GET", "/nuget/flatcontainer/{id}/{version}/{file}", options.DownloadPolicy!,
            http => WorkflowPublishingNuGet.FileAsync(http, options));

        WorkflowHttp.ValidateAtStartup(endpoints);

        return group;

        async Task Validate(CancellationToken token)
        {
            try
            {
                ValidateRegistration();
                await ValidatePolicies(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid publishing service configuration", error);
            }
        }

        void ValidateStructure()
        {
            try
            {
                ValidateRegistration();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid publishing service configuration", error);
            }
        }

        void ValidateRegistration()
        {
            var registrations = services.GetRequiredService<IServiceProviderIsService>();

            foreach (var type in new[]
                     {
                         typeof(IAuthorizationService), typeof(IAuthorizationPolicyProvider), typeof(WorkflowHost),
                         typeof(ICatalogQueryScope), typeof(IWorkflowCatalogStore), typeof(IWorkflowCatalog), typeof(IArtifactStore),
                         typeof(WorkflowPublisher),
                         typeof(WorkflowPublisherOptions), typeof(PublicationService), typeof(PackageIndexingService),
                         typeof(WorkflowPackageUploadService)
                     })
                if (!registrations.IsService(type))
                    throw Configuration("Publishing requires " + type.Name);

            foreach (var policy in new[]
                     {
                         options.ReadPolicy, options.AuthorPolicy, options.ExecutePolicy, options.PreviewPolicy, options.PublishPolicy,
                         options.LifecyclePolicy, options.DownloadPolicy
                     })
                if (string.IsNullOrWhiteSpace(policy))
                    throw Configuration("Every publishing policy must name an authorization policy");

            if (options.ActForPolicy != null && string.IsNullOrWhiteSpace(options.ActForPolicy))
                throw Configuration("Unknown publishing actor policy");

            if (options.PublishWait < TimeSpan.Zero || options.PublishWait > TimeSpan.FromDays(1) || options.MaxDraftBytes < 1 ||
                options.MaxConcurrentUploads < 1)
                throw Configuration("Invalid publishing limits");

            // Scoped application scopes are resolved per request. Singleton infrastructure is checked now.
            var publisher = services.GetRequiredService<WorkflowPublisherOptions>();

            if (publisher.PackageReadLimits == null || publisher.BuildTimeout <= TimeSpan.Zero || publisher.MaxPackageCandidates < 1 ||
                publisher.MaxResolvedPackages < 1 || publisher.MaxPackageResolutionBytes < 1 || publisher.MaxPreviewModelBytes < 1 ||
                publisher.MaxPreviewOutputBytes < 1 ||
                publisher.PublicationHeartbeatInterval <= TimeSpan.Zero ||
                publisher.PublicationStaleAfter <= publisher.PublicationHeartbeatInterval || publisher.SourceOptions == null)
                throw Configuration("Invalid publisher limits");

            publisher.PackageReadLimits.Validate();
            services.GetService<WorkflowBundleOptions>()?.ReadLimits.Validate();
            _ = services.GetRequiredService<WorkflowPublisher>();
            _ = services.GetRequiredService<PublicationService>();
            _ = services.GetRequiredService<WorkflowPackageUploadService>();
            _ = services.GetRequiredService<PackageIndexingService>();
        }

        async Task ValidatePolicies(CancellationToken token)
        {
            foreach (var policy in new[]
                     {
                         options.ReadPolicy, options.AuthorPolicy, options.ExecutePolicy, options.PreviewPolicy, options.PublishPolicy,
                         options.LifecyclePolicy, options.DownloadPolicy
                     })
                if (string.IsNullOrWhiteSpace(policy) || await services.GetRequiredService<IAuthorizationPolicyProvider>()
                        .GetPolicyAsync(policy).WaitAsync(token).ConfigureAwait(false) == null)
                    throw Configuration("Every publishing policy must name a registered authorization policy");

            if (options.ActForPolicy != null && (string.IsNullOrWhiteSpace(options.ActForPolicy) ||
                    await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(options.ActForPolicy).WaitAsync(token)
                        .ConfigureAwait(false) == null))
                throw Configuration("Unknown publishing actor policy");
        }

        RouteHandlerBuilder Map(string method, string route, string policy, Func<HttpContext, Task<IResult>> handler, Type? response = null,
            string[]? queries = null, string? body = null, string? precondition = null)
        {
            var endpoint = group.MapMethods(route, [method], (Delegate)handler)
                .WithMetadata(new WorkflowHttp.WorkflowRoute("publishing:" + prefix + ":" + method + ":" + route));

            WorkflowPublishingOpenApi.Describe(endpoint, method, route, policy, response, queries ?? [], body, precondition);

            return endpoint;
        }

        void MapWorkflow(string method, string suffix, string policy,
            Func<HttpContext, CatalogWorkflowVersionDetail, Task<IResult>> handler, Type? response = null, string? precondition = null) =>
            Map(method, "/catalog/workflows/{name}/versions/{version}" + suffix, policy, async http =>
            {
                // Syntax/presence precede lookup; the store alone tests freshness.
                if (precondition != null)
                    _ = LifecyclePrecondition(http);

                var name = Route(http, "name");
                var version = Version(http);

                if (!Scope(http).Allows("workflow", name))
                    return Missing();

                var detail = await Store(http)
                    .GetWorkflowVersionDetailAsync(name, version, Scope(http), cancellationToken: http.RequestAborted);

                if (detail == null)
                    return Missing();

                if (await Allow(http, detail, policy) is { } denied)
                    return denied;

                return await handler(http, detail);
            }, response, precondition: precondition);

        void MapPackage(string suffix, Func<HttpContext, CatalogPackageDetail, Task<IResult>> handler, Type? response = null,
            bool download = false, string[]? queries = null) =>
            Map("GET", "/catalog/packages/{id}/{version}" + suffix, download ? options.DownloadPolicy! : options.ReadPolicy!, async http =>
            {
                var id = Route(http, "id");
                var version = Route(http, "version");

                if (!Scope(http).Allows("package", id))
                    return Missing();

                var detail = await Store(http).GetPackageDetailAsync(id, version, Scope(http), cancellationToken: http.RequestAborted);

                if (detail == null)
                    return Missing();

                if (await Allow(http, detail, download ? options.DownloadPolicy! : options.ReadPolicy!) is { } denied)
                    return denied;

                return await handler(http, detail);
            }, response, queries);

        async Task<(CatalogPackageDetail? Package, IResult? Result)> PackageResource(HttpContext http, string policy)
        {
            var id = Route(http, "id");
            var version = Route(http, "version");

            if (!Scope(http).Allows("package", id))
                return (null, Missing());

            var detail = await Store(http).GetPackageDetailAsync(id, version, Scope(http), cancellationToken: http.RequestAborted);

            if (detail == null)
                return (null, Missing());

            return (detail, await Allow(http, detail, policy));
        }
    }

    private static Task<IResult?> Allow(HttpContext http, object resource, string policy) =>
        WorkflowHttp.AuthorizeAsync(http, resource, policy);

    private static WorkflowEngineException Configuration(string message) => new(WorkflowErrorKind.InvalidRequest, message);
}