using System.Security.Claims;
using System.Text.Encodings.Web;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IxIFlow.Tests.Conformance;

internal sealed class PublishingHttpFactory(IWorkflowStateRepository repository) : WebApplicationFactory<PublishingHttpFactory.Application>
{
    internal sealed class Application;
    internal static readonly string[] Policies = ["read", "author", "execute", "preview", "publish", "lifecycle", "download", "act-for"];
    public ScopeProvider Scope { get; } = new();
    public WorkflowPublishingOptions Options { get; } = new()
    {
        ReadPolicy = "read", AuthorPolicy = "author", ExecutePolicy = "execute", PreviewPolicy = "preview", PublishPolicy = "publish",
        LifecyclePolicy = "lifecycle", DownloadPolicy = "download", ActForPolicy = "act-for", PublishWait = TimeSpan.Zero
    };
    public WorkflowPublisherOptions PublisherOptions { get; set; } = new();
    public bool RegisterScope { get; set; } = true;
    public Func<object?, bool> ResourceAllowed { get; set; } = _ => true;
    public Action<IServiceCollection>? ConfigureServices { get; set; }
    public Action<IEndpointRouteBuilder>? ConfigureEndpoints { get; set; }
    public IArtifactStore? Artifacts { get; set; }
    public string? GroupPrefix { get; set; }
    public string? PathBase { get; set; }
    public HttpClient Operator(string? policies = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");
        client.DefaultRequestHeaders.Add("X-Test-Policies", policies ?? string.Join(' ', Policies));
        return client;
    }
    protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureWebHostDefaults(web => web.ConfigureServices(services =>
        {
            services.AddIxIFlow().AddWorkflowPublishing(Options, PublisherOptions);
            // These hosts exercise HTTP/publication ownership, without starting workflow execution members.
            // SQLite permits one execution host; several HTTP frontends may share its catalog.
            services.RemoveAll<IHostedService>();
            services.Replace(ServiceDescriptor.Singleton(repository));
            services.Replace(ServiceDescriptor.Singleton<IWorkflowCatalog>(new WorkflowCatalog(repository, artifacts: Artifacts ?? repository)));
            if (Artifacts != null) services.Replace(ServiceDescriptor.Singleton(Artifacts));
            if (RegisterScope) services.AddSingleton<ICatalogQueryScope>(Scope);
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Authentication>("test", _ => { });
            services.AddAuthorization(options =>
            {
                foreach (var name in Policies) options.AddPolicy(name, policy => policy.RequireAuthenticatedUser()
                    .RequireAssertion(context => context.User.HasClaim("policy", name) && ResourceAllowed(context.Resource)));
            });
            services.AddEndpointsApiExplorer();
            // The library annotations do not require the application's optional XML-comment generator.
            var documentName = "v1";
            services.AddOpenApi(documentName);
            ConfigureServices?.Invoke(services);
        }).Configure(app =>
        {
            if (PathBase != null) app.UsePathBase(PathBase);
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                (GroupPrefix == null ? endpoints : endpoints.MapGroup(GroupPrefix)).MapWorkflowPublishing("/pub");
                endpoints.MapOpenApi(); ConfigureEndpoints?.Invoke(endpoints);
            });
        }));
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory);
    internal sealed class ScopeProvider : ICatalogQueryScope
    {
        public CatalogScope Value { get; set; } = CatalogScope.Unrestricted;
        public ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => new(Value);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            return Task.FromResult(user.Length == 0 ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }.Concat(Request.Headers["X-Test-Policies"].ToString()
                    .Split(' ').Select(policy => new Claim("policy", policy))), "test")), "test")));
        }
    }
}
