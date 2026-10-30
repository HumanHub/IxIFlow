using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using IxIFlow.AspNetCore;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
public sealed class PrereleaseSlice3HttpScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OversizedDraftHasAnAccurateTitleAndAStackFreeInformationLog(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var logger = new RecordingLogger();

        await using var factory = new PublishingHttpFactory(database.NewRepository())
            { ConfigureServices = services => services.AddSingleton<ILogger<WorkflowPublishingOptions>>(logger) };

        factory.Options.MaxDraftBytes = 32;
        using var client = factory.Operator();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/pub/drafts/workflow/Large/1")
            { Content = new StringContent(new string('x', 64), Encoding.UTF8, "application/yaml") };

        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Draft exceeds the configured limit", await response.Content.ReadAsStringAsync());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Error == null);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error || entry.Error != null);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftDetailUsesOneLatestAttemptQueryAndNeverPagesHistory(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var inner = database.NewRepository();
        var draft = await inner.SaveDefinitionAsync(new("LatestHttp", 1, "workflow", "source"), null);
        var proxy = DispatchProxy.Create<IWorkflowStateRepository, ReadProbe>();
        var probe = (ReadProbe)(object)proxy;
        probe.Inner = inner;
        await using var factory = new PublishingHttpFactory(proxy);
        using var client = factory.Operator();
        using var response = await client.GetAsync("/pub/drafts/workflow/LatestHttp/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, probe.LatestReads);
        Assert.Equal(0, probe.HistoryReads);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedTemplateReadsKeepGuidIdentityAndRefuseAStaleDraft(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        using var feed =
            (PublishingFeed)typeof(TemplatePublisherScenarios).GetMethod("Feed", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                null)!;

        var yaml = (string)typeof(TemplatePublisherScenarios).GetMethod("Yaml", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            ["slice-three", "TemplateModels.Surface", "Hello {{ Name }}"])!;

        var old = await store.SaveDefinitionAsync(new("slice-three", 1, "template", yaml), null);
        var current = await store.SaveDefinitionAsync(old with { Source = yaml + "# updated\n" }, old.Id);
        var logger = new PublicationLogger();

        var publisher = new IxIFlow.Authoring.WorkflowPublisher(store, store, new() { NuGetConfigPath = feed.Config }, logger,
            new([new IxIFlow.Templates.Liquid.LiquidTemplateEngine()]));

        var published = await publisher.PublishAsync(current.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics) + logger.Error);
        Assert.Equal(current.Id, (await store.GetTemplateVersionAsync(current.Name, 1))!.PublishedDraft);
        var stale = await publisher.PublishAsync(old.Id);

        Assert.True(stale.Diagnostics.Count == 1 && stale.Diagnostics[0].Code == "PUB001",
            string.Join("; ", stale.Diagnostics) + logger.Error);
    }

    private sealed class PublicationLogger : ILogger<IxIFlow.Authoring.WorkflowPublisher>
    {
        internal Exception? Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Error = error;
    }

    public class ReadProbe : DispatchProxy
    {
        public IWorkflowStateRepository Inner { get; set; } = null!;
        public int LatestReads, HistoryReads;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowCatalogStore.GetLatestPublicationAsync))
                Interlocked.Increment(ref LatestReads);

            if (method.Name == nameof(IWorkflowCatalogStore.GetPublicationsAsync))
                Interlocked.Increment(ref HistoryReads);

            try
            {
                return method.Invoke(Inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();

                throw;
            }
        }
    }

    private sealed class RecordingLogger : ILogger<WorkflowPublishingOptions>
    {
        internal List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, error));
    }
}