using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Extensions;
using IxIFlow.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples;

public static class PublishingHttpExamples
{
    // The application supplies authentication. Policies below require explicit
    // permissions issued by that authentication system.
    // snippet: publishing-register
    public static void Register(IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { "read", "author", "execute", "preview", "publish", "lifecycle", "download" })
                options.AddPolicy("publishing-" + permission, policy => policy
                    .RequireAuthenticatedUser().RequireClaim("publishing", permission));
        });
        services.AddScoped<ICatalogQueryScope, SampleCatalogScope>();
        services.AddIxIFlow().AddWorkflowPublishing(new WorkflowPublishingOptions
        {
            ReadPolicy = "publishing-read",
            AuthorPolicy = "publishing-author",
            ExecutePolicy = "publishing-execute",
            PreviewPolicy = "publishing-preview",
            PublishPolicy = "publishing-publish",
            LifecyclePolicy = "publishing-lifecycle",
            DownloadPolicy = "publishing-download"
        });
    }
    // end-snippet: publishing-register

    // snippet: publishing-map
    public static void Map(WebApplication app) => app.MapWorkflowPublishing("/ixiflow/publishing");
    // end-snippet: publishing-map
}

// A deployment chooses its restrictions. All four identity sets are explicit.
public sealed class SampleCatalogScope : ICatalogQueryScope
{
    public ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CatalogScope(
            Workflows: new(Names: ["PublishedDouble", "BundledGreeting", "InlineDouble", "DoubleNumber", "NumberBody"], Prefixes: ["Sample"]),
            Activities: new(Names: ["acme.double"], Prefixes: ["samples."]),
            Packages: new(Names: ["acme.double"], Prefixes: ["samples."]),
            Templates: new(Prefixes: ["samples."])));
}
