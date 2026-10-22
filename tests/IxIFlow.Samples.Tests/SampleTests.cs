using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using IxIFlow.Samples.Generated;
using IxIFlow.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class SampleTests
{
    public static TheoryData<string, bool> Pairs => new()
    {
        { "quote", false }, { "quote", true },
        { "sequential", false }, { "sequential", true },
        { "all", false }, { "all", true },
        { "any", false }, { "any", true },
        { "conditional", false }, { "conditional", true },
        { "review", false }, { "review", true },
        { "child", false }, { "child", true },
        { "reusable", false }, { "reusable", true },
        { "reservation", false }
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task YamlAndFluentHaveTheSameSavedStructureHashAndResult(string sample, bool alternative)
    {
        var (fluent, yaml) = Definitions(sample);
        var first = await Execute(fluent, Data(sample, alternative), sample, Review.Child());
        var second = await Execute(yaml, Data(sample, alternative), sample, Workflow_review_child_workflow.Build());
        Assert.Equal(first.StructureHash, second.StructureHash);
        Assert.Equal(JsonSerializer.Serialize(first.Data), JsonSerializer.Serialize(second.Data));
        if (first.Data is QuoteData quote)
        {
            Assert.Equal(sample == "reusable" ? 11m : alternative ? 2.5m : 12.5m, quote.Amount);
            if (sample == "quote")
                Assert.Equal(alternative ? "small order" : "ready", quote.Outcome);
            if (sample == "reusable")
            {
                Assert.Equal("quoted", quote.Outcome);
                Assert.Equal(new FakeTimeProvider().GetUtcNow(), quote.PricedAt);
            }
            if (sample == "review")
                Assert.Equal("Grace", quote.Customer);
        }
        else if (first.Data is ReservationData reservation)
            Assert.Equal("reservation-1", reservation.Reference);
        else
        {
            var results = Assert.IsType<BatchData>(first.Data).Results;
            Assert.Equal(alternative ? 0 : 3, results.Count);
            Assert.Equal(Enumerable.Range(0, results.Count), results.Select(result => result.Index));
            if (sample is "sequential" or "all")
            {
                Assert.All(results, result => Assert.Equal(ItemStatus.Completed, result.Status));
                Assert.Equal(alternative ? [] : new[] { 2000, 3000, 4000 }, results.Select(result => result.Value));
            }
            else if (!alternative)
            {
                Assert.Equal(sample == "any" ? 1 : 2, results.Count(result => result.Status == ItemStatus.Completed));
                Assert.Contains(results, result => result.Status != ItemStatus.Completed);
            }
        }
    }

    [Fact]
    public void EveryGeneratedDocumentHasAParityCase()
    {
        var generated = typeof(Quote).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "IxIFlow.Samples.Generated").Select(type => type.Name).Order().ToArray();
        var covered = Pairs.Select(row => Definitions((string)row[0]).Yaml)
            .Select(definition => definition.Name)
            .Concat(YamlSampleTests.YamlDefinitions.Values.Select(build => build().Name)).Distinct().Count();
        Assert.Equal(18, generated.Length);
        Assert.Equal(generated.Length, covered);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(5, 6)]
    public async Task RestockChecksBeforeWhileAndAfterDo(int initial, int expected)
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(Restock.Definition(), new StockData { Count = initial });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(expected, Assert.IsType<StockData>(result.WorkflowData).Count);
    }

    [Fact]
    public async Task CatchUsesTypedFaultAndFinallyAudits()
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(FailedQuote.Definition(), new FailureData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<FailureData>(result.WorkflowData);
        Assert.Equal("Quote expired", data.Message);
        Assert.True(data.Audited);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("any")]
    [InlineData("conditional")]
    public async Task ParallelBranchesMergeOnlyTheirChanges(string join)
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(ParallelQuotes.Definition(join), new QuoteData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var amount = Assert.IsType<QuoteData>(result.WorkflowData).Amount;
        if (join == "all")
            Assert.Equal(17.5m, amount);
        else
            Assert.Contains(amount, new[] { 12.5m, 15m });
    }

    [Fact]
    public async Task RetryReadsEarlierAttemptErrorAndNote()
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(RetryQuote.Definition(), new RetryData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<RetryData>(result.WorkflowData);
        Assert.Equal(2, data.Attempt);
        Assert.Equal("Primary unavailable", data.EarlierError);
        Assert.Equal("primary endpoint", data.EarlierNote);
        Assert.NotEmpty(data.InvocationId);
        Assert.NotEmpty(data.EarlierAttemptId);
        Assert.NotEqual(data.EarlierAttemptId, data.AttemptId);
    }

    [Fact]
    public async Task ReminderWaitsForFakeTimeAndReadsTheWorkflowClock()
    {
        var time = new FakeTimeProvider();
        await using var services = Services(time);
        var client = Client(services);
        var start = await client.RunAsync(Reminder.Definition(), new ReminderData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
        time.Advance(TimeSpan.FromMinutes(59));
        var early = await client.AwaitAsync(start.InstanceId);
        Assert.True(early.Status == WorkflowExecutionStatus.Suspended, early.Status + ": " + early.ErrorMessage);
        time.Advance(TimeSpan.FromMinutes(1));
        var end = await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        Assert.Equal(time.GetUtcNow(), Assert.IsType<ReminderData>(end.WorkflowData).SentAt);
    }

    [Fact]
    public async Task ContractsExposeTypedDefaultsAndDirections()
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(Contracts.ValidateQuote(), new QuoteData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var catalog = Contracts.Catalog();
        Assert.Single(catalog.Packages);
        var activity = Assert.Single(catalog.Activities, entry => entry.Key == "samples.add-fee");
        Assert.Equal(2.5m, JsonDocument.Parse(Assert.Single(activity.Inputs, field => field.Name == "Fee").DefaultJson!).RootElement.GetDecimal());
        var customer = Assert.Single(activity.Inputs, field => field.Name == "Customer");
        Assert.True(customer.Required);
        Assert.Equal("Customer", customer.DisplayName);
        Assert.Equal("text", customer.UIHint);
        Assert.Equal("The customer receiving the quote", customer.Description);
        Assert.Contains(activity.Inputs, field => field.Name == "Amount");
        Assert.Contains(activity.Outputs, field => field.Name == "Amount");
        Assert.DoesNotContain(activity.Inputs, field => field.Name == "Receipt");
        Assert.Contains(activity.Outputs, field => field.Name == "Receipt");
        Assert.Equal(typeof(AddFee), catalog.Find("samples.add-fee")!.ActivityType);
    }

    [Fact]
    public void MissingRequiredMappingFailsDefinitionValidation()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<QuoteData>("MissingCustomer")
            .Step<AddFee>().Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("Customer", error.Message);
    }

    [Fact]
    public async Task EmptyRequiredValueIsRejectedBeforeExecution()
    {
        await using var services = Services();
        var result = await Client(services).RunAsync(Quote.Definition(), new QuoteData { Customer = "" });
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Customer", result.ErrorMessage);
    }

    [Fact]
    public async Task TelemetryAndCommittedInspectionAreAvailable()
    {
        using var observations = new Observability();
        await using var services = Services();
        var client = Client(services);
        var result = await client.RunAsync(Quote.Definition(), new QuoteData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var inspection = await Observability.InspectAsync(client, result.InstanceId);
        Assert.NotEmpty(inspection.Trace);
        Assert.NotNull(inspection.Snapshot);
        Assert.NotEmpty(observations.Spans);
        Assert.Contains("ixiflow.activity.duration", observations.Measurements);
        Assert.Contains("ixiflow.commit.size", observations.Measurements);
    }

    [Fact]
    public async Task LiquidUsesTheParentCultureAndSendsWithAnIdempotencyKey()
    {
        var sender = new RecordingSender();
        var collection = Hosting.Services();
        collection.AddSingleton<TimeProvider>(new FakeTimeProvider());
        QuoteMessage.Configure(collection, sender);
        await using var services = collection.BuildServiceProvider();
        await QuoteMessage.PublishAsync(services);
        await QuoteMessage.PublishAsync(services);
        var result = await Client(services).RunAsync(QuoteMessage.Definition(), new MessageData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<MessageData>(result.WorkflowData);
        Assert.Equal("Devis pour Ada", data.Body!.Content);
        var message = Assert.Single(sender.Messages);
        Assert.Equal(data.MessageId, message.MessageId);
        Assert.NotEmpty(message.MessageId);
        Assert.Equal("email", message.Channel);
        Assert.Equal(["ada@example.com"], message.Recipients);
        Assert.Equal(data.Body, message.Body);
    }

    [Fact]
    public async Task GenericHostRunsAndDrains()
    {
        var result = await Hosting.RunHostedAsync();
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(12.5m, Assert.IsType<QuoteData>(result.WorkflowData).Amount);
    }

    [Theory]
    [InlineData("memory", "InMemoryWorkflowStateRepository")]
    [InlineData("sqlite", "SqliteWorkflowStateRepository")]
    [InlineData("sqlserver", "SqlWorkflowStateRepository")]
    [InlineData("postgresql", "PostgreSqlWorkflowStateRepository")]
    public async Task BuilderChoosesEachStore(string store, string expected)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "sample-" + Guid.NewGuid().ToString("N") + ".db");
        var connection = store switch
        {
            "sqlite" => "Data Source=" + file,
            "sqlserver" => "Server=localhost;Database=IxIFlow;Integrated Security=true;TrustServerCertificate=true",
            "postgresql" => "Host=localhost;Database=ixiflow;Username=ixiflow;Password=example",
            _ => null
        };
        try
        {
            await using var provider = Hosting.Services(store, connection).BuildServiceProvider();
            var repository = provider.GetRequiredService<IWorkflowStateRepository>();
            Assert.Equal(expected, repository.GetType().Name);
            if (store is "memory" or "sqlite")
            {
                var result = await Client(provider).RunAsync(Quote.Definition(), new QuoteData());
                Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
                Assert.Equal(12.5m, Assert.IsType<QuoteData>(result.WorkflowData).Amount);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", ".ixiflow-owner" })
                File.Delete(file + suffix);
        }
    }

    internal static ServiceProvider Services(TimeProvider? time = null)
    {
        var collection = Hosting.Services();
        collection.AddSingleton(time ?? new FakeTimeProvider());
        collection.AddSingleton<IQuoteReservations>(new RecoveryTests.Reservations { Found = true });
        collection.AddTransient<ReserveQuote>();
        return collection.BuildServiceProvider();
    }

    internal static IWorkflowClient Client(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();

    private static (WorkflowDefinition Fluent, WorkflowDefinition Yaml) Definitions(string name) => name switch
    {
        "quote" => (Quote.Definition(), Workflow_quote_workflow.Build()),
        "sequential" => (Batch.Definition(), Workflow_batch_workflow.Build()),
        "all" => (Batch.Definition("all"), Workflow_batch_parallel_workflow.Build()),
        "any" => (Batch.Definition("any"), Workflow_batch_any_workflow.Build()),
        "conditional" => (Batch.Definition("conditional"), Workflow_batch_conditional_workflow.Build()),
        "review" => (Review.Definition(), Workflow_review_workflow.Build()),
        "child" => (Review.Child(), Workflow_review_child_workflow.Build()),
        "reusable" => (Reusable.Definition(), Workflow_reusable_workflow.Build()),
        "reservation" => (Recovery.Definition(), Workflow_reservation_workflow.Build()),
        _ => throw new ArgumentException("Unknown sample", nameof(name))
    };

    private static object Data(string name, bool alternative) => name switch
    {
        "sequential" or "all" or "any" or "conditional" => new BatchData { Weights = alternative ? [] : [2, 3, 4] },
        "reusable" => new QuoteData { Amount = -10m },
        "reservation" => new ReservationData(),
        _ => new QuoteData { Amount = alternative ? 0m : 10m }
    };

    private static async Task<(object Data, string StructureHash)> Execute(WorkflowDefinition definition, object data,
        string name, WorkflowDefinition child)
    {
        await using var services = Services();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var client = Client(services);
        var result = await client.RunAsync(definition, data);
        if (name == "review")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            result = await client.ResumeAsync(result.InstanceId, "review", new ReviewAnswer { Customer = "Grace" });
        }
        if (result.Status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended)
            result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId);
        using var checkpoint = JsonDocument.Parse(saved!.ExecutionStateJson);
        return (result.WorkflowData!, checkpoint.RootElement.GetProperty("StructureHash").GetString()!);
    }

    private sealed class RecordingSender : ITemplateMessageSender
    {
        public List<TemplateMessage> Messages { get; } = [];
        public Task SendAsync(TemplateMessage message, CancellationToken cancellationToken = default)
        {
            if (Messages.All(existing => existing.MessageId != message.MessageId))
                Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
