using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A sequential <c>ForEach</c> runs its body once per item of a list saved when the step starts, each item
/// seeing the changes of the items before it (D-34, spec E-11). The body reads its item with
/// <c>ctx.Item(item)</c> and <c>ctx.Index(item)</c> wherever a context is given, and can end with a result
/// stored per item (E-13). The rules of handles, results and targets are checked when the workflow is built.
/// </summary>
[Trait("Feature", "control-flow")]
[Trait("Spec", "E-11")]
public sealed class ForEachTests
{
    [Fact]
    public async Task EachItemSeesTheChangesOfTheItemsBeforeIt()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("PriceLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<PriceLineActivity>(step => step
                    .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)
                    .Input(activity => activity.Index).From(ctx => ctx.Index(line))
                    .Input(activity => activity.TotalSoFar).From(ctx => ctx.WorkflowData.Total)
                    .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                    .Output(activity => activity.Total).To(ctx => ctx.WorkflowData.Total)
                    .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5, 7));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(15, Data(result).Total);
        Assert.Equal("0:3:0;1:5:3;2:7:8;", Data(result).Log);
    }

    [Fact]
    public async Task ConditionsInTheBodyReadTheItem()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("CountExpensiveLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .If(ctx => ctx.Item(line).Price > 4, then => then
                    .Step<CountActivity>(step => step
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                        .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive))))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5, 7, 1));

        Assert.Equal(2, Data(result).Expensive);
    }

    [Fact]
    public async Task AWaitInTheBodyTakesOnlyTheAnswerForItsItem()
    {
        using var services = Services();
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("ApprovePerLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .WaitFor<LineApproval>("approval", (approval, ctx) => approval.Name == ctx.Item(line).Name,
                    wait => wait.Output(approval => approval.Name).To(ctx => ctx.WorkflowData.LastApproved)))
            .Build();
        var started = await engine.RunAsync(definition, Order(3, 5));
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        // The first line waits; an answer for the second is not its answer.
        EventAssert.Rejected(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-1" }),
            EventRejection.NotMatched);
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-0" }));
        var finished = await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-1" });

        EventAssert.Consumed(finished);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("line-1", Data(finished).LastApproved);
    }

    [Fact]
    public async Task ANestedBodyReadsTheOuterItem()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("NestedLists")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Customers, (customers, customer) => customers
                .ForEach(ctx => ctx.Item(customer).Lines, (lines, line) => lines
                    .Step<NoteActivity>(step => step
                        .Input(activity => activity.Note).From(ctx =>
                            $"{ctx.Item(customer).Name}/{ctx.Index(customer)}:{ctx.Item(line).Name}/{ctx.Index(line)}")
                        .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                        .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)),
                    setup => setup.As("line")),
                setup => setup.As("customer"))
            .Build();
        var data = new OrderData
        {
            Customers =
            [
                new Customer { Name = "ann", Lines = [Line(0, 1), Line(1, 2)] },
                new Customer { Name = "bob", Lines = [] },
                new Customer { Name = "cid", Lines = [Line(0, 3)] }
            ]
        };

        var result = await Engine(services).RunAsync(definition, data);

        Assert.Equal("ann/0:line-0/0;ann/0:line-1/1;cid/2:line-0/0;", Data(result).Log);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task ResultsAreStoredOnePerItemInItemOrder()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("DoubleLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<NoopActivity>()
                    .Result(ctx => ctx.Item(line).Price * 2),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5, 7));

        Assert.Equal(
            [
                new ItemResult<int>(0, ItemStatus.Completed, 6),
                new ItemResult<int>(1, ItemStatus.Completed, 10),
                new ItemResult<int>(2, ItemStatus.Completed, 14)
            ],
            Data(result).Doubled);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task AnEmptyListCompletesAtOnceWithNoResults()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("NoLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body.Step<NoopActivity>().Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Step<CountActivity>(step => step
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive))
            .Build();
        var data = Order();
        data.Doubled = null!;

        var result = await Engine(services).RunAsync(definition, data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Empty(Data(result).Doubled);
        Assert.Equal(1, Data(result).Expensive);
    }

    [Fact]
    public async Task ANullListFailsTheStepIntoItsCatch()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("NullLines")
            .Step<NoopActivity>()
            .Try(body => body.ForEach(ctx => (List<OrderLine>?)null, (items, line) => items.Step<NoopActivity>()))
            .Catch<InvalidOperationException>(handler => handler.Step<CountActivity>(step => step
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Expensive);
    }

    [Fact]
    public async Task AListOverTheLimitStopsTheInstanceForAnOperatorBeforeAnyItemRuns()
    {
        using var services = Services(options => options.MaxForEachItems = 2);
        var definition = Workflow.Create<OrderData>("TooManyLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<CountActivity>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(1, 2, 3));

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Contains("limit of 2 items", result.ErrorMessage);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Contains("\"Expensive\":0", saved!.WorkflowDataJson);
    }

    [Fact]
    public async Task TheListIsSavedOnceSoItemsAddedDuringTheStepDoNotRun()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("GrowingLines")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<AddLineActivity>(step => step
                    .Input(activity => activity.Lines).From(ctx => ctx.WorkflowData.Lines)
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                    .Output(activity => activity.Lines).To(ctx => ctx.WorkflowData.Lines)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(1, 2));

        Assert.Equal(2, Data(result).Expensive);
        Assert.Equal(4, Data(result).Lines.Count);
    }

    [Fact]
    public async Task EachItemIsANewOccurrenceSoEachRequestGetsItsOwnToken()
    {
        using var services = Services();
        var engine = Engine(services);
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("AskPerLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Request<AskActivity>("approval", step => step
                    .Input(activity => activity.Name).From(ctx => ctx.Item(line).Name))
                .WaitFor<LineApproval>("approval", configure: wait => wait
                    .Output(approval => approval.Name).To(ctx => ctx.WorkflowData.LastApproved)))
            .Build();
        var started = await engine.RunAsync(definition, Order(1, 2));
        var first = Assert.Single(outbox.Sent);

        EventAssert.Consumed(await engine.ReplyAsync(first.Token, new LineApproval { Name = first.Name }));
        var second = outbox.Sent[1];
        Assert.Equal(("line-0", "line-1"), (first.Name, second.Name));
        Assert.NotEqual(first.Token, second.Token);
        // The first item's request was answered; its token cannot answer the second item's.
        EventAssert.Rejected(await engine.ReplyAsync(first.Token, new LineApproval { Name = "again" }),
            EventRejection.WaitAnswered);
        var finished = await engine.ReplyAsync(second.Token, new LineApproval { Name = second.Name });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("line-1", Data(finished).LastApproved);
        Assert.Equal(started.InstanceId, finished.InstanceId);
    }

    [Fact]
    public async Task AfterACrashRecoveryContinuesTheSameItems()
    {
        var store = new FaultInjectingRepository();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<OrderData>("CrashMidList")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<RecoverablePriceLineActivity>(step => step
                    .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)
                    .Input(activity => activity.Index).From(ctx => ctx.Index(line))
                    .Input(activity => activity.TotalSoFar).From(ctx => ctx.WorkflowData.Total)
                    .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                    .Output(activity => activity.Total).To(ctx => ctx.WorkflowData.Total)
                    .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))
            .Build();
        // The host dies when it saves the second line's total.
        store.FailCommit = instance => instance.WorkflowDataJson.Contains("\"Total\":8");
        using (var first = Services(store: store))
            await Assert.ThrowsAnyAsync<Exception>(() => Engine(first).RunAsync(definition, Order(3, 5, 7),
                new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;

        using var second = Services(store: store);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var recovered = await Engine(second).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(15, Data(recovered).Total);
        Assert.Equal("0:3:0;1:5:3;2:7:8;", Data(recovered).Log);
    }

    [Fact]
    public async Task AChildsInputsReadTheItem()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("NameChild")
            .Step<NoteActivity>(step => step
                .Input(activity => activity.Note).From(ctx => ctx.WorkflowData.Name)
                .Input(activity => activity.Log).From(_ => "")
                .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Echo))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
        var definition = Workflow.Create<OrderData>("ChildPerLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Invoke<ChildData>("NameChild", 1, step => step
                    .Input(data => data.Name).From(ctx => ctx.Item(line).Name)
                    .Output(data => data.Echo).To(ctx => ctx.WorkflowData.LastApproved)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(1, 2));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("line-1;", Data(result).LastApproved);
    }

    [Fact]
    public void AHandleUsedOutsideItsBodyIsABuildError()
    {
        ItemScope<OrderLine>? leaked = null;
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("LeakedHandle")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) =>
            {
                leaked = line;
                body.Step<NoopActivity>();
            })
            .Step<NoteActivity>(step => step
                .Input(activity => activity.Note).From(ctx => ctx.Item(leaked!).Name))
            .Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("outside its ForEach body", error.Message);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void AResultOnlyEndsAForEachBody()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("ResultOutside")
            .Step<NoopActivity>()
            .Result(_ => 1)
            .Build());
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("ResultNotLast")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Result(ctx => ctx.Item(line).Price)
                .Step<NoopActivity>())
            .Build());
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void ResultsStoredWithIntoNeedABodyResultOfTheirTypeAndATargetTheBodyDoesNotWrite()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("IntoWithoutResult")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body.Step<NoopActivity>(),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("IntoOfAnotherType")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body.Step<NoopActivity>().Result(ctx => ctx.Item(line).Name),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());
        var writes = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("BodyWritesTarget")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<ResultsActivity>(step => step
                        .Output(activity => activity.Results).To(ctx => ctx.WorkflowData.Doubled))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());
        Assert.Contains("stores its results", writes.Message);
    }

    private static OrderData Order(params int[] prices) =>
        new() { Lines = prices.Select((price, index) => Line(index, price)).ToList() };

    private static OrderLine Line(int index, int price) => new() { Name = $"line-{index}", Price = price };

    private static ServiceProvider Services(Action<IxIFlowOptions>? configure = null,
        IWorkflowStateRepository? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (store != null)
            services.AddSingleton(store);
        if (configure != null)
            services.AddIxIFlow(configure);
        else
            services.AddIxIFlow();
        services.AddSingleton<Outbox>();
        services.AddTransient<AskActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();

    private static OrderData Data(WorkflowExecutionResult result) => Assert.IsType<OrderData>(result.WorkflowData);

    public sealed class OrderData
    {
        public List<OrderLine> Lines { get; set; } = [];
        public List<Customer> Customers { get; set; } = [];
        public int Total { get; set; }
        public int Expensive { get; set; }
        public string Log { get; set; } = "";
        public string LastApproved { get; set; } = "";
        public List<ItemResult<int>> Doubled { get; set; } = [];
    }

    public sealed class OrderLine
    {
        public string Name { get; set; } = "";
        public int Price { get; set; }
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public List<OrderLine> Lines { get; set; } = [];
    }

    public sealed class ChildData
    {
        public string Name { get; set; } = "";
        public string Echo { get; set; } = "";
    }

    public sealed class LineApproval
    {
        public string Name { get; set; } = "";
    }

    public sealed class Outbox
    {
        private readonly List<(string Token, string Name)> _sent = [];

        public IReadOnlyList<(string Token, string Name)> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public void Send(string token, string name)
        {
            lock (_sent)
                _sent.Add((token, name));
        }
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class PriceLineActivity : IAsyncActivity
    {
        public int Price { get; set; }
        public int Index { get; set; }
        public int TotalSoFar { get; set; }
        public int Total { get; set; }
        public string Log { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Log += $"{Index}:{Price}:{TotalSoFar};";
            Total = TotalSoFar + Price;
            return Task.CompletedTask;
        }
    }

    /// <summary>Prices a line; an attempt lost in a crash is run again.</summary>
    public sealed class RecoverablePriceLineActivity : IAsyncActivity, IRecoverableActivity<bool>
    {
        public int Price { get; set; }
        public int Index { get; set; }
        public int TotalSoFar { get; set; }
        public int Total { get; set; }
        public string Log { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Log += $"{Index}:{Price}:{TotalSoFar};";
            Total = TotalSoFar + Price;
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    public sealed class CountActivity : IAsyncActivity
    {
        public int Count { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    public sealed class NoteActivity : IAsyncActivity
    {
        public string Note { get; set; } = "";
        public string Log { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Log += Note + ";";
            return Task.CompletedTask;
        }
    }

    public sealed class AddLineActivity : IAsyncActivity
    {
        public List<OrderLine> Lines { get; set; } = [];
        public int Count { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Lines = [.. Lines, new OrderLine { Name = "added", Price = 1 }];
            Count++;
            return Task.CompletedTask;
        }
    }

    public sealed class ResultsActivity : IAsyncActivity
    {
        public List<ItemResult<int>> Results { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class AskActivity(Outbox outbox) : IRequestActivity<LineApproval>
    {
        public string Name { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            outbox.Send(context.Reply!.Token, Name);
            return Task.CompletedTask;
        }
    }
}
