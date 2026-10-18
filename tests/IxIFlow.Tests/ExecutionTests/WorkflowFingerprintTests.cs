using System.Linq.Expressions;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class WorkflowFingerprintTests
{
    public static TheoryData<string> Behaviors => new()
    {
        "mapping", "condition", "while", "do", "wait", "request", "first-request",
        "parallel", "foreach", "foreach-expression", "outcome", "saga-wait", "saga-request"
    };

    [Theory]
    [MemberData(nameof(Behaviors))]
    public void EngineFactoriesAndAdaptersDoNotChangeTheFingerprint(string behavior)
    {
        var original = Define(behavior, 1);
        var changed = new WorkflowDefinition
        {
            WorkflowDataType = original.WorkflowDataType,
            Steps = original.Steps.Select(step => step.Clone()).ToList()
        };
        foreach (var step in ParallelRules.Flatten(changed.Steps))
        {
            step.Runtime = step.Runtime with
            {
                Context = _ => throw new InvalidOperationException("different context factory"),
                OutputContext = _ => throw new InvalidOperationException("different output factory"),
                EventContext = _ => throw new InvalidOperationException("different event factory"),
                PreviousEventContext = _ => throw new InvalidOperationException("different previous event factory"),
                Prepare = (_, _) => throw new InvalidOperationException("different dispatch"),
                ChildDefinition = _ => throw new InvalidOperationException("different child factory")
            };
            if (step.CompiledCondition != null) step.CompiledCondition = _ => false;
            if (step.OutcomeSelector != null) step.OutcomeSelector = _ => null;
            if (step.ParallelCompletionCondition != null) step.ParallelCompletionCondition = _ => false;
            if (step.RequestSubject != null) step.RequestSubject = _ => "different adapter";
            if (step.ForEachItems != null) step.ForEachItems = _ => Array.Empty<int>();
            if (step.ForEachCompletionCondition != null) step.ForEachCompletionCondition = (_, _) => false;
            if (step.ResultSelector != null) step.ResultSelector = _ => null;
            if (step.ForEachInto != null) step.ForEachInto = (_, _) => { };
            foreach (var mapping in step.InputMappings.Concat(step.OutputMappings))
            {
                mapping.ContextFactory = _ => throw new InvalidOperationException("different mapping factory");
                mapping.SourceFunction = _ => throw new InvalidOperationException("different input adapter");
                mapping.TargetAssignmentFunction = (_, _) => throw new InvalidOperationException("different output adapter");
            }
        }
        Assert.Equal(new WorkflowScopeCatalog(original).Fingerprint, new WorkflowScopeCatalog(changed).Fingerprint);
    }

    [Theory]
    [MemberData(nameof(Behaviors))]
    public void AuthorBehaviorStillChangesTheFingerprint(string behavior) =>
        Assert.NotEqual(new WorkflowScopeCatalog(Define(behavior, 1)).Fingerprint,
            new WorkflowScopeCatalog(Define(behavior, 2)).Fingerprint);

    private static WorkflowDefinition Define(string behavior, int threshold)
    {
        var start = Workflow.Create<Data>("Fingerprint");
        if (behavior == "first-request")
            return start.Request<Question>("question", subject: ctx => ctx.WorkflowData.Total + ":" + threshold)
                .WaitFor<Answer>("question").Build();
        var builder = start.Step<Activity>();
        return behavior switch
        {
            "mapping" => builder.Step<Activity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Total + threshold)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Total)).Build(),
            "condition" => builder.If(ctx => ctx.WorkflowData.Total > threshold, body => body.Step<Activity>()).Build(),
            "while" => builder.WhileDo(ctx => ctx.WorkflowData.Total > threshold, body => body.Step<Activity>()).Build(),
            "do" => builder.DoWhile(body => body.Step<Activity>(), ctx => ctx.WorkflowData.Total > threshold).Build(),
            "wait" => builder.WaitFor<Answer>("answer", (_, ctx) => ctx.WorkflowData.Total > threshold).Build(),
            "request" => builder.Request<Question>("question", subject: ctx => ctx.WorkflowData.Total + ":" + threshold)
                .WaitFor<Answer>("question").Build(),
            "parallel" => builder.Parallel(parallel => parallel.Do(body => body.Step<Activity>())
                .Do(body => body.Step<Activity>()).WaitConditionally(data => data.Total > threshold)).Build(),
            "foreach" => builder.ForEach(ctx => ctx.WorkflowData.Values.Where(value => value > threshold),
                (body, item) => body.Step<Activity>(setup => setup.Input(activity => activity.Value).From(ctx => ctx.Item(item)))
                    .Result(ctx => ctx.PreviousStep.Value + threshold),
                setup => setup.Parallel(2).WaitConditionally<int>((_, results) => results.Count > threshold)
                    .Into(ctx => ctx.WorkflowData.Results).Combine<int, Sum<int>>(data => data.Total)).Build(),
            "foreach-expression" => builder.ForEach(
                (Expression<Func<WorkflowContext<Data, Activity>, IEnumerable<int>?>>)(ctx => ctx.WorkflowData.Values),
                (body, _) => body.Step<Activity>().Result(
                    (Expression<Func<WorkflowContext<Data, Activity>, int>>)(ctx => ctx.PreviousStep.Value + threshold)),
                setup => setup.Parallel(2).WaitConditionally<int>(
                    (Expression<Func<Data, IReadOnlyList<ItemResult<int>>, bool>>)((_, results) => results.Count > threshold))
                    .Into(ctx => ctx.WorkflowData.Results)).Build(),
            "outcome" => builder.Saga(saga => saga.Step<Activity>().OutcomeOn(
                ctx => ctx.PreviousStep.Value + threshold, outcomes => outcomes.Outcome(1, body => body.Step<Activity>())
                    .DefaultOutcome(body => body.Step<Activity>()))).Build(),
            "saga-wait" => builder.Saga(saga => saga.Step<Activity>()
                .WaitFor<Answer>("answer", (_, ctx) => ctx.WorkflowData.Total > threshold)).Build(),
            "saga-request" => builder.Saga(saga => saga.Request<Question>("question",
                subject: ctx => ctx.WorkflowData.Total + ":" + threshold).WaitFor<Answer>("question")).Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(behavior))
        };
    }

    public sealed class Data
    {
        public int Total { get; set; }
        public List<int> Values { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
    }

    public sealed class Activity : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Answer;
    public sealed class Question : IAsyncActivity, IRequestActivity<Answer>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
