using System.Security.Claims;
using System.Text.Encodings.Web;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IxIFlow.Tests.Conformance;

internal sealed class OperationsHttpFactory(IWorkflowStateRepository repository, IWorkflowCatalog? registry = null)
    : WebApplicationFactory<OperationsHttpFactory.Application>
{
    internal sealed class Application;
    public TestScope Scope { get; } = new();
    public Probe ActivityProbe { get; } = new();
    public string? DenyInstance { get; set; }
    public Action<WorkflowOperationsOptions>? ConfigureOptions { get; set; }
    public Action<IServiceCollection>? ConfigureServices { get; set; }
    public Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>? ConfigureEndpoints { get; set; }
    public bool RegisterScope { get; set; } = true;
    public TimeSpan Wait { get; set; } = TimeSpan.FromMilliseconds(100);
    public int Slots { get; set; } = 64;
    public IWorkflowClient Engine => Services.GetRequiredService<IWorkflowClient>();
    public HttpClient Operator(string? policies = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");
        client.DefaultRequestHeaders.Add("X-Test-Policies", policies ?? "read reply inspect write outbox act-for");
        return client;
    }

    protected override IHostBuilder CreateHostBuilder() => Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()).ConfigureWebHostDefaults(web =>
        web.ConfigureServices(services =>
        {
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddIxIFlow(o => { o.PollInterval = TimeSpan.FromMilliseconds(30); o.Slots = Slots; });
            services.Replace(ServiceDescriptor.Singleton(repository));
            services.Replace(ServiceDescriptor.Singleton(registry ?? new WorkflowCatalog()));
            services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
            services.AddSingleton(ActivityProbe);
            services.AddTransient<Noop>(); services.AddTransient<Block>(); services.AddTransient<Uncertain>();
            if (RegisterScope) services.AddSingleton<IInstanceQueryScope>(Scope);
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, Authentication>("test", _ => { });
            services.AddAuthorization(options =>
            {
                foreach (var name in new[] { "read", "reply", "inspect", "write", "outbox", "act-for" })
                    options.AddPolicy(name, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                        context.User.HasClaim("policy", name) && (context.Resource is not InstanceSummary summary || summary.InstanceId != DenyInstance)));
            });
            ConfigureServices?.Invoke(services);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapWorkflowOperations("/ops", options =>
                {
                    options.ReadPolicy = "read"; options.ReplyPolicy = "reply"; options.InspectPolicy = "inspect";
                    options.WritePolicy = "write"; options.OutboxPolicy = "outbox"; options.MutationWait = Wait;
                    ConfigureOptions?.Invoke(options);
                });
                ConfigureEndpoints?.Invoke(endpoints);
            });
        }));
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory);

    public sealed class TestScope : IInstanceQueryScope
    {
        public Func<InstanceQuery, InstanceQuery> Instances { get; set; } = query => query;
        public Func<WaitQuery, WaitQuery> Waits { get; set; } = query => query;
        public ValueTask<InstanceQuery> ScopeAsync(ClaimsPrincipal user, InstanceQuery query, CancellationToken cancellationToken = default) => new(Instances(query));
        public ValueTask<WaitQuery> ScopeAsync(ClaimsPrincipal user, WaitQuery query, CancellationToken cancellationToken = default) => new(Waits(query));
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(user) ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }.Concat(
                    Request.Headers["X-Test-Policies"].ToString().Split(' ').Select(p => new Claim("policy", p))), "test")), "test")));
        }
    }
    public sealed class Data { public string Secret { get; set; } = "private-workflow-data"; public int Number { get; set; } }
    public sealed class Approval;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Probe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RunToken { get; set; }
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.RunToken = cancellationToken;
            probe.Entered.TrySetResult();
            await probe.Exit.Task; // An external effect can ignore cancellation; ownership still fences its commit.
        }
    }
    public sealed class Uncertain : IAsyncActivity
    {
        public int Number { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public static WorkflowDefinition Waiting(string name = "HttpOperations") => Workflow.Create<Data>(name).Step<Noop>().WaitFor<Approval>("approval").Build();
}
