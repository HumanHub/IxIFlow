using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A saga error handler chooses how much to undo and what follows (D-36): a retry resumes where the undo
/// stopped and keeps its requests' waits and answers, a saga that gives up undoes what it kept, a handler that
/// ends the saga cannot keep a request whose answer the saga has not read, the step <c>CompensateUpTo</c> stops
/// at is in the saga once, a handler with no <c>Then...</c> keeps its choice, and a request's compensation
/// reads the request's answer.
/// </summary>
[Trait("Feature", "saga")]
public sealed class SagaHandlerTests
{
    [Theory]
    [Trait("Spec", "A-11")]
    [InlineData("continue")]
    [InlineData("terminate")]
    [InlineData("raise")]
    public void AHandlerThatEndsTheSagaCannotKeepARequestItHasNotReadTheAnswerTo(string then)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => PaymentSaga(handler =>
        {
            var undo = handler.CompensateUpTo<ReserveStock>();
            if (then == "continue")
                undo.ThenContinue();
            else if (then == "terminate")
                undo.ThenTerminate();
        }));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("keeps request 'payment' (CreatePayment), but its WaitFor(\"payment\") comes after " +
            "ReserveStock, where the undo stops", error.Message);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void KeepingEveryStepIsRefusedWhenAStepCanFailBeforeARequestsAnswerIsRead()
    {
        var error = Assert.Throws<WorkflowEngineException>(() =>
            PaymentSaga(handler => handler.CompensateNone().ThenContinue()));

        Assert.Contains("keeps request 'payment'", error.Message);
        // A request whose answer is read straight after it leaves nothing to fail in between.
        Workflow.Create<ShopData>("AnsweredAtOnce")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .WaitFor<PaymentReceived>("payment")
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenContinue())
            .Build();
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void ARetryOrAnAnswerReadBeforeTheBoundaryKeepsTheRequestSafely()
    {
        PaymentSaga(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(2));
        PaymentSaga(handler => handler.Compensate().ThenContinue());
        Workflow.Create<ShopData>("AnsweredBeforeTheBoundary")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .WaitFor<PaymentReceived>("payment")
                .Step<ReserveStock>()
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenContinue())
            .Build();
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void TheStepAnUndoStopsAtIsInTheSagaOnce()
    {
        var absent = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ShopData>("NoSuchStep")
            .Step<ValidateOrder>()
            .Saga(saga => saga.Step<ReserveStock>().Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenTerminate())
            .Build());
        Assert.Contains("compensates up to PackOrder, which is not a step of the saga", absent.Message);

        var twice = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ShopData>("StepTwice")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>()
                .OutcomeOn(ctx => ctx.WorkflowData.Express, outcomes => outcomes
                    .Outcome(true, branch => branch.Step<ReserveStock>())
                    .DefaultOutcome(branch => branch.Step<PackOrder>()))
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenTerminate())
            .Build());
        Assert.Contains("compensates up to ReserveStock, which is a step of the saga more than once", twice.Message);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AHandlerWithNoThenKeepsItsUndoAndRaisesTheError()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var definition = Workflow.Create<ShopData>("UndoThenRaise")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>())
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        // Packing is undone; the stock stays reserved, and the shipping failure is raised.
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains(nameof(ShippingFailed), result.ErrorType);
        Assert.Equal(["ReserveStock", "PackOrder", "ShipOrder", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryResumesAtTheStepTheUndoReachedAndRunsKeptStepsOnce()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var definition = Workflow.Create<ShopData>("ResumeAtPacking")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<PackOrder>(step => step
                    .Input(activity => activity.Reservation).From(ctx => ctx.PreviousStep.Reservation)
                    .CompensateWith<UnpackOrder>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenRetry(2))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        // The stock is reserved once; packing runs again with the reservation it saw the first time.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "PackOrder R-1", "ShipOrder", "undo PackOrder", "PackOrder R-1", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryThatUndidNothingStartsAtTheFailingStep()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var definition = Workflow.Create<ShopData>("ResumeAtShipping")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenRetry(2))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "PackOrder", "ShipOrder", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryResumesInsideTheOutcomeBranchItLeft()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var definition = Workflow.Create<ShopData>("ResumeInBranch")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .OutcomeOn(ctx => ctx.WorkflowData.Express, outcomes => outcomes
                    .Outcome(true, branch => branch
                        .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                        .Step<ShipOrder>())
                    .DefaultOutcome(branch => branch.Step<ShipOrder>())))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenRetry(2))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ShopData { Express = true });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "PackOrder", "ShipOrder", "undo PackOrder", "PackOrder", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryGivesAKeptRequestsWaitForTheAnswerItTookBefore()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("AnsweredThenResumed")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus))
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        await engine.RunAsync(definition, new ShopData());
        var token = Assert.Single(journal.Tokens);

        var result = await engine.ReplyAsync(token, new PaymentReceived { Status = "paid" });

        // The payment is created once; the retried pass takes its answer again without a new wait.
        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("paid", Data(result).PaymentStatus);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
        EventAssert.Rejected(await engine.ReplyAsync(token, new PaymentReceived { Status = "paid" }),
            EventRejection.WaitAnswered);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AKeptRequestKeepsItsWaitAndTokenThroughTheRetryAndAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal { ShippingFailures = 1 };
        var definition = Workflow.Create<ShopData>("KeptUntilAnswered")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        using (var first = Services(repository, journal))
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new ShopData())).Status);

        // Another host, reading the saved instance, takes the answer to the first and only payment.
        using var second = Services(repository, journal);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var result = await second.GetRequiredService<IWorkflowClient>()
            .ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("paid", Data(result).PaymentStatus);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
    }

    // The same inside a parallel branch: the kept request and its wait belong to the branch, which another host
    // reads back from its own stored part (A-08), and the answer still reaches the retried saga.
    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AKeptRequestInsideAParallelBranchKeepsItsWaitThroughTheRetryAndAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal { ShippingFailures = 1 };
        var definition = Workflow.Create<ShopData>("KeptInABranch")
            .Step<ValidateOrder>()
            .Parallel(parallel => parallel
                .Do(branch => branch
                    .Step<ValidateOrder>()
                    .Saga(saga => saga
                        .Request<CreatePayment>("payment")
                        .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                        .Step<ShipOrder>()
                        .WaitFor<PaymentReceived>("payment", configure: wait => wait
                            .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
                    .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(2)))
                .Do(branch => branch.Step<ValidateOrder>()))
            .Build();
        using (var first = Services(repository, journal))
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new ShopData())).Status);

        using var second = Services(repository, journal);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var result = await second.GetRequiredService<IWorkflowClient>()
            .ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("paid", Data(result).PaymentStatus);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnAnswerThatArrivesWhileTheSagaUndoesIsKeptForTheRetry()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal { ShippingFailures = 1 };
        using var services = Services(repository, journal);
        var gate = services.GetRequiredService<Gate>();
        gate.Armed = true;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<ShopData>("AnsweredWhileUndoing")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                // Its predicate is checked when the answer arrives, while the saga is still undoing.
                .WaitFor<PaymentReceived>("payment", (received, _) => received.Status != "pending", wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var run = engine.RunAsync(definition, new ShopData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var reply = engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" }, "paid-1");
        while (await ((IWorkflowInbox)repository).GetEventAsync(instanceId, "paid-1") == null)
            await Task.Delay(10);
        gate.Release.TrySetResult();
        await run;

        EventAssert.Consumed(await reply);
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("paid", Data(finished).PaymentStatus);
        Assert.Equal(1, journal.Entries.Count(entry => entry == "CreatePayment"));
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void AnAnswerIsCheckedWithoutAPreviousStepItsWaitForHasNotReached()
    {
        var definition = Workflow.Create<ShopData>("CheckedEarly")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment", (received, _) => received.Status != "pending"))
            .Build();
        var wait = definition.Steps.Single(step => step.StepType == WorkflowStepType.Saga).SequenceSteps[^1];

        // The run is elsewhere, so its previous step is another activity than the one before the WaitFor.
        var elsewhere = new ReserveStock(new Journal());
        Assert.True(Core.Runtime.WorkflowValueBinding.Matches(wait, new PaymentReceived { Status = "paid" },
            new ShopData(), elsewhere));
        Assert.False(Core.Runtime.WorkflowValueBinding.Matches(wait, new PaymentReceived { Status = "pending" },
            new ShopData(), elsewhere));
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task RunningOutOfRetriesUndoesTheKeptStepsAndClosesTheKeptWait()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("RetriesRunOut")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step.CompensateWith<RefundPayment>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment"))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(1))
            .Build();

        var result = await engine.RunAsync(definition, new ShopData());

        // The payment kept for the retry is refunded once the retry has failed too.
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains(nameof(SagaTerminatedException), result.ErrorType);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder",
            "undo ReserveStock", "undo CreatePayment"], journal.Entries);
        EventAssert.Rejected(await engine.ReplyAsync(Assert.Single(journal.Tokens),
            new PaymentReceived { Status = "paid" }), EventRejection.WaitCancelled);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AFailedUndoDuringARetryUndoesTheKeptStepsAndEndsTheSaga()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var definition = Workflow.Create<ShopData>("UndoFailsBeforeRetry")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<BrokenRelease>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("compensation failed", result.ErrorMessage);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock failed", "undo PackOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AFailingRetryHandlerUndoesTheKeptStepsAndRaisesItsError()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var definition = Workflow.Create<ShopData>("HandlerFails")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.Step<NotifyCustomer>()
                .CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains(nameof(SagaTerminatedException), result.ErrorType);
        Assert.Contains("the error handler failed: the customer could not be told", result.ErrorMessage);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "NotifyCustomer", "undo PackOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingWhileARetryUndoesUndoesTheKeptSteps()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var gate = services.GetRequiredService<Gate>();
        gate.Armed = true;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<ShopData>("CancelledWhileUndoing")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();
        var run = engine.RunAsync(definition, new ShopData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await engine.CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        gate.Release.TrySetResult();
        await run;
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnApprovalFromAnEarlierAttemptIsNotAskedAgainByALaterBroaderRetry()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        journal.ShippingErrors.Enqueue(new ShippingFailed("the parcel was refused"));
        journal.ShippingErrors.Enqueue(new CarrierDown("the carrier is down"));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ApprovedOnce")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .WaitFor<Approval>("approval")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenRetry(3))
            .OnError<CarrierDown>(handler => handler.Compensate().ThenRetry(3))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());

        // Shipping fails twice: the first retry starts at shipping, the second at the top, where the approval
        // already given is replayed.
        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "ShipOrder", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AStepWhoseOutputsFailedIsUndoneAndRunAgain()
    {
        ShopData.FragileFailures = 1;
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        var definition = Workflow.Create<ShopData>("OutputsFailed")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step
                    .Output(activity => activity.Reservation).To(ctx => ctx.WorkflowData.Fragile)
                    .CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<InvalidOperationException>(handler => handler.CompensateNone().ThenRetry(1))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());

        // The reservation was made, so it is undone even though nothing else is, and made again.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "undo ReserveStock", "ReserveStock", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ASecondBroaderRetryStillKeepsTheRequestBothRetriesKept()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        journal.ShippingErrors.Enqueue(new ShippingFailed("the parcel was refused"));
        journal.ShippingErrors.Enqueue(new CarrierDown("the carrier is down"));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("RetriedTwice")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenRetry(3))
            .OnError<CarrierDown>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();
        await engine.RunAsync(definition, new ShopData());

        var result = await engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["CreatePayment", "ReserveStock", "PackOrder", "ShipOrder", "undo PackOrder", "PackOrder",
            "ShipOrder", "undo PackOrder", "undo ReserveStock", "ReserveStock", "PackOrder", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryResumesAtItsTargetEvenAsTheSagasFirstStep()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var definition = Workflow.Create<ShopData>("TargetFirst")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .OutcomeOn(ctx => ctx.WorkflowData.Express, outcomes => outcomes
                    .Outcome(true, branch => branch
                        // Undoing the packing clears the flag its branch was chosen by.
                        .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>(undo => undo
                            .Output(activity => activity.Express).To(ctx => ctx.WorkflowData.Express)))
                        .Step<ShipOrder>())
                    .DefaultOutcome(branch => branch.Step<ReserveStock>())))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenRetry(2))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ShopData { Express = true });

        // The retry resumes in the branch it left rather than choosing again.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["PackOrder", "ShipOrder", "undo PackOrder", "PackOrder", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AKeptRequestWhoseAnswerTimedOutTakesItsTimeoutPathAgain()
    {
        var time = new TimerRecordingClock();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("TimedOutThenRetried")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Step<ShipOrder>())))
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenRetry(1))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());

        time.Advance(TimeSpan.FromMinutes(2));
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Once the retry is due, it resumes at the WaitFor, whose timeout path runs again without another wait.
        Assert.Equal(WorkflowExecutionStatus.Faulted, finished.Status);
        Assert.Contains("retry exhausted", finished.ErrorMessage);
        Assert.Equal(["CreatePayment", "ShipOrder", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AWaitWhoseOutputsFailedIsGivenItsEventAgain()
    {
        ShopData.FragileFailures = 1;
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ApprovalOutputsFailed")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .WaitFor<Approval>("approval", configure: wait => wait
                    .Output(approval => approval.Note).To(ctx => ctx.WorkflowData.Fragile))
                .Step<ShipOrder>())
            .OnError<InvalidOperationException>(handler => handler.CompensateNone().ThenRetry(1))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());

        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        // The approval already taken is replayed; nobody is asked again.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnAnswerArrivingWhileTheRetryHandlerWaitsIsKeptAcrossHosts()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal { ShippingFailures = 1 };
        var definition = Workflow.Create<ShopData>("HandlerWaits")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<ShippingFailed>(handler => handler.WaitFor<Approval>("retry")
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        string instanceId;
        using (var first = Services(repository, journal))
        {
            var stopped = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ShopData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, stopped.Status);
            instanceId = stopped.InstanceId;
        }
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock"], journal.Entries);

        // The saga is saved while it undoes, waiting in its handler; another host takes the payment's answer,
        // which the instance keeps while it still waits there, and then lets the retry go.
        using var second = Services(repository, journal);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var engine = second.GetRequiredService<IWorkflowClient>();
        var answered = await engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });
        EventAssert.Consumed(answered);
        Assert.Equal(WorkflowExecutionStatus.Suspended, answered.Status);
        var result = await engine.ResumeAsync(instanceId, "retry", new Approval());

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("paid", Data(result).PaymentStatus);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingWhileARetryHandlerRunsCancelsItsStepsAndUndoesTheKeptSteps()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var gate = services.GetRequiredService<HandlerGate>();
        gate.Armed = true;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<ShopData>("HandlerCancelledWhileRunning")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.Step<HoldHandler>().Step<NotifyCustomer>()
                .WaitFor<Approval>("retry").CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();
        var run = engine.RunAsync(definition, new ShopData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The instance is cancelled while the handler's activity runs, which stops it.
        await engine.CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        gate.Release.TrySetResult();
        await run;
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // No retry follows, so the handler's further steps and its wait are cancelled and the kept packing undone.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARetryHandlersActivityFailingAfterACancellationLeavesTheInstanceCancelled()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var gate = services.GetRequiredService<HandlerGate>();
        gate.Armed = true;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<ShopData>("HandlerFailsAfterCancel")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.Step<StubbornHandler>()
                .CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();
        var run = engine.RunAsync(definition, new ShopData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The handler's activity ignores the cancellation and fails once let go: like a forward activity's, its
        // failure gives way to the cancellation (D-36).
        await engine.CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while ((await repository.GetWorkflowInstanceAsync(instanceId))?.ExecutionStateJson
                   .Contains("\"CancellationUnwind\":true") != true)
                await Task.Delay(20, patience.Token);
        gate.Release.TrySetResult();
        await run;
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "StubbornHandler", "undo PackOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingARetryHandlersNestedSagaUndoesItBeforeTheKeptSteps()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("NestedSagaInHandler")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler
                .WaitFor<Approval>("retry", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Saga(inner => inner
                        .Step<InsureOrder>(step => step.CompensateWith<UninsureOrder>())
                        .WaitFor<Approval>("insured"))))
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!journal.Entries.Contains(nameof(InsureOrder)) ||
               (await engine.GetOpenWaitsAsync(new WaitQuery { InstanceId = started.InstanceId }))
               .All(wait => wait.Key != "insured"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The nested saga never waited");
            await Task.Delay(20);
        }

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "InsureOrder", "undo InsureOrder",
            "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingARetryHandlersChildCancelsTheChildBeforeTheKeptStepsAreUndone()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var journal = new Journal();
        using var services = Services(repository, journal, time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ShopData>("CarrierClaim").Step<ValidateOrder>().WaitFor<Approval>("settled").Build());
        var definition = Workflow.Create<ShopData>("ChildInHandler")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler
                .WaitFor<Approval>("retry", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Invoke<ShopData>("CarrierClaim", 1)))
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorkflowInstance? child = null;
        while (child == null || (await engine.GetOpenWaitsAsync(new WaitQuery { InstanceId = child.InstanceId }))
               .All(wait => wait.Key != "settled"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The handler's child never waited");
            await Task.Delay(20);
            child = (await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: started.InstanceId, Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!))).SingleOrDefault();
        }

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The handler's child is asked to cancel and its end awaited; then the kept packing is undone.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await repository.GetWorkflowInstanceAsync(child.InstanceId))!.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingInsideAFinallyOfARetryHandlerUndoesTheKeptStepsOnceItIsDone()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("FinallyInHandler")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler
                .WaitFor<Approval>("retry", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path
                        .Try(body => body.Step<InsureOrder>())
                        .Finally(cleanup => cleanup.WaitFor<Approval>("cleanup"))))
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await engine.GetOpenWaitsAsync(new WaitQuery { InstanceId = started.InstanceId }))
               .All(wait => wait.Key != "cleanup"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The cleanup never waited");
            await Task.Delay(20);
        }

        // The cancellation lets the Finally finish, then unwinds to the saga, which undoes what it kept.
        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        await engine.ResumeAsync(started.InstanceId, "cleanup", new Approval());
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "InsureOrder", "undo PackOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ANestedHandlerFailingInsideARetryHandlerUndoesTheOuterKeptSteps()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 2;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("NestedHandlerFails")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler
                .WaitFor<Approval>("retry", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path
                        .Saga(inner => inner
                            .Step<InsureOrder>(step => step.CompensateWith<UninsureOrder>())
                            .Step<ShipOrder>())
                        .OnError<ShippingFailed>(innerHandler => innerHandler.Step<NotifyCustomer>()
                            .Compensate().ThenContinue())))
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));

        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The inner handler's failure ends the outer retry handler, which undoes the packing it kept.
        Assert.Equal(WorkflowExecutionStatus.Faulted, finished.Status);
        Assert.Contains("the error handler failed", finished.ErrorMessage);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "InsureOrder", "ShipOrder",
            "undo InsureOrder", "NotifyCustomer", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task EachItemsSagaResumesOnItsOwn()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        journal.FailEachOrderOnce = true;
        var definition = Workflow.Create<ShopData>("SagaPerOrder")
            .Step<ValidateOrder>()
            .ForEach(ctx => ctx.WorkflowData.Orders,
                (body, order) => body
                    .Saga(saga => saga
                        .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                        .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                        .Step<ShipOrder>(step => step.Input(activity => activity.Order).From(ctx => ctx.Item(order))))
                    .OnError<ShippingFailed>(handler => handler.CompensateUpTo<PackOrder>().ThenRetry(2)),
                setup => setup.Parallel())
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ShopData { Orders = ["a", "b", "c"] });

        // Each order's stock is reserved once; each order that failed to ship packs again.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, journal.Entries.Count(entry => entry == "ReserveStock"));
        Assert.Equal(3, journal.Entries.Count(entry => entry == "undo PackOrder"));
        Assert.Equal(6, journal.Entries.Count(entry => entry == "PackOrder"));
        Assert.DoesNotContain("undo ReserveStock", journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void ARequestIsPairedWithTheWaitForInItsOwnBodyWhenCheckingWhatAHandlerKeeps()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ShopData>("SameNameElsewhere")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .OutcomeOn(ctx => ctx.WorkflowData.Express, outcomes => outcomes
                    .Outcome(true, branch => branch
                        .Request<CreatePayment>("payment")
                        .WaitFor<PaymentReceived>("payment"))
                    .DefaultOutcome(branch => branch.Step<PackOrder>()))
                .Step<ReserveStock>()
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment"))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenContinue())
            .Build());

        Assert.Contains("keeps request 'payment'", error.Message);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingWhileARetryHandlerWaitsUndoesTheKeptStepsAndCancels()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("CancelledInHandlerWait")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.WaitFor<Approval>("retry")
                .CompensateUpTo<ReserveStock>().ThenRetry(2))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The handler stops waiting: no retry follows a cancellation, so the kept packing is undone.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnApprovalReplayedOnAnotherPathIsReplayedAgainByALaterPartialRetry()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 0;
        journal.ShippingErrors.Enqueue(new ShippingFailed("the parcel was refused"));
        journal.ShippingErrors.Enqueue(new CarrierDown("the carrier is down"));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ApprovalOnAnotherPath")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .OutcomeOn(ctx => ctx.WorkflowData.Express, outcomes => outcomes
                    .Outcome(true, branch => branch
                        .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>(undo => undo
                            .Output(activity => activity.Express).To(ctx => ctx.WorkflowData.Express))))
                    .DefaultOutcome(branch => branch
                        .Step<InsureOrder>()
                        .Step<LabelOrder>(step => step.CompensateWith<UnlabelOrder>())))
                .WaitFor<Approval>("approval")
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.Compensate().ThenRetry(3))
            .OnError<CarrierDown>(handler => handler.CompensateUpTo<LabelOrder>().ThenRetry(3))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData { Express = true });

        // The first attempt is approved; the second takes the other branch and replays the approval; the third
        // starts at the label and replays it again.
        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ReserveStock", "PackOrder", "ShipOrder", "undo PackOrder", "undo ReserveStock",
            "ReserveStock", "InsureOrder", "LabelOrder", "ShipOrder", "undo LabelOrder", "LabelOrder", "ShipOrder"],
            journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnApprovalTakenInATimeoutPathIsReplayedWhenTheRetryTakesThePathAgain()
    {
        var time = new TimerRecordingClock();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ManualAfterTimeout")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.WaitFor<Approval>("manual").Step<ShipOrder>())))
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenRetry(1))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));
        var timedOut = await engine.AwaitAsync(started.InstanceId).WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (timedOut.Status != WorkflowExecutionStatus.Suspended ||
               (await engine.GetOpenWaitsAsync(new WaitQuery { InstanceId = started.InstanceId }))
               .All(wait => wait.Key != "manual"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The manual approval was never asked for");
            await Task.Delay(20);
            timedOut = await engine.AwaitAsync(started.InstanceId);
        }

        var resuming = engine.ResumeAsync(started.InstanceId, "manual", new Approval());
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await resuming.WaitAsync(TimeSpan.FromSeconds(10));

        // The retry takes the timeout path again and replays the manual approval instead of asking for it.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["CreatePayment", "ShipOrder", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AnApprovalTakenInAPlainWaitsTimeoutPathIsKeptWhileTheRetryWaitsAgain()
    {
        var time = new TimerRecordingClock();
        using var services = Services(time: time);
        var journal = services.GetRequiredService<Journal>();
        journal.ShippingFailures = 1;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ManualAfterPlainTimeout")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .WaitFor<Approval>("pickup", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.WaitFor<Approval>("manual").Step<ShipOrder>())))
            .OnError<ShippingFailed>(handler => handler.CompensateNone().ThenRetry(1))
            .Build();
        var started = await engine.RunAsync(definition, new ShopData());
        time.Advance(TimeSpan.FromMinutes(2));
        await OpenWaitAsync(engine, started.InstanceId, "manual");

        // Shipping fails; the retry waits for the pickup again, which times out again, and the manual approval
        // already given is replayed rather than asked for.
        var resuming = engine.ResumeAsync(started.InstanceId, "manual", new Approval());
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await resuming.WaitAsync(TimeSpan.FromSeconds(10));
        await OpenWaitAsync(engine, started.InstanceId, "pickup");
        time.Advance(TimeSpan.FromMinutes(2));
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(["ReserveStock", "ShipOrder", "ShipOrder"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARequestsCompensationReadsItsAnswerOnAnotherHost()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal();
        var definition = Workflow.Create<ShopData>("AnswerReadElsewhere")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step
                    .CompensateWith<UndoPayment, PaymentReceived>(undo => undo
                        .Input(activity => activity.Answered).From(ctx => ctx.Answered)
                        .Input(activity => activity.Status).From(ctx => ctx.Reply == null ? "none" : ctx.Reply.Status)))
                .WaitFor<PaymentReceived>("payment")
                .WaitFor<Approval>("ship")
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler => handler.Compensate().ThenTerminate())
            .Build();
        string instanceId;
        using (var first = Services(repository, journal))
        {
            var engine = first.GetRequiredService<IWorkflowClient>();
            instanceId = (await engine.RunAsync(definition, new ShopData())).InstanceId;
            EventAssert.Consumed(await engine.ReplyAsync(Assert.Single(journal.Tokens),
                new PaymentReceived { Status = "paid" }));
        }

        using var second = Services(repository, journal);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        await second.GetRequiredService<IWorkflowClient>().ResumeAsync(instanceId, "ship", new Approval());

        Assert.Contains("undo CreatePayment paid True", journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void ChainedCompensationsStillBuildThroughConcreteBuilders()
    {
        // The answer overload has the same arity as the chained one; a lambda that fits both picks the chained
        // one through the interface and through the builder classes alike.
        Workflow.Create<ShopData>("ChainedConcrete")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<PackOrder>()
                .Step<ReserveStock>(setup =>
                {
                    setup.CompensateWith<ReleaseStock, PackOrder>(_ => { });
                    ((SagaActivitySetupBuilder<ShopData, ReserveStock, PackOrder>)setup)
                        .CompensateWith<ReleaseStock, PackOrder>(_ => { });
                    ((SagaActivitySetupInputBuilder<ShopData, ReserveStock, string, PackOrder>)setup
                            .Input(activity => activity.Reservation))
                        .CompensateWith<ReleaseStock, PackOrder>(_ => { });
                    ((SagaActivitySetupOutputBuilder<ShopData, ReserveStock, string, PackOrder>)setup
                            .Output(activity => activity.Reservation))
                        .CompensateWith<ReleaseStock, PackOrder>(_ => { });
                }))
            .Build();
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingWhileAnUndoAwaitsResolutionUndoesTheKeptStepsOnceItIsResolved()
    {
        var store = new FaultInjectingRepository();
        var journal = new Journal();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<ShopData>("UndoAwaitsResolution")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step.CompensateWith<RefundPayment>())
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment"))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>().ThenRetry(3))
            .Build();
        // The host dies as it saves the release, which then awaits an operator.
        store.FailCommit = instance => instance.ExecutionStateJson.Contains("\"PreviousCompensation\":{");
        using (var first = Services(store, journal))
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new ShopData(), new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        using var second = Services(store, journal);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var engine = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await engine.RecoverAsync(instanceId)).Status);

        await engine.CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        // No retry will follow, so the kept payment's wait closes at once, before the release is resolved.
        EventAssert.Rejected(await engine.ReplyAsync(Assert.Single(journal.Tokens),
            new PaymentReceived { Status = "paid" }), EventRejection.WaitCancelled);
        var release = Assert.Single(await engine.GetPendingActivitiesAsync(instanceId));
        await engine.ResolveAsync(instanceId, release.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Once the release is resolved the saga cancels instead of retrying, and the kept steps are undone.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["CreatePayment", "PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder",
            "undo CreatePayment"], journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task CancellingAParkedRetryUndoesTheKeptStepsAndClosesTheKeptWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal();
        using var services = Services(repository, journal);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ShopData>("ParkedThenCancelled")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step.CompensateWith<RefundPayment>())
                .Step<PackOrder>(step => step.CompensateWith<UnpackOrder>())
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment"))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>()
                .ThenRetry(new RetryPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 2, 1)))
            .Build();
        var instanceId = await engine.StartAsync(definition, new ShopData());
        await ParkedAsync(repository, instanceId);

        await engine.CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        var finished = await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        Assert.Equal(["CreatePayment", "PackOrder", "ReserveStock", "ShipOrder", "undo ReserveStock", "undo PackOrder",
            "undo CreatePayment"], journal.Entries);
        EventAssert.Rejected(await engine.ReplyAsync(Assert.Single(journal.Tokens),
            new PaymentReceived { Status = "paid" }), EventRejection.WaitCancelled);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task AParkedRetryResumesOnAnotherHostWithItsKeptRequest()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var journal = new Journal { ShippingFailures = 1 };
        var definition = Workflow.Create<ShopData>("ParkedThenMoved")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>(step => step.CompensateWith<ReleaseStock>())
                .Step<ShipOrder>()
                .WaitFor<PaymentReceived>("payment", configure: wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<ShippingFailed>(handler => handler.CompensateUpTo<ReserveStock>()
                .ThenRetry(new RetryPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 2, 1)))
            .Build();
        string instanceId;
        using (var first = Services(repository, journal, time))
        {
            instanceId = await first.GetRequiredService<IWorkflowClient>().StartAsync(definition, new ShopData());
            await ParkedAsync(repository, instanceId);
        }

        // Another host reads the parked retry, with its restored frames and the kept payment's wait.
        using var second = Services(repository, journal, time);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var engine = second.GetRequiredService<IWorkflowClient>();
        time.Advance(TimeSpan.FromSeconds(3));
        // The retry has not run yet: the first host left before it was due.
        Assert.Equal(1, journal.Entries.Count(entry => entry == nameof(ReserveStock)));
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await engine.RecoverAsync(instanceId)).Status);
        // The recovery ran the retry, which now waits for the payment it kept.
        Assert.Equal(2, journal.Entries.Count(entry => entry == nameof(ShipOrder)));
        var result = await engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["CreatePayment", "ReserveStock", "ShipOrder", "undo ReserveStock", "ReserveStock", "ShipOrder"],
            journal.Entries);
    }

    // Waits until the instance's retry is parked: saved with a retry time and its lease released.
    private static async Task ParkedAsync(IWorkflowStateRepository repository, string instanceId)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await repository.GetWorkflowInstanceAsync(instanceId))?.ExecutionStateJson
               .Contains("\"RetryAfterUtc\":\"") != true)
            await Task.Delay(20, patience.Token);
    }

    // Waits until the instance has an open wait under the key.
    private static async Task OpenWaitAsync(IWorkflowClient engine, string instanceId, string key)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await engine.GetOpenWaitsAsync(new WaitQuery { InstanceId = instanceId }))
               .All(wait => wait.Key != key))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The wait '{key}' never opened");
            await Task.Delay(20);
        }
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARequestsCompensationReadsTheAnswerItsWaitForTook()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();
        var engine = services.GetRequiredService<IWorkflowClient>();
        await engine.RunAsync(AnswerReadingSaga("AnswerTaken", answerFirst: true), new ShopData());

        await engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" });

        Assert.Contains("undo CreatePayment paid True", journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARequestsCompensationReadsAnAnswerItsWaitForHadNotReached()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var journal = new Journal();
        using var services = Services(repository, journal);
        var gate = services.GetRequiredService<Gate>();
        gate.Armed = true;
        var engine = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        var run = engine.RunAsync(AnswerReadingSaga("AnswerKept", answerFirst: false, hold: true), new ShopData(),
            new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var reply = engine.ReplyAsync(Assert.Single(journal.Tokens), new PaymentReceived { Status = "paid" }, "early");
        while (await ((IWorkflowInbox)repository).GetEventAsync(instanceId, "early") == null)
            await Task.Delay(10);
        gate.Release.TrySetResult();
        await run;

        EventAssert.Consumed(await reply);
        Assert.Contains("undo CreatePayment paid True", journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public async Task ARequestsCompensationSeesWhenNoAnswerCame()
    {
        using var services = Services();
        var journal = services.GetRequiredService<Journal>();

        await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(AnswerReadingSaga("NoAnswer", answerFirst: false), new ShopData());

        Assert.Contains("undo CreatePayment none False", journal.Entries);
    }

    [Fact]
    [Trait("Spec", "A-11")]
    public void AnAnswerIsReadOnlyByItsRequestsCompensationWithItsType()
    {
        var mismatch = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ShopData>("WrongAnswerType")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step
                    .CompensateWith<UndoPayment, Approval>(undo => undo
                        .Input(activity => activity.Answered).From(ctx => ctx.Answered)))
                .WaitFor<PaymentReceived>("payment"))
            .Build());
        Assert.Contains("reads an answer of type Approval, but the request is answered with PaymentReceived",
            mismatch.Message);

        var notARequest = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ShopData>("NotARequest")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Step<ReserveStock>(step => step
                    .CompensateWith<UndoPayment, PaymentReceived>(undo => undo
                        .Input(activity => activity.Answered).From(ctx => ctx.Answered))))
            .Build());
        Assert.Contains("ReserveStock is not a request", notARequest.Message);
    }

    // A payment whose compensation reads its answer, undone when shipping fails. With answerFirst the answer
    // is read before shipping; otherwise shipping comes first, after an optional step held by the gate.
    private static WorkflowDefinition AnswerReadingSaga(string name, bool answerFirst, bool hold = false) =>
        Workflow.Create<ShopData>(name)
            .Step<ValidateOrder>()
            .Saga(saga =>
            {
                var paid = saga.Request<CreatePayment>("payment", step => step
                    .CompensateWith<UndoPayment, PaymentReceived>(undo => undo
                        .Input(activity => activity.Answered).From(ctx => ctx.Answered)
                        .Input(activity => activity.Status).From(ctx => ctx.Reply == null ? "none" : ctx.Reply.Status)));
                if (answerFirst)
                    paid.WaitFor<PaymentReceived>("payment").Step<ShipOrder>();
                else if (hold)
                    paid.Step<HoldOrder>().Step<ShipOrder>().WaitFor<PaymentReceived>("payment");
                else
                    paid.Step<ShipOrder>().WaitFor<PaymentReceived>("payment");
            })
            .OnError<ShippingFailed>(handler => handler.Compensate().ThenTerminate())
            .Build();

    private static ShopData Data(WorkflowExecutionResult result) => Assert.IsType<ShopData>(result.WorkflowData);

    // A payment requested in a saga, answered only after the stock is reserved.
    private static WorkflowDefinition PaymentSaga(Action<ISagaErrorBuilder<ShopData, EmptyFault, ValidateOrder>> handler) =>
        Workflow.Create<ShopData>("PaymentSaga")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment")
                .Step<ReserveStock>()
                .WaitFor<PaymentReceived>("payment")
                .Step<ShipOrder>())
            .OnError<ShippingFailed>(handler)
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null, Journal? journal = null,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (time != null)
            services.AddSingleton(time);
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        if (repository != null)
            services.AddSingleton(repository);
        services.AddSingleton(journal ?? new Journal());
        services.AddSingleton<Gate>();
        services.AddSingleton<HandlerGate>();
        foreach (var activity in new[]
                 {
                     typeof(ReserveStock), typeof(ReleaseStock), typeof(BrokenRelease), typeof(PackOrder),
                     typeof(UnpackOrder), typeof(ShipOrder), typeof(HoldOrder), typeof(HoldHandler), typeof(StubbornHandler), typeof(NotifyCustomer),
                     typeof(InsureOrder), typeof(UninsureOrder), typeof(LabelOrder), typeof(UnlabelOrder),
                     typeof(CreatePayment), typeof(RefundPayment), typeof(UndoPayment)
                 })
            services.AddTransient(activity);
        return services.BuildServiceProvider();
    }

    public sealed class ShopData
    {
        /// <summary>How many more times setting <see cref="Fragile"/> fails.</summary>
        public static int FragileFailures;

        public bool Express { get; set; }
        public string PaymentStatus { get; set; } = "";
        public List<string> Orders { get; set; } = [];

        [JsonIgnore]
        public string Fragile
        {
            get => "";
            set
            {
                if (Interlocked.Decrement(ref FragileFailures) >= 0)
                    throw new InvalidOperationException("the reservation could not be stored");
            }
        }
    }

    public sealed class PaymentReceived
    {
        public string Status { get; set; } = "";
    }

    public sealed class Approval
    {
        public string Note { get; set; } = "approved";
    }

    public sealed class ShippingFailed(string message) : Exception(message);

    public sealed class CarrierDown(string message) : Exception(message);

    /// <summary>Holds the activity that waits on it until released, once armed.</summary>
    public sealed class Gate
    {
        public bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PassAsync(CancellationToken cancellationToken)
        {
            if (!Armed)
                return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>What ran and what was undone, in order.</summary>
    public sealed class Journal
    {
        private readonly List<string> _entries = [];
        private readonly List<string> _tokens = [];

        /// <summary>How many more times shipping fails, after the errors queued for it.</summary>
        public int ShippingFailures = int.MaxValue;

        /// <summary>When set, the first shipment of each order fails.</summary>
        public bool FailEachOrderOnce;

        private readonly HashSet<string> _shipped = [];

        public bool FirstShipment(string order)
        {
            lock (_shipped)
                return _shipped.Add(order);
        }

        /// <summary>Errors shipping raises first, one per attempt.</summary>
        public Queue<Exception> ShippingErrors { get; } = new();

        private int _reservations;

        public string NextReservation() => $"R-{Interlocked.Increment(ref _reservations)}";

        public IReadOnlyList<string> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IReadOnlyList<string> Tokens
        {
            get { lock (_entries) return _tokens.ToArray(); }
        }

        public void Add(string entry)
        {
            lock (_entries)
                _entries.Add(entry);
        }

        public void Sent(string token)
        {
            lock (_entries)
                _tokens.Add(token);
        }
    }

    public sealed class ValidateOrder : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class ReserveStock(Journal journal) : IAsyncActivity
    {
        public string Reservation { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(ReserveStock));
            Reservation = journal.NextReservation();
            return Task.CompletedTask;
        }
    }

    public sealed class ReleaseStock(Journal journal, Gate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await gate.PassAsync(cancellationToken);
            journal.Add("undo ReserveStock");
        }
    }

    public sealed class BrokenRelease(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add("undo ReserveStock failed");
            throw new InvalidOperationException("the warehouse is unreachable");
        }
    }

    public sealed class PackOrder(Journal journal) : IAsyncActivity
    {
        public string? Reservation { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(Reservation == null ? nameof(PackOrder) : $"{nameof(PackOrder)} {Reservation}");
            return Task.CompletedTask;
        }
    }

    /// <summary>A second gate, for a handler step, so a test can hold it apart from the undo steps.</summary>
    public sealed class HandlerGate
    {
        public bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class HoldHandler(HandlerGate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (!gate.Armed)
                return;
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed class InsureOrder(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(InsureOrder));
            return Task.CompletedTask;
        }
    }

    public sealed class UninsureOrder(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add("undo InsureOrder");
            return Task.CompletedTask;
        }
    }

    public sealed class LabelOrder(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(LabelOrder));
            return Task.CompletedTask;
        }
    }

    public sealed class UnlabelOrder(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add("undo LabelOrder");
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds until released, whatever its token says, then fails.</summary>
    public sealed class StubbornHandler(HandlerGate gate, Journal journal) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
            journal.Add(nameof(StubbornHandler));
            throw new InvalidOperationException("the handler failed after all");
        }
    }

    public sealed class HoldOrder(Gate gate) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            gate.PassAsync(cancellationToken);
    }

    public sealed class NotifyCustomer(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(NotifyCustomer));
            throw new InvalidOperationException("the customer could not be told");
        }
    }

    public sealed class UnpackOrder(Journal journal) : IAsyncActivity
    {
        public bool Express { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add("undo PackOrder");
            return Task.CompletedTask;
        }
    }

    public sealed class ShipOrder(Journal journal) : IAsyncActivity
    {
        public string? Order { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(ShipOrder));
            if (journal.ShippingErrors.TryDequeue(out var error))
                throw error;
            if (journal.FailEachOrderOnce && Order != null && journal.FirstShipment(Order))
                throw new ShippingFailed($"the parcel for {Order} was refused");
            return Interlocked.Decrement(ref journal.ShippingFailures) >= 0
                ? throw new ShippingFailed("the carrier refused the parcel")
                : Task.CompletedTask;
        }
    }

    public sealed class CreatePayment(Journal journal) : IRequestActivity<PaymentReceived>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add(nameof(CreatePayment));
            journal.Sent(context.Reply!.Token);
            return Task.CompletedTask;
        }
    }

    public sealed class RefundPayment(Journal journal) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add("undo CreatePayment");
            return Task.CompletedTask;
        }
    }

    /// <summary>Cancels a pending payment or refunds a settled one, as its answer says.</summary>
    public sealed class UndoPayment(Journal journal) : IAsyncActivity
    {
        public bool Answered { get; set; }
        public string Status { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            journal.Add($"undo CreatePayment {Status} {Answered}");
            return Task.CompletedTask;
        }
    }
}
