using IxIFlow.Core;
using IxIFlow.Recipes;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The list recipes of spec D-17 that ask several people at once with a parallel <c>ForEach</c>
/// (D-34), as the samples define them, in every profile (J-02). Each scenario asserts what became of
/// every answer it sends.
/// </summary>
[Trait("Spec", "D-17")]
[Trait("Spec", "E-12")]
public sealed class ApproverListScenarios
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TheFirstApprovalFromTheListWinsAndTheOthersAreCancelled(string profile)
    {
        var mailer = new Mailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await engine.RunAsync(AnyApproverFromListRecipe.Definition(),
            new ApprovalRound { OrderId = "order-13", Amount = 50, Approvers = ["ann", "bob", "cid"] });

        // A rejection does not end the round.
        var rejected = await engine.ReplyAsync(mailer.TokenOf("bob"), new ApprovalDecision { Approved = false, By = "bob" });
        EventAssert.Consumed(rejected);
        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);
        var approved = await engine.ReplyAsync(mailer.TokenOf("cid"), new ApprovalDecision { Approved = true, By = "cid" });

        EventAssert.Consumed(approved);
        Assert.Equal(WorkflowExecutionStatus.Success, approved.Status);
        var round = Assert.IsType<ApprovalRound>(approved.WorkflowData);
        Assert.Equal("approved", round.Outcome);
        Assert.Equal([ItemStatus.Cancelled, ItemStatus.Completed, ItemStatus.Completed],
            round.Decisions.Select(decision => decision.Status));
        Assert.Equal([null, false, true], round.Decisions.Select(decision => decision.Value?.Approved));
        EventAssert.Rejected(await engine.ReplyAsync(mailer.TokenOf("ann"), new ApprovalDecision { Approved = true }),
            EventRejection.WaitCancelled);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnEmptyListOrEveryoneRejectingTakesTheRejectionPath(string profile)
    {
        var mailer = new Mailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();

        var empty = await engine.RunAsync(AnyApproverFromListRecipe.Definition(),
            new ApprovalRound { OrderId = "order-18a", Approvers = [] });
        await engine.RunAsync(AnyApproverFromListRecipe.Definition(),
            new ApprovalRound { OrderId = "order-18b", Approvers = ["ann", "bob"] });
        await engine.ReplyAsync(mailer.TokenOf("ann"), new ApprovalDecision { Approved = false });
        var allRejected = await engine.ReplyAsync(mailer.TokenOf("bob"), new ApprovalDecision { Approved = false });

        Assert.Equal(("rejected", 0), (Assert.IsType<ApprovalRound>(empty.WorkflowData).Outcome,
            Assert.IsType<ApprovalRound>(empty.WorkflowData).Decisions.Count));
        var round = Assert.IsType<ApprovalRound>(allRejected.WorkflowData);
        Assert.Equal("rejected", round.Outcome);
        Assert.Equal([false, false], round.Decisions.Select(decision => decision.Value!.Approved));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EveryApproversDecisionIsKeptInApproverOrder(string profile)
    {
        var mailer = new Mailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await engine.RunAsync(EveryApproverFromListRecipe.Definition(),
            new ApprovalRound { OrderId = "order-14", Approvers = ["ann", "bob", "cid"] });

        foreach (var name in new[] { "cid", "ann" })
            Assert.Equal(WorkflowExecutionStatus.Suspended,
                (await engine.ReplyAsync(mailer.TokenOf(name), new ApprovalDecision { Approved = true, By = name })).Status);
        var finished = await engine.ReplyAsync(mailer.TokenOf("bob"), new ApprovalDecision { Approved = false, By = "bob" });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(["ann", "bob", "cid"],
            Assert.IsType<ApprovalRound>(finished.WorkflowData).Decisions.Select(decision => decision.Value!.By));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OneFailedRequestCancelsTheOthersAndStoresNoDecisions(string profile)
    {
        var mailer = new Mailer { Failing = "nobody" };
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();

        var result = await engine.RunAsync(EveryApproverFromListRecipe.Definition(),
            new ApprovalRound { OrderId = "order-19", Approvers = ["ann", "nobody", "cid"] });
        var finished = await engine.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience);

        var round = Assert.IsType<ApprovalRound>(finished.WorkflowData);
        Assert.Equal(("failed", 0), (round.Outcome, round.Decisions.Count));
        foreach (var name in mailer.Names)
            EventAssert.Rejected(await engine.ReplyAsync(mailer.TokenOf(name), new ApprovalDecision { Approved = true }),
                EventRejection.WaitCancelled);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task QueuedRequestsAreSentOnlyAsAnswersComeInEvenOnAnotherHost(string profile)
    {
        var mailer = new Mailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var first = cluster.AddHost();
        var second = cluster.AddHost();

        var started = await first.RunAsync(EveryApproverFromListRecipe.Definition(maxConcurrency: 2),
            new ApprovalRound { OrderId = "order-21", Approvers = ["ann", "bob", "cid", "dan", "eve"] });

        // Two requests are out; the next goes when one is answered, here on the other host.
        Assert.Equal(["ann", "bob"], mailer.Names);
        await second.ReplyAsync(mailer.TokenOf("bob"), new ApprovalDecision { Approved = true, By = "bob" });
        Assert.Equal(["ann", "bob", "cid"], mailer.Names);
        foreach (var name in new[] { "ann", "cid", "dan", "eve" })
            await second.ReplyAsync(mailer.TokenOf(name), new ApprovalDecision { Approved = true, By = name });

        var finished = await second.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience);
        Assert.Equal(["ann", "bob", "cid", "dan", "eve"],
            Assert.IsType<ApprovalRound>(finished.WorkflowData).Decisions.Select(decision => decision.Value!.By));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AChildPerCustomerRunsNoMoreThanTheCapAtOnce(string profile)
    {
        var mailer = new Mailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        await cluster.Registry.RegisterWorkflowAsync(ChargeCustomersRecipe.Child());
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(ChargeCustomersRecipe.Parent(maxConcurrency: 2),
            new Billing { Customers = ["ann", "bob", "cid"] });

        // Two children asked their customers; the third child has not started.
        Assert.Equal(["ann", "bob"], mailer.Names);
        // The answer goes to the child; the parent wakes when the child finishes and starts the next one.
        await engine.ReplyAsync(mailer.TokenOf("ann"), new ApprovalDecision { Approved = true });
        await mailer.WaitForAsync(3).WaitAsync(Patience);
        Assert.Equal(["ann", "bob", "cid"], mailer.Names);
        await engine.ReplyAsync(mailer.TokenOf("cid"), new ApprovalDecision { Approved = true });
        await engine.ReplyAsync(mailer.TokenOf("bob"), new ApprovalDecision { Approved = false });

        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal([true, false, true], Assert.IsType<Billing>(finished.WorkflowData).Paid.Select(item => item.Value));
    }

    private static async Task<ConformanceCluster> ClusterAsync(string profile, Mailer mailer) =>
        await ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddWaitRecipes();
            services.AddSingleton<IApprovalMailer>(mailer);
        });

    private sealed class Mailer : IApprovalMailer
    {
        private readonly List<ApprovalRequest> _sent = [];

        public string? Failing { get; init; }

        public IReadOnlyList<string> Names
        {
            get { lock (_sent) return _sent.Select(request => request.Approver).ToArray(); }
        }

        public string TokenOf(string approver)
        {
            lock (_sent)
                return _sent.Single(request => request.Approver == approver).ReplyToken;
        }

        public async Task WaitForAsync(int count)
        {
            while (Names.Count < count)
                await Task.Delay(10);
        }

        public Task SendAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            if (request.Approver == Failing)
                throw new InvalidOperationException($"No address for '{request.Approver}'");
            lock (_sent)
                _sent.Add(request);
            return Task.CompletedTask;
        }
    }
}
