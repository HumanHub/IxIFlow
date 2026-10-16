using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

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

    [Fact]
    public async Task ItemsAreReadOnlySoAnActivityChangingOneIsNotSeenLater()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("ReadOnlyItems")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<MutateLineActivity>(step => step
                    .Input(activity => activity.Line).From(ctx => ctx.Item(line)))
                .Step<NoteActivity>(step => step
                    .Input(activity => activity.Note).From(ctx => ctx.Item(line).Price.ToString())
                    .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                    .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("3;5;", Data(result).Log);
        Assert.Equal([3, 5], Data(result).Lines.Select(item => item.Price));
    }

    [Fact]
    public void AnOutputOrResultTargetReachedThroughAnItemIsABuildError()
    {
        var output = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("WritesItem")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<CountActivity>(step => step
                    .Output(activity => activity.Count).To(ctx => ctx.Item(line).Price)))
            .Build());
        var into = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("StoresIntoItem")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Customers, (customers, customer) => customers
                .ForEach(ctx => ctx.Item(customer).Lines,
                    (lines, line) => lines.Step<NoopActivity>().Result(ctx => ctx.Item(line).Price),
                    setup => setup.Into(ctx => ctx.Item(customer).Results)))
            .Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, output.Kind);
        Assert.Contains("read-only", output.Message);
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, into.Kind);
        Assert.Contains("not of an item", into.Message);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task ATimeoutThatLeavesTheBodyGivesItsOwnResult()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("ApproveOrDefault")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .WaitFor<LineApproval>("approval", (approval, ctx) => approval.Name == ctx.Item(line).Name,
                        wait => wait
                            .TimeoutAfter(TimeSpan.FromMinutes(10))
                            .OnTimeout(path => path.Result(ctx => -ctx.Item(line).Price)))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();
        var started = await engine.RunAsync(definition, Order(3, 5));
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-0" }));

        time.Advance(TimeSpan.FromMinutes(11));
        await RecoverOnceAsync(services, time);
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(
            [new ItemResult<int>(0, ItemStatus.Completed, 3), new ItemResult<int>(1, ItemStatus.Completed, -5)],
            Data(finished).Doubled);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void ATimeoutThatLeavesTheBodyWithoutAResultIsABuildError()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("TimeoutWithoutResult")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .WaitFor<LineApproval>("approval", configure: wait => wait
                        .TimeoutAfter(TimeSpan.FromMinutes(10))
                        .OnTimeout(path => path.Step<NoopActivity>()))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());

        Assert.Contains("every timeout branch", error.Message);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void AResultMustBeOfExactlyTheStoredType()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("ResultOfASubtype")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body.Step<NoopActivity>().Result(ctx => ctx.Item(line)),
                setup => setup.Into(ctx => ctx.WorkflowData.Objects))
            .Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void AWriteToAnAncestorOfTheTargetOrANestedStepStoringThereIsABuildError()
    {
        var ancestor = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("WritesReport")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<ReportActivity>(step => step
                        .Output(activity => activity.Report).To(ctx => ctx.WorkflowData.Report))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Report.Results))
            .Build());
        var nested = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("NestedStoresThere")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Customers,
                (customers, customer) => customers
                    .ForEach(ctx => ctx.Item(customer).Lines,
                        (lines, line) => lines.Step<NoopActivity>().Result(ctx => ctx.Item(line).Price),
                        setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
                    .Result(ctx => ctx.Index(customer)),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());

        Assert.Contains("stores its results", ancestor.Message);
        Assert.Contains("stores its results", nested.Message);
    }

    [Fact]
    public void TheFingerprintKnowsAHandleByItsStepNotItsLabelAndIncludesTheWholeTarget()
    {
        WorkflowDefinition Define(string label, int group) => Workflow.Create<OrderData>("Fingerprinted")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<NoteActivity>(step => step
                        .Input(activity => activity.Note).From(ctx => ctx.Item(line).Name))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.As(label).Into(ctx => ctx.WorkflowData.Groups[group].Results))
            .Build();

        Assert.Equal(new WorkflowScopeCatalog(Define("line", 0)).Fingerprint,
            new WorkflowScopeCatalog(Define("renamed", 0)).Fingerprint);
        Assert.NotEqual(new WorkflowScopeCatalog(Define("line", 0)).Fingerprint,
            new WorkflowScopeCatalog(Define("line", 1)).Fingerprint);
    }

    [Fact]
    public async Task TheFingerprintIsTheSameBeforeAndAfterTheBodyRuns()
    {
        using var services = Services();
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("StableFingerprint")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) =>
                {
                    var skipped = "none";
                    // The inner lambda is made only when the result runs, and C# keeps it in the closure.
                    body
                        .WaitFor<LineApproval>("approval", (approval, ctx) => approval.Name == ctx.Item(line).Name,
                            wait => wait
                                .TimeoutAfter(TimeSpan.FromMinutes(10))
                                .OnTimeout(path => path.Result(ctx => -ctx.Item(line).Price)))
                        .Result(ctx => new[] { "a", "b" }.Count(name => name != skipped) + ctx.Item(line).Price);
                },
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();
        var before = new WorkflowScopeCatalog(definition).Fingerprint;

        var started = await engine.RunAsync(definition, Order(3));
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-0" }));

        Assert.Equal(before, new WorkflowScopeCatalog(definition).Fingerprint);
    }

    [Fact]
    public async Task BranchesForkedInABodyReadTheItemAcrossAWait()
    {
        using var services = Services();
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("ForkPerLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Parallel(parallel => parallel
                    .Do(branch => branch.Step<NoteActivity>(step => step
                        .Input(activity => activity.Note).From(ctx => ctx.Item(line).Name)
                        .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                        .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))
                    .Do(branch => branch.WaitFor<LineApproval>("approval",
                        (approval, ctx) => approval.Name == ctx.Item(line).Name,
                        wait => wait.Output(approval => approval.Name).To(ctx => ctx.WorkflowData.LastApproved)))))
            .Build();
        var started = await engine.RunAsync(definition, Order(3, 5));

        EventAssert.Rejected(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-1" }),
            EventRejection.NotMatched);
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-0" }));
        var finished = await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-1" });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("line-0;line-1;", Data(finished).Log);
        Assert.Equal("line-1", Data(finished).LastApproved);
    }

    [Fact]
    public async Task AForEachInsideABranchRunsItsItems()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("ListInBranch")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                    .Step<NoteActivity>(step => step
                        .Input(activity => activity.Note).From(ctx => ctx.Item(line).Name)
                        .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                        .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log))))
                .Do(branch => branch.Step<CountActivity>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive))))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("line-0;line-1;", Data(result).Log);
        Assert.Equal(1, Data(result).Expensive);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task AFailureEscapingTheBodyStoresNoResults()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("FailMidList")
            .Step<NoopActivity>()
            .Try(attempt => attempt.ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<FailOnFiveActivity>(step => step.Input(activity => activity.Price).From(ctx => ctx.Item(line).Price))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled)))
            .Catch<InvalidOperationException>(handler => handler.Step<CountActivity>(step => step
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
            .Build();
        var data = Order(3, 5, 7);
        data.Doubled = [new ItemResult<int>(9, ItemStatus.Completed, 9)];

        var result = await Engine(services).RunAsync(definition, data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Expensive);
        Assert.Equal([new ItemResult<int>(9, ItemStatus.Completed, 9)], Data(result).Doubled);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task AFailureHandledInsideTheBodyLetsTheItemFinish()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("HandleInBody")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Try(attempt => attempt.Step<FailOnFiveActivity>(step => step
                        .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)))
                    .Catch<InvalidOperationException>(handler => handler.Step<CountActivity>(step => step
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                        .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(3, 5, 7));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Expensive);
        Assert.Equal([3, 5, 7], Data(result).Doubled.Select(item => item.Value));
    }

    [Fact]
    public async Task CancellingMidListStopsTheRemainingItems()
    {
        using var services = Services();
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("CancelMidList")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .WaitFor<LineApproval>("approval", (approval, ctx) => approval.Name == ctx.Item(line).Name)
                    .Step<CountActivity>(step => step
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                        .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();
        var started = await engine.RunAsync(definition, Order(3, 5, 7));
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-0" }));

        var cancelled = await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        EventAssert.Closed(await engine.ResumeAsync(started.InstanceId, "approval", new LineApproval { Name = "line-1" }));
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Cancelled, saved!.Status);
        Assert.Contains("\"Expensive\":1", saved.WorkflowDataJson);
        Assert.Contains("\"Doubled\":[]", saved.WorkflowDataJson);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task ATimeoutInsideALeavingTimeoutBranchGivesItsOwnResult()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        var definition = Workflow.Create<OrderData>("TwoTimeouts")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .WaitFor<LineApproval>("approval", configure: wait => wait
                        .TimeoutAfter(TimeSpan.FromMinutes(10))
                        .OnTimeout(first => first
                            .WaitFor<LineApproval>("reminder", configure: reminder => reminder
                                .TimeoutAfter(TimeSpan.FromMinutes(10))
                                .OnTimeout(second => second.Result(_ => -2)))
                            .Result(_ => -1)))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build();
        var engine = Engine(services);
        var started = await engine.RunAsync(definition, Order(3));

        time.Advance(TimeSpan.FromMinutes(11));
        await RecoverOnceAsync(services, time);
        // The host's timer may run the first timeout; the reminder must be open before time moves on.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId, Key: "reminder"))).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The reminder never opened");
            await Task.Delay(10);
        }
        time.Advance(TimeSpan.FromMinutes(11));
        await RecoverOnceAsync(services, time);
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal([new ItemResult<int>(0, ItemStatus.Completed, -2)], Data(finished).Doubled);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void ATimeoutInsideALeavingTimeoutBranchWithoutAResultIsABuildError()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("InnerWithoutResult")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .WaitFor<LineApproval>("approval", configure: wait => wait
                        .TimeoutAfter(TimeSpan.FromMinutes(10))
                        .OnTimeout(first => first
                            .WaitFor<LineApproval>("reminder", configure: reminder => reminder
                                .TimeoutAfter(TimeSpan.FromMinutes(10))
                                .OnTimeout(second => second.Step<NoopActivity>()))
                            .Result(_ => -1)))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());

        Assert.Contains("every timeout branch", error.Message);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public void ATargetTheRulesCannotNameIsABuildErrorWhileResultsAreStored()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("UnnamedTarget")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<ResultsActivity>(step => step
                        .Output(activity => activity.Results).To(ctx => ctx.WorkflowData.Groups.First().Results))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Doubled))
            .Build());

        Assert.Contains("cannot be shown to write workflow data", error.Message);
    }

    [Fact]
    public void TheHandleCheckSeesHandlesInInterpretedExpressions()
    {
        var line = new ItemScope<OrderLine>("line");
        System.Linq.Expressions.Expression<Func<WorkflowContext<OrderData>, string>> read = ctx => ctx.Item(line).Name;

        var interpreted = read.Compile(preferInterpretation: true);

        Assert.Same(line, Assert.Single(ItemCaptures.Of(interpreted)));
    }

    [Fact]
    public async Task ABranchCleaningUpAfterASiblingFailedStillReadsTheItem()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("CleanupAfterSiblingFailed")
            .Step<NoopActivity>()
            .Try(attempt => attempt.ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Parallel(parallel => parallel
                    .Do(branch => branch.Step<FailOnFiveActivity>(step => step
                        .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)))
                    .Do(branch => branch
                        .Try(waiting => waiting.WaitFor<LineApproval>("approval"))
                        .Finally(cleanup => cleanup.Step<NoteActivity>(step => step
                            .Input(activity => activity.Note).From(ctx => ctx.Item(line).Name)
                            .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                            .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))))))
            .Catch<InvalidOperationException>(handler => handler.Step<CountActivity>(step => step
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Expensive)
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Expensive)))
            .Build();

        var result = await Engine(services).RunAsync(definition, Order(5));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Expensive);
        Assert.Equal("line-0;", Data(result).Log);
    }

    [Fact]
    public async Task ABranchCleaningUpOnCancellationStillReadsTheItem()
    {
        using var services = Services();
        var engine = Engine(services);
        var definition = Workflow.Create<OrderData>("CleanupOnCancel")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Parallel(parallel => parallel
                    .Do(branch => branch
                        .Try(waiting => waiting.WaitFor<LineApproval>("approval"))
                        .Finally(cleanup => cleanup.Step<NoteActivity>(step => step
                            .Input(activity => activity.Note).From(ctx => ctx.Item(line).Name)
                            .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                            .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log))))
                    .Do(branch => branch.WaitFor<LineApproval>("other"))))
            .Build();
        var started = await engine.RunAsync(definition, Order(3, 5));

        var cancelled = await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Contains("\"Log\":\"line-0;\"", saved!.WorkflowDataJson);
    }

    [Fact]
    public void AnOutputThroughACastOfTheItemIsABuildError()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("WritesCastItem")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<CountActivity>(step => step
                    .Output(activity => activity.Count).To(ctx => ((IPriced)ctx.Item(line)).Price)))
            .Build());

        Assert.Contains("read-only", error.Message);
    }

    [Fact]
    public void TheFingerprintKnowsWhichHandleAnExpressionMadeElsewhereReads()
    {
        static System.Linq.Expressions.Expression<Func<WorkflowContext<OrderData>, List<ItemResult<int>>>> GroupOf(
            ItemScope<OrderLine> handle) => ctx => ctx.WorkflowData.Groups[ctx.Index(handle)].Results;
        Assert.NotEqual(new WorkflowScopeCatalog(DefineWith(true)).Fingerprint,
            new WorkflowScopeCatalog(DefineWith(false)).Fingerprint);

        // The innermost step stores through one of the two enclosing handles of the same type.
        WorkflowDefinition DefineWith(bool byOuter) => Workflow.Create<OrderData>("TargetFactory")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (outer, first) => outer
                .ForEach(ctx => ctx.WorkflowData.Lines, (middle, second) => middle
                    .ForEach(ctx => ctx.WorkflowData.Lines,
                        (inner, third) => inner.Step<NoopActivity>().Result(ctx => ctx.Index(third)),
                        setup => setup.Into(GroupOf(byOuter ? first : second)))))
            .Build();
    }

    [Fact]
    public void AnOutputThroughAUserDefinedConversionIsABuildError()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<OrderData>("ConvertedTarget")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<ResultsActivity>(step => step
                        .Output(activity => activity.Results).To(ctx => ((Report)ctx.WorkflowData).Results))
                    .Result(ctx => ctx.Item(line).Price),
                setup => setup.Into(ctx => ctx.WorkflowData.Report.Results))
            .Build());

        Assert.Contains("cannot be shown to write workflow data", error.Message);
    }

    [Fact]
    public void TheCodeOfABehaviorIncludesTheLocalFunctionsItUses()
    {
        var limit = 1;
        bool Keep(int value) => value > limit;
        Func<int, bool> behavior = least => new[] { 1, 2, 3 }.Count(Keep) > least;

        var parts = LambdaCode.Of(behavior.Method).Split('+');

        Assert.Equal(2, parts.Length);
    }

    [Fact]
    public void TheCodeOfABehaviorIncludesTheLambdasItMakes()
    {
        Func<int, bool> behavior = limit => new[] { 1, 2, 3 }.Count(value => value > limit) > 1;

        var parts = LambdaCode.Of(behavior.Method).Split('+');

        Assert.Equal(2, parts.Length);
    }

    [Fact]
    public async Task AfterACrashRecoveryUsesTheSavedListNotTheSelector()
    {
        var store = new FaultInjectingRepository();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<OrderData>("CrashWithChangedSource")
            .Step<NoopActivity>()
            .ForEach(_ => MutableSource.Lines, (body, line) => body
                .Step<RecoverablePriceLineActivity>(step => step
                    .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)
                    .Input(activity => activity.Index).From(ctx => ctx.Index(line))
                    .Input(activity => activity.TotalSoFar).From(ctx => ctx.WorkflowData.Total)
                    .Input(activity => activity.Log).From(ctx => ctx.WorkflowData.Log)
                    .Output(activity => activity.Total).To(ctx => ctx.WorkflowData.Total)
                    .Output(activity => activity.Log).To(ctx => ctx.WorkflowData.Log)))
            .Build();
        MutableSource.Lines = Order(3, 5, 7).Lines;
        store.FailCommit = instance => instance.WorkflowDataJson.Contains("\"Total\":8");
        using (var first = Services(store: store))
            await Assert.ThrowsAnyAsync<Exception>(() => Engine(first).RunAsync(definition, Order(),
                new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        // Were the list read again, recovery would price these instead.
        MutableSource.Lines = Order(100, 100, 100, 100).Lines;

        using var second = Services(store: store);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var recovered = await Engine(second).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(15, Data(recovered).Total);
        Assert.Equal("0:3:0;1:5:3;2:7:8;", Data(recovered).Log);
    }

    [Fact]
    public async Task AnOutputCanWriteTheElementPickedByTheIndex()
    {
        using var services = Services();
        var definition = Workflow.Create<OrderData>("RowPerLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Step<PriceLineActivity>(step => step
                    .Input(activity => activity.Price).From(ctx => ctx.Item(line).Price)
                    .Output(activity => activity.Total).To(ctx => ctx.WorkflowData.Rows[ctx.Index(line)].Value)))
            .Build();
        var data = Order(3, 5, 7);
        data.Rows = [new Row(), new Row(), new Row()];

        var result = await Engine(services).RunAsync(definition, data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal([3, 5, 7], Data(result).Rows.Select(row => row.Value));
    }

    [Fact]
    public async Task ARequestSubjectReadsTheItem()
    {
        using var services = Services();
        var engine = Engine(services);
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("AskAboutLine")
            .Step<NoopActivity>()
            .ForEach(ctx => ctx.WorkflowData.Lines, (body, line) => body
                .Request<AskActivity>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(line).Name),
                    subject: ctx => ctx.Item(line).Name)
                .WaitFor<LineApproval>("approval"))
            .Build();
        await engine.RunAsync(definition, Order(1, 2));
        var first = Assert.Single(outbox.Sent);
        Assert.True(ReplyToken.TryRead(first.Token, out _, out _, out var subject));
        Assert.Equal(ReplyToken.Digest("line-0"), subject);

        EventAssert.Consumed(await engine.ReplyAsync(first.Token, new LineApproval { Name = "line-0" }));

        Assert.True(ReplyToken.TryRead(outbox.Sent[1].Token, out _, out _, out subject));
        Assert.Equal(ReplyToken.Digest("line-1"), subject);
    }

    private static OrderData Order(params int[] prices) =>
        new() { Lines = prices.Select((price, index) => Line(index, price)).ToList() };

    private static OrderLine Line(int index, int price) => new() { Name = $"line-{index}", Price = price };

    private static ServiceProvider Services(Action<IxIFlowOptions>? configure = null,
        IWorkflowStateRepository? store = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (time != null)
            services.AddSingleton(time);
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

    private static async Task RecoverOnceAsync(IServiceProvider services, TimeProvider time)
    {
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
            time);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    /// <summary>A list a selector reads from outside the workflow data, changed between runs by one test.</summary>
    private static class MutableSource
    {
        public static List<OrderLine> Lines { get; set; } = [];
    }

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
        public List<ItemResult<object>> Objects { get; set; } = [];
        public List<Row> Rows { get; set; } = [];
        public List<Group> Groups { get; set; } = [];
        public Report Report { get; set; } = new();

        public static explicit operator Report(OrderData data) => data.Report;
    }

    public sealed class Row
    {
        public int Value { get; set; }
    }

    public sealed class Group
    {
        public List<ItemResult<int>> Results { get; set; } = [];
    }

    public sealed class Report
    {
        public List<ItemResult<int>> Results { get; set; } = [];
    }

    public interface IPriced
    {
        int Price { get; set; }
    }

    public sealed class OrderLine : IPriced
    {
        public string Name { get; set; } = "";
        public int Price { get; set; }
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public List<OrderLine> Lines { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
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

    public sealed class MutateLineActivity : IAsyncActivity
    {
        public OrderLine Line { get; set; } = new();

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Line.Price = 100;
            return Task.CompletedTask;
        }
    }

    public sealed class FailOnFiveActivity : IAsyncActivity
    {
        public int Price { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Price == 5 ? throw new InvalidOperationException("five") : Task.CompletedTask;
    }

    public sealed class ReportActivity : IAsyncActivity
    {
        public Report Report { get; set; } = new();

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
