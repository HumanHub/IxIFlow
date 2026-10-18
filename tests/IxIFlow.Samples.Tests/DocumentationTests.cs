using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Core;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class DocumentationTests
{
    [Fact]
    public async Task DiscoverableWaitSuggestsButDoesNotApplyAnAnswer()
    {
        await using var provider = SampleTests.Services();
        var client = SampleTests.Client(provider);
        var started = await client.RunAsync(DocumentationExamples.DiscoverableReview(),
            new QuoteData { Customer = "Waiting" });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal("Waiting", Assert.IsType<QuoteData>(started.WorkflowData).Customer);
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.Equal("Ada", JsonSerializer.Deserialize<ReviewAnswer>(wait.DefaultPayloadJson!)!.Customer);

        var finished = await DocumentationExamples.AnswerReviewAsync(client, started.InstanceId, "Grace");
        Assert.True(finished.IsSuccess, finished.ErrorMessage);
        Assert.Equal(EventOutcome.Consumed, finished.Event!.Outcome);
        Assert.Equal("Grace", Assert.IsType<QuoteData>(finished.WorkflowData).Customer);
    }

    [Fact]
    public async Task RetentionExampleLeavesAClientVisibleTombstone()
    {
        var time = new FakeTimeProvider();
        var services = DocumentationExamples.RetentionServices();
        services.AddSingleton<TimeProvider>(time);
        await using var provider = services.BuildServiceProvider();
        var client = SampleTests.Client(provider);
        var finished = await client.RunAsync(Quote.Definition(), new QuoteData());
        Assert.True(finished.IsSuccess, finished.ErrorMessage);

        time.Advance(TimeSpan.FromDays(31));
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        var purged = await store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100);
        Assert.Equal(1, purged.InstancesRemoved);
        Assert.Equal(WorkflowExecutionStatus.Removed, await client.GetStatusAsync(finished.InstanceId));
        var removed = await client.AwaitAsync(finished.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Removed, removed.Status);
        Assert.Null(removed.WorkflowData);
        Assert.True((await client.GetTraceAsync(finished.InstanceId)).IsRemoved);
        Assert.NotNull(await store.GetTombstoneAsync(finished.InstanceId));
    }

    [Fact]
    public async Task PublisherExampleRegistersAndAcceptsACompletion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        DocumentationExamples.RegisterPublisher(services);
        await using var provider = services.BuildServiceProvider();
        var publisher = Assert.Single(provider.GetServices<IOutboxPublisher>().OfType<CompletionLog>());
        Assert.Equal("completion-log", publisher.Name);
        Assert.Contains(OutboxEvent.WorkflowCompleted, publisher.EventTypes);
        var now = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var envelope = new OutboxEvent("event-1", OutboxEvent.WorkflowCompleted,
            "instance-1", "Quote", 1, "correlation-1", now, "{\"status\":\"Completed\"}");
        var result = await publisher.PublishAsync(new OutboxPublishContext(envelope, 1, now),
            CancellationToken.None);
        Assert.Equal(OutboxPublishOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public void GuideSnippetsMatchTheirCompiledSources()
    {
        var root = RepositoryRoot();
        var count = 0;
        foreach (var page in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"))
        {
            var markdown = File.ReadAllText(page);
            var snippets = Regex.Matches(markdown,
                @"<!-- snippet: (?<source>\S+) -->\r?\n```(?<language>csharp|yaml)\r?\n(?<body>[\s\S]*?)\r?\n```");
            Assert.Equal(Regex.Matches(markdown, @"^```(?:csharp|yaml)\s*$", RegexOptions.Multiline).Count,
                snippets.Count);
            foreach (Match snippet in snippets)
            {
                var reference = snippet.Groups["source"].Value.Split('#', 2);
                Assert.True(reference[0].StartsWith("samples/IxIFlow.Samples/", StringComparison.Ordinal) ||
                    reference[0].StartsWith("tests/IxIFlow.Samples.Tests/", StringComparison.Ordinal));
                var source = File.ReadAllText(Path.Combine(root, reference[0]));
                if (reference.Length == 2)
                {
                    var start = "// snippet: " + reference[1];
                    var end = "// end-snippet: " + reference[1];
                    var offset = source.IndexOf(start, StringComparison.Ordinal);
                    Assert.True(offset >= 0, start);
                    offset += start.Length;
                    var endOffset = source.IndexOf(end, offset, StringComparison.Ordinal);
                    Assert.True(endOffset >= offset, end);
                    source = source[offset..endOffset];
                }
                Assert.True(Normalize(source).Contains(Normalize(snippet.Groups["body"].Value),
                    StringComparison.Ordinal), $"Snippet in {Path.GetFileName(page)} differs from {reference[0]}");
                count++;
            }
        }
        Assert.True(count > 0);
    }

    [Fact]
    public void ReadmeAndGuideLinksResolveAndTheIndexListsEveryGuide()
    {
        var root = RepositoryRoot();
        var guides = Directory.GetFiles(Path.Combine(root, "docs"), "*.md");
        var index = File.ReadAllText(Path.Combine(root, "docs", "index.md"));
        foreach (var guide in guides.Where(path => Path.GetFileName(path) != "index.md"))
            Assert.Contains("](" + Path.GetFileName(guide) + ")", index);

        foreach (var page in guides.Append(Path.Combine(root, "README.md")))
        {
            var markdown = File.ReadAllText(page);
            foreach (Match link in Regex.Matches(markdown, @"\[[^\]]*\]\((?<target>[^\s)]+)\)"))
            {
                var target = link.Groups["target"].Value.Trim('<', '>');
                if (Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                    continue;
                var parts = target.Split('#', 2);
                var path = parts[0].Length == 0 ? page : Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(page)!, Uri.UnescapeDataString(parts[0])));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"Broken link in {page}: {target}");
                if (parts.Length == 2 && parts[1].Length > 0 && File.Exists(path))
                    Assert.Contains(Uri.UnescapeDataString(parts[1]), HeadingAnchors(File.ReadAllText(path)));
            }
        }
    }

    private static string Normalize(string value) => string.Join('\n', value.Replace("\r\n", "\n")
        .Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    private static IEnumerable<string> HeadingAnchors(string markdown)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match heading in Regex.Matches(markdown, @"^#{1,6}\s+(.+?)\s*$", RegexOptions.Multiline))
        {
            var text = heading.Groups[1].Value.TrimEnd('#').Trim().ToLowerInvariant();
            var anchor = Regex.Replace(text, @"[^\p{L}\p{N}_\- ]", "").Replace(' ', '-');
            var suffix = used.GetValueOrDefault(anchor);
            used[anchor] = suffix + 1;
            yield return suffix == 0 ? anchor : anchor + "-" + suffix;
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IxIFlow.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find the repository containing documentation examples");
    }
}
