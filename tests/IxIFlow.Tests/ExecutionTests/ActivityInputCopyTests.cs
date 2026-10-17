using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// An activity runs on copies of its inputs, read back from what its attempt saves (A-09), on its first attempt as
/// after a recovery. A change it makes to an input reaches the workflow data only through an output mapping, whether
/// or not the host crashed, and every attempt starts from the saved inputs.
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-09")]
public sealed class ActivityInputCopyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnInputChangedInPlaceReachesTheDataOnlyThroughAnOutputWithOrWithoutACrash(bool mapped, bool crash)
    {
        var store = new FaultInjectingRepository();
        var probe = new Probe();
        var definition = Appending($"InPlace{mapped}{crash}", mapped);
        var instanceId = Guid.NewGuid().ToString("N");
        using (var first = Services(store, probe))
        {
            // The host dies as it saves the appending activity's end, after the activity ran.
            if (crash)
                store.FailCommit = _ => probe.Appends > 0;
            var run = first.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data(),
                new WorkflowOptions { InstanceId = instanceId });
            if (crash)
                await Assert.ThrowsAnyAsync<Exception>(() => run);
            else
                await run;
        }
        store.FailCommit = _ => false;
        using var second = Services(store, probe);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);

        var result = crash
            ? await second.GetRequiredService<IWorkflowClient>().RecoverAsync(instanceId)
            : await second.GetRequiredService<IWorkflowClient>().AwaitAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ListData>(result.WorkflowData);
        var expected = mapped ? 3 : 2;
        Assert.Equal(expected, data.Items.Count);
        Assert.Equal(expected, data.Seen);
    }

    // An attempt that changed its input and then failed leaves nothing behind: the retry starts from the saved inputs.
    [Fact]
    public async Task ARetryStartsFromTheSavedInputs()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe);
        var definition = Workflow.Create<ListData>("RetryFromSavedInputs")
            .Step<CountsNames>(step => step.Input(activity => activity.Names).From(ctx => ctx.WorkflowData.Items))
            .Saga(saga => saga.Step<AppendThenFailOnce>(step => step
                .Input(activity => activity.Items).From(ctx => ctx.WorkflowData.Items)
                .Output(activity => activity.Items).To(ctx => ctx.WorkflowData.Items)
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["a", "b", "added"], Assert.IsType<ListData>(result.WorkflowData).Items);
        Assert.Equal(2, probe.Appends);
    }

    // A retry is another attempt of the same invocation: it starts from the inputs the failed attempt saved, even when
    // that attempt changed the data the inputs were mapped from.
    [Fact]
    public async Task ARetryKeepsTheSavedInputsWhenTheirSourceChanged()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe);
        var definition = Workflow.Create<ListData>("RetryKeepsSavedInputs")
            .Step<CountsNames>(step => step.Input(activity => activity.Names).From(ctx => ctx.WorkflowData.Items))
            .Saga(saga => saga.Step<ChangesTheDataThenFailsOnce>(step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Seen)
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Seen)
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ListData { Seen = 10 });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(10, Assert.IsType<ListData>(result.WorkflowData).Seen);
        Assert.Equal(2, probe.Appends);
    }

    // The recovery state is taken from the inputs the activity runs on, not from the data they were copied from.
    [Fact]
    public async Task TheRecoveryStateIsTakenFromTheInputsTheActivityRunsOn()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("RecoveryStateFromInputs")
            .Step<KeepsItsInput>(step => step
                .Input(activity => activity.Items).From(ctx => ctx.WorkflowData.Items)
                .Output(activity => activity.SameObject).To(ctx => ctx.WorkflowData.Flag))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.True(Assert.IsType<ListData>(result.WorkflowData).Flag);
    }

    // A value of a type that cannot be named, such as a LINQ iterator, is saved under the input's declared type.
    [Fact]
    public async Task ALazySequenceIsSavedUnderTheInputsDeclaredType()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("LazySequenceInput")
            .Step<CountsNames>(step => step
                .Input(activity => activity.Names).From(ctx => ctx.WorkflowData.Items.Where(item => item != "b"))
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Assert.IsType<ListData>(result.WorkflowData).Seen);
    }

    // Every property is saved, so a null the type would leave out reads back as null, not as its default.
    [Fact]
    public async Task ANullInsideAnInputReadsBackAsNull()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("NullInsideInput")
            .Step<ReadsNote>(step => step
                .Input(activity => activity.Sparse).From(ctx => ctx.WorkflowData.Sparse)
                .Output(activity => activity.NoteWasNull).To(ctx => ctx.WorkflowData.Flag))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition,
            new ListData { Sparse = new Sparse { Note = null } });

        Assert.True(Assert.IsType<ListData>(result.WorkflowData).Flag);
    }

    // An input that cannot be saved and read back is an invalid definition: the run fails before the activity runs,
    // instead of only after a crash.
    [Fact]
    public async Task AnInputThatCannotBeReadBackIsAnInvalidDefinition()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe);
        var definition = Workflow.Create<ListData>("UnreadableInput")
            .Step<TakesFixed>(step => step.Input(activity => activity.Fixed).From(_ => new Fixed(7)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Activity input 'TakesFixed.Fixed' cannot be saved and read back", result.ErrorMessage);
        Assert.Equal(typeof(WorkflowEngineException).FullName, result.ErrorType);
        Assert.Equal(0, probe.Appends);
    }

    private static WorkflowDefinition Appending(string name, bool mapped) =>
        Workflow.Create<ListData>(name)
            .Step<AppendsToItsInput>(step =>
            {
                step.Input(activity => activity.Items).From(ctx => ctx.WorkflowData.Items);
                if (mapped)
                    step.Output(activity => activity.Items).To(ctx => ctx.WorkflowData.Items);
            })
            .Step<CountsNames>(step => step
                .Input(activity => activity.Names).From(ctx => ctx.WorkflowData.Items)
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Seen))
            .Build();

    private static ListData Data() => new() { Items = ["a", "b"] };

    private static ServiceProvider Services(IWorkflowStateRepository store, Probe probe)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(probe);
        services.AddTransient<AppendsToItsInput>();
        services.AddTransient<AppendThenFailOnce>();
        services.AddTransient<ChangesTheDataThenFailsOnce>();
        services.AddTransient<TakesFixed>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class ListData
    {
        public List<string> Items { get; set; } = [];
        public int Seen { get; set; }
        public bool Flag { get; set; }
        public Sparse? Sparse { get; set; }
    }

    public sealed class Sparse
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; } = "default";
    }

    /// <summary>Its constructor's parameter names no property, so it cannot be read back from JSON.</summary>
    public sealed class Fixed(int value)
    {
        public int Number { get; } = value;
    }

    public sealed class Probe
    {
        private int _appends;
        public int Appends => Volatile.Read(ref _appends);
        public void Appended() => Interlocked.Increment(ref _appends);
    }

    /// <summary>Appends to its input; safe to run again, so a recovery runs it on the saved inputs.</summary>
    public sealed class AppendsToItsInput(Probe probe) : IRecoverableActivity<bool>
    {
        public List<string> Items { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Items.Add("added");
            probe.Appended();
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    public sealed class AppendThenFailOnce(Probe probe) : IAsyncActivity
    {
        public List<string> Items { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Items.Add("added");
            probe.Appended();
            return probe.Appends == 1
                ? throw new InvalidOperationException("Planned failure after changing the input")
                : Task.CompletedTask;
        }
    }

    /// <summary>Changes the live workflow data its input was mapped from, then fails on its first attempt.</summary>
    public sealed class ChangesTheDataThenFailsOnce(Probe probe) : IAsyncActivity
    {
        public int Amount { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Appended();
            if (probe.Appends > 1)
                return Task.CompletedTask;
            ((ListData)context.WorkflowData).Seen = 20;
            throw new InvalidOperationException("Planned failure after changing the data");
        }
    }

    public sealed class KeepsItsInput : IRecoverableActivity<List<string>>
    {
        public List<string> Items { get; set; } = [];
        public bool SameObject { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            SameObject = ReferenceEquals(context.RecoveryState, Items);
            return Task.CompletedTask;
        }

        public List<string> CaptureRecoveryState(IActivityContext context) => Items;

        public Task<ActivityRecoveryResult<List<string>>> RecoverAsync(List<string> state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<List<string>>>(new ActivityRecoveryResult<List<string>>.Execute());
    }

    public sealed class CountsNames : IAsyncActivity
    {
        public IEnumerable<string> Names { get; set; } = [];
        public int Count { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Count = Names.Count();
            return Task.CompletedTask;
        }
    }

    public sealed class ReadsNote : IAsyncActivity
    {
        public Sparse? Sparse { get; set; }
        public bool NoteWasNull { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            NoteWasNull = Sparse is { Note: null };
            return Task.CompletedTask;
        }
    }

    public sealed class TakesFixed(Probe probe) : IAsyncActivity
    {
        public Fixed? Fixed { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Appended();
            return Task.CompletedTask;
        }
    }
}
