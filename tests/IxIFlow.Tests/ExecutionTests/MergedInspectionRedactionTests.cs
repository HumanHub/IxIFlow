using System.Text.Json.Schema;
using System.Security.Claims;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "H-08")]
[Trait("Feature", "tracing")]
public sealed class MergedInspectionRedactionTests
{
    private const string Secret = "merged-inspection-secret-57de9e";

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task ManagementAndHttpReadsProjectBeforeReturningValues(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        await using var relational = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + path) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(store);
        builder.Services.AddIxIFlow();
        DiagnosticTestAccess.Allow(builder.Services);
        builder.Services.AddSingleton<IInstanceQueryScope, Scope>();
        builder.Services.AddAuthorization(options => options.AddPolicy("operator", policy => policy.RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.MapWorkflowOperations("/ops", options =>
        {
            options.ReadPolicy = options.ReplyPolicy = options.InspectPolicy = options.WritePolicy = options.OutboxPolicy = "operator";
        });
        var client = app.Services.GetRequiredService<IWorkflowClient>();
        var operations = app.Services.GetRequiredService<IWorkflowOperations>();
        var started = await client.RunAsync(Workflow.Create<Data>("MergedInspection" + sqlite).Step<Noop>()
            .WaitFor<Notice>("gate", configure: wait => wait.DefaultPayload(new Notice { Secret = Secret }))
            .Step<Throw>().Build(), new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(wait));
        var unstripped = JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Default, typeof(Notice)).ToJsonString();
        Assert.Contains(Secret, unstripped);
        Assert.Contains("default", unstripped);
        Assert.Contains("Secret", wait.EventSchema);
        Assert.DoesNotContain(Secret, wait.EventSchema);
        Assert.DoesNotContain("default", wait.EventSchema);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(new
        {
            page = await operations.SearchAsync(new(InstanceId: started.InstanceId)),
            detail = await operations.GetAsync(started.InstanceId),
            waits = await operations.GetWaitsAsync([started.InstanceId]),
            trace = await operations.GetTraceAsync(started.InstanceId, limit: 2)
        }));
        foreach (var suffix in new[] { "", "/snapshot", "/waits", "/trace", "/pending-activities" })
            Assert.DoesNotContain(Secret, await GetAsync("/ops/instances/{id}" + suffix, started.InstanceId));
        var failed = await client.ReplyAsync(wait.Token, new Notice());
        Assert.Equal(Secret, failed.ErrorMessage);
        var raw = Assert.Single(await app.Services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(started.InstanceId));
        var trace = await operations.GetTraceAsync(started.InstanceId);
        Assert.Contains(trace, entry => entry.Entry.Error?.DiagnosticId == raw.DiagnosticId);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(trace));
        var httpTrace = await GetAsync("/ops/instances/{id}/trace", started.InstanceId);
        Assert.DoesNotContain(Secret, httpTrace);
        Assert.Contains(raw.DiagnosticId, httpTrace);
        Assert.Equal(Secret, raw.Message);
        Assert.Equal(Secret, Assert.IsType<Data>((await client.AwaitAsync(started.InstanceId)).WorkflowData).Secret);

        async Task<string> GetAsync(string route, string id)
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);
            var http = new DefaultHttpContext { RequestServices = app.Services,
                User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test")) };
            http.SetEndpoint(endpoint);
            http.Request.Method = "GET";
            http.Request.RouteValues["id"] = id;
            http.Request.Path = route.Replace("{id}", id);
            http.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(http);
            Assert.Equal(200, http.Response.StatusCode);
            http.Response.Body.Position = 0;
            return await new StreamReader(http.Response.Body).ReadToEndAsync();
        }
    }

    [Fact]
    public async Task OutboxAdministrationErasesErrorsAndBodiesForDotNetCallers()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        await store.RegisterPublisherAsync("publisher", [OutboxEvent.WorkflowCompleted]);
        await OutboxStoreContract.CompleteAsync(store);
        var claim = Assert.Single(await store.ClaimDeliveriesAsync("publisher", "owner", TimeSpan.FromMinutes(1), 1));
        await store.SettleDeliveryAsync(claim.Event.Id, "publisher", "owner", OutboxPublishResult.GiveUp(Secret, true));
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        var delivery = (await operations.GetDeliveryAsync(claim.Event.Id, "publisher", "reader"))!;
        Assert.Equal("", delivery.Event.DataJson);
        Assert.Contains("DeliveryFailure", delivery.LastError);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(new
        {
            delivery, page = await operations.ListDeliveriesAsync(new(), "reader"),
            publishers = await operations.ListPublishersAsync("reader")
        }));
        Assert.Equal(Secret, (await store.GetDeliveryAsync(claim.Event.Id, "publisher"))!.LastError);
        Assert.Equal(Secret, Assert.Single(await store.ListPublishersAsync()).LastError);
    }

    [Fact]
    public async Task OperationCompletionKeepsTheCommittedDiagnosticIdentity()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        DiagnosticTestAccess.Allow(services);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("OperationFailure").Step<Noop>()
            .Saga(saga => saga.Step<UnsafeOutput>()).Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, started.Status);
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(started.InstanceId));
        var operation = await provider.GetRequiredService<IWorkflowOperations>().SubmitResolutionAsync(started.InstanceId,
            pending.InvocationId, ActivityResolution.Failed(Secret, "operator"));
        var result = await operation.Completion;
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Error!.ErrorType);
        Assert.Equal(result.Error.ToString(), result.ErrorMessage);
        var raw = await provider.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(started.InstanceId);
        var failure = Assert.Single(raw, item => item.ErrorType != "OperatorNote");
        Assert.Equal(result.Error.DiagnosticId, failure.DiagnosticId);
        Assert.Equal(result.Error.ErrorType, failure.ErrorType);
        Assert.Equal(Secret, failure.Message);
        var note = Assert.Single(raw, item => item.ErrorType == "OperatorNote");
        Assert.Equal(Secret, note.Message);
        Assert.Null(note.StackTrace);
        Assert.NotEqual(failure.DiagnosticId, note.DiagnosticId);
        var resolved = Assert.Single(await client.GetTraceAsync(started.InstanceId),
            entry => entry.EntryType == TraceEntryType.ActivityResolved);
        Assert.Equal(note.DiagnosticId, resolved.Error!.DiagnosticId);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task TerminationRetainsOnlyTheMostRecentRawDiagnostic(bool sqlite)
    {
        await using var relational = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        DiagnosticTestAccess.Allow(services);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("TerminatedDiagnostics" + sqlite).Step<Noop>()
            .WaitFor<Notice>("gate").Build(), new Data());
        var saved = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var older = checkpoint.RecordDiagnostic("ResourceLimit", Secret);
        var recent = checkpoint.RecordDiagnostic("RecentFailure", Secret);
        checkpoint.OperatorStop = new OperatorStopState { Kind = OperatorStopKind.ResourceLimit, Diagnostic = older, Reason = Secret };
        saved.Status = WorkflowStatus.NeedsResolution;
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await store.AppendEventAsync(new(saved.InstanceId, Guid.NewGuid().ToString("N"),
            typeof(Notice).AssemblyQualifiedName!, "{}", Key: "gate"));
        var lease = await StoreTestSupport.LeaseAsync(store, saved.InstanceId);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved,
            lease.Epoch, saved.Revision, stopped: true, diagnostics: checkpoint.PendingDiagnostics,
            retainedDiagnosticIds: [older.DiagnosticId, recent.DiagnosticId]))).Status);
        await StoreTestSupport.ReleaseAsync(store, saved.InstanceId);
        Assert.Equal(2, (await store.GetDiagnosticsAsync(saved.InstanceId)).Count);
        var limitedServices = new ServiceCollection().AddLogging();
        limitedServices.AddSingleton(store);
        limitedServices.AddIxIFlow(options => options.MaxCheckpointBytes = 1);
        await using var limited = limitedServices.BuildServiceProvider();
        var terminated = await limited.GetRequiredService<IWorkflowOperations>().TerminateAsync(saved.InstanceId, "operator", actor: "operator");
        Assert.Equal(WorkflowStatus.Terminated, terminated.Status);
        var raw = Assert.Single(await provider.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(saved.InstanceId));
        Assert.Equal(recent.DiagnosticId, raw.DiagnosticId);
        var snapshot = await client.GetSnapshotAsync(saved.InstanceId);
        Assert.Equal(recent, Assert.Single(snapshot!.Errors));
    }

    private sealed class Scope : IInstanceQueryScope
    {
        public ValueTask<InstanceQuery> ScopeAsync(ClaimsPrincipal principal, InstanceQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(query);
        public ValueTask<WaitQuery> ScopeAsync(ClaimsPrincipal principal, WaitQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(query);
    }
    public sealed class Data { [Sensitive] public string Secret { get; set; } = MergedInspectionRedactionTests.Secret; }
    public sealed class Notice(string secret = MergedInspectionRedactionTests.Secret)
    {
        [Sensitive] public string Secret { get; set; } = secret;
    }
    public sealed class Noop : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class Throw : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret); }
    public sealed class UnsafeOutput : IAsyncActivity
    {
        [ActivityOutput] public object Result { get; set; } = (Action)(() => { });
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
