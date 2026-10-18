using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// A sequential <c>ForEach</c> whose body asks and waits, in every profile (J-02): each item's request and
/// answer are its own, the item is read again from the saved list on another host, and the results are
/// stored in item order (D-34, spec E-11, E-13).
/// </summary>
[Trait("Spec", "E-11")]
[Trait("Spec", "E-13")]
public sealed class ForEachScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task EachItemAsksAndWaitsForItsOwnAnswerOnAnyHost(string profile)
    {
        var asked = new Asked();
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services => services
            .AddSingleton(asked)
            .AddTransient<AskAboutLine>());
        var first = cluster.AddHost();
        var second = cluster.AddHost();
        var definition = Workflow.Create<Lines>("AskPerLine")
            .Step<StartLines>()
            .ForEach(ctx => ctx.WorkflowData.Names,
                (body, name) => body
                    .Request<AskAboutLine>("line-answer", step => step
                            .Input(activity => activity.Name).From(ctx => ctx.Item(name)),
                        subject: ctx => ctx.Item(name))
                    .WaitFor<LineAnswer>("line-answer")
                    .Result(ctx => $"{ctx.Index(name)}:{ctx.Item(name)}:{ctx.PreviousStep.Answer}"),
                setup => setup.As("name").Into(ctx => ctx.WorkflowData.Answers))
            .Build();

        var started = await first.RunAsync(definition, new Lines { Names = ["pen", "ink"] });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var pen = Assert.Single(asked.Requests);
        Assert.Equal("pen", pen.Name);

        EventAssert.Consumed(await second.ReplyAsync(pen.Token, new LineAnswer { Answer = "yes" }));
        var ink = asked.Requests[1];
        Assert.Equal("ink", ink.Name);
        EventAssert.Rejected(await second.ReplyAsync(pen.Token, new LineAnswer { Answer = "again" }),
            EventRejection.WaitAnswered);
        var finished = await first.ReplyAsync(ink.Token, new LineAnswer { Answer = "no" });

        EventAssert.Consumed(finished);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(
            [
                new ItemResult<string>(0, ItemStatus.Completed, "0:pen:yes"),
                new ItemResult<string>(1, ItemStatus.Completed, "1:ink:no")
            ],
            Assert.IsType<Lines>(finished.WorkflowData).Answers);
    }

    public sealed class Lines
    {
        public List<string> Names { get; set; } = [];
        public List<ItemResult<string>> Answers { get; set; } = [];
    }

    public sealed class LineAnswer
    {
        public string Answer { get; set; } = "";
    }

    public sealed class Asked
    {
        private readonly List<(string Token, string Name)> _requests = [];

        public IReadOnlyList<(string Token, string Name)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public void Add(string token, string name)
        {
            lock (_requests)
                _requests.Add((token, name));
        }
    }

    public sealed class StartLines : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class AskAboutLine(Asked asked) : IRequestActivity<LineAnswer>
    {
        public string Name { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            asked.Add(context.Reply!.Token, Name);
            return Task.CompletedTask;
        }
    }
}
