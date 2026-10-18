using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Samples;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class DocumentationTests
{
    [Fact]
    public async Task RootHostedSnippetRuns() => await DocumentationExamples.RunHostedAsync();

    [Fact]
    public async Task WebhookSnippetRegistersWithoutMakingRequests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhooks:Orders:Secret"] = "whsec_" + Convert.ToBase64String(new byte[32])
        }).Build();
        DocumentationExamples.RegisterWebhook(services, configuration);
        await using var provider = services.BuildServiceProvider();
        var publisher = Assert.Single(provider.GetServices<IOutboxPublisher>());
        Assert.Equal("webhook:orders", publisher.Name);
        Assert.Contains(OutboxEvent.WorkflowCompleted, publisher.EventTypes);
    }

    [Fact]
    public async Task BranchLimitExcludesRootAndRecoveryLeavesOperatorStops()
    {
        var definition = new WorkflowBuilder<QuoteData, object>([], "BranchLimitDocs")
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<Label>())
                .Do(branch => branch.Step<Label>()))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.MaxActiveBranches = 2);
        await using var provider = services.BuildServiceProvider();
        var client = SampleTests.Client(provider);
        var finished = await client.RunAsync(definition, new QuoteData());
        Assert.True(finished.IsSuccess, finished.ErrorMessage);
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await client.ReleaseAsync(finished.InstanceId)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, await client.GetStatusAsync(finished.InstanceId));
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await client.RecoverAsync("unknown-doc-instance")).Status);

        var limitedServices = new ServiceCollection();
        limitedServices.AddLogging();
        limitedServices.AddIxIFlow(options => options.MaxActiveBranches = 1);
        await using var limitedProvider = limitedServices.BuildServiceProvider();
        var limitedClient = SampleTests.Client(limitedProvider);
        var stopped = await limitedClient.RunAsync(definition, new QuoteData());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var recovered = await limitedClient.RecoverAsync(stopped.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Null(recovered.WorkflowData);
    }

    [Fact]
    public void TimedWaitRequiresBothDurationAndNonemptyPath()
    {
        var missingPath = new WorkflowBuilder<QuoteData, object>([], "MissingTimeoutPath")
            .WaitFor<ReviewAnswer>("review", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1)))
            .Build();
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(missingPath));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);

        var missingDuration = new WorkflowBuilder<QuoteData, object>([], "MissingTimeoutDuration")
            .WaitFor<ReviewAnswer>("review", configure: wait => wait.OnTimeout(path => path.Step<Label>()))
            .Build();
        Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(missingDuration));
    }

    [Fact]
    public async Task TimeoutExitsContainingBody()
    {
        var definition = new WorkflowBuilder<QuoteData, object>([], "TimeoutExitDocs")
            .Sequence(sequence => sequence
                .WaitFor<ReviewAnswer>("review", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Step<Label>(step => step
                        .Input(activity => activity.Value).From(ctx => "timed out")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
                .Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => "should be skipped")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Step<Label>(step => step
                .Input(activity => activity.Value).From(ctx => "after sequence")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Customer))
            .Build();
        var time = new FakeTimeProvider();
        await using var provider = SampleTests.Services(time);
        var client = SampleTests.Client(provider);
        var started = await client.RunAsync(definition, new QuoteData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        time.Advance(TimeSpan.FromMinutes(1));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var finished = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, deadline.Token);
        Assert.True(finished.IsSuccess, finished.ErrorMessage);
        var data = Assert.IsType<QuoteData>(finished.WorkflowData);
        Assert.Equal("timed out", data.Outcome);
        Assert.Equal("after sequence", data.Customer);
    }

    [Fact]
    public void YamlFunctionRequiresALocalAssetWithoutArguments()
    {
        const string yaml = """
            workflow:
              name: InvalidFunctionDocs
              dataType: IxIFlow.Samples.QuoteData
            definitions:
              - kind: activity
                activity: IxIFlow.Samples.Label
                input:
                  - target: Value
                    from: { kind: function, function: System.Math.Abs }
            """;
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "function.workflow.yaml"));
        var withArguments = yaml.Replace("definitions:", "code:\n  System.Math.Abs:\n    language: csharp-expression\n    source: System.Math.Abs(-1)\ndefinitions:")
            .Replace("function: System.Math.Abs }", "function: System.Math.Abs, arguments: [{ kind: constant, value: -1 }] }");
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(withArguments, "function.workflow.yaml"));
    }

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
        foreach (var page in PublicPages(root))
        {
            var markdown = File.ReadAllText(page);
            var snippets = Regex.Matches(markdown,
                @"<!-- snippet: (?<source>\S+) -->\r?\n```(?<language>csharp|yaml)\r?\n(?<body>[\s\S]*?)\r?\n```");
            // The generator README also has an editor directive, not an executable workflow document.
            var executableBlocks = Regex.Matches(markdown,
                @"^```(?:csharp|yaml)\s*\r?\n(?!# yaml-language-server:)", RegexOptions.Multiline).Count;
            Assert.Equal(executableBlocks,
                snippets.Count);
            foreach (Match snippet in snippets)
            {
                var reference = snippet.Groups["source"].Value.Split('#', 2);
                Assert.True(reference[0].StartsWith("samples/IxIFlow.Samples/", StringComparison.Ordinal) ||
                    reference[0].StartsWith("tests/IxIFlow.Samples.Tests/", StringComparison.Ordinal));
                var source = File.ReadAllText(Path.Combine(root, reference[0]));
                var compiledSource = source;
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
                var body = snippet.Groups["body"].Value;
                if (snippet.Groups["language"].Value == "csharp")
                {
                    // Imports are file-level in the compiled source and precede the sample method.
                    foreach (Match import in Regex.Matches(body, @"^using (?<name>[\w.]+);\s*$", RegexOptions.Multiline))
                        Assert.True(compiledSource.Contains(import.Value.Trim(), StringComparison.Ordinal) ||
                            compiledSource.Contains("namespace " + import.Groups["name"].Value + ";", StringComparison.Ordinal),
                            $"Import in {page} is missing from {reference[0]}: {import.Value.Trim()}");
                    body = Regex.Replace(body, @"^using [\w.]+;\s*$", "", RegexOptions.Multiline);
                }
                Assert.True(Normalize(source).Contains(Normalize(body),
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

        foreach (var page in PublicPages(root))
        {
            var markdown = File.ReadAllText(page);
            Assert.DoesNotMatch(@"\b[A-Z]-\d{2}\b", markdown);
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

    private static IEnumerable<string> PublicPages(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md")
            .Append(Path.Combine(root, "README.md"))
            .Append(Path.Combine(root, "samples", "README.md"))
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "src"))
                .Select(directory => Path.Combine(directory, "README.md")).Where(File.Exists));

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
