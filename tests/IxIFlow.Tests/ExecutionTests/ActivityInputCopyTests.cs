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

    // A cancellation abandons the retry, and the inputs kept for it go with it.
    [Fact]
    public async Task ACancelledRetryLeavesNoKeptInputsBehind()
    {
        var probe = new Probe();
        var store = new InMemoryWorkflowStateRepository();
        using var services = Services(store, probe);
        var definition = Workflow.Create<ListData>("CancelledRetry")
            .Step<CountsNames>(step => step.Input(activity => activity.Names).From(ctx => ctx.WorkflowData.Items))
            .Saga(saga => saga.Step<ChangesTheDataThenFailsOnce>(step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Seen)
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(
                    new RetryPolicy(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), 2, 1)))))
            .Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var instanceId = Guid.NewGuid().ToString("N");
        // The run parks until its retry is due, keeping the failed attempt's inputs.
        var running = client.RunAsync(definition, new ListData { Seen = 10 }, new WorkflowOptions { InstanceId = instanceId });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await store.GetWorkflowInstanceAsync(instanceId) is not { RunnableAtUtc: not null } parked ||
               !parked.ExecutionStateJson.Contains("RetryInputs"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The run did not park its retry");
            await Task.Delay(20);
        }

        await client.CancelAsync(instanceId, new CancellationReason { ReasonCode = "stop" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.DoesNotContain("RetryInputs", (await store.GetWorkflowInstanceAsync(instanceId))!.ExecutionStateJson);
        Assert.Equal(1, probe.Appends);
    }

    // Number handling on an input property needs no declared type: a value behind an interface keeps its own.
    [Fact]
    public async Task NumberHandlingOnAnInterfaceInputKeepsTheValuesType()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("NumberHandledInput")
            .Step<ReadsNumberedLine>(step => step
                .Input(activity => activity.Line).From(_ => new PublicLine { Quantity = 5 })
                .Output(activity => activity.Quantity).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(5, Assert.IsType<ListData>(result.WorkflowData).Seen);
    }

    // A converter on an input typed as object applies to whatever value it holds.
    [Fact]
    public async Task AConverterOnAnObjectInputAppliesToItsValue()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("ConvertedObjectInput")
            .Step<TakesBoxed>(step => step
                .Input(activity => activity.Boxed).From(_ => new Fixed(6))
                .Output(activity => activity.Number).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(6, Assert.IsType<ListData>(result.WorkflowData).Seen);
    }

    // An activity given as an input is read back through the properties its saved form holds; an ignored property's
    // converter, which could not be made, is never needed.
    [Fact]
    public async Task AnActivityInputWithAnIgnoredConvertedPropertyIsReadBack()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("IgnoredConverterActivityInput")
            .Step<TakesIgnoring>(step => step
                .Input(activity => activity.Other).From(_ => new IgnoresAConvertedProperty { Count = 3 })
                .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, Assert.IsType<ListData>(result.WorkflowData).Seen);
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

    // A non-public type is read back by name, so a value of one behind an interface input keeps its type.
    [Fact]
    public async Task AnInputOfANonPublicTypeKeepsItsType()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("NonPublicInput")
            .Step<ReadsLine>(step => step
                .Input(activity => activity.Line).From(_ => new HiddenLine { Quantity = 4 })
                .Output(activity => activity.Quantity).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(4, Assert.IsType<ListData>(result.WorkflowData).Seen);
    }

    // A converter on the input property applies to a value of a derived type: the value is saved under the declared
    // type the converter is written for.
    [Fact]
    public async Task AConverterOnTheInputPropertyAppliesToADerivedValue()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe());
        var definition = Workflow.Create<ListData>("ConvertedInput")
            .Step<TakesMoney>(step => step
                .Input(activity => activity.Amount).From(_ => new TaggedMoney(250))
                .Output(activity => activity.Cents).To(ctx => ctx.WorkflowData.Seen))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(250, Assert.IsType<ListData>(result.WorkflowData).Seen);
    }

    // An exception input that cannot be restored is an invalid definition that names the input.
    [Fact]
    public async Task AnExceptionInputThatCannotBeRestoredIsAnInvalidDefinition()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe);
        var definition = Workflow.Create<ListData>("UnrestorableExceptionInput")
            .Step<TakesError>(step => step.Input(activity => activity.Error).From(_ => new HiddenStateException("kept")))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Activity input 'TakesError.Error' cannot be saved and read back", result.ErrorMessage);
        Assert.Equal(0, probe.Appends);
    }

    // An activity given as an input whose saved form cannot be read back fails the run as an invalid definition; it
    // is not an interruption that recovery would meet again.
    [Fact]
    public async Task AnActivityInputThatCannotBeReadBackIsAnInvalidDefinition()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe);
        var definition = Workflow.Create<ListData>("UnreadableActivityInput")
            .Step<TakesActivity>(step => step.Input(activity => activity.Other).From(_ => new Computes()))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, Data())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Activity input 'TakesActivity.Other' cannot be saved and read back", result.ErrorMessage);
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
        services.AddTransient<TakesError>();
        services.AddTransient<TakesBoxed>();
        services.AddTransient<TakesIgnoring>();
        services.AddTransient<IgnoresAConvertedProperty>();
        services.AddTransient<TakesActivity>();
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
            // The state is the list the activity runs on, and that list is a copy, not the data's.
            SameObject = ReferenceEquals(context.RecoveryState, Items) &&
                         !ReferenceEquals(Items, ((ListData)context.WorkflowData).Items);
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

    public interface ILine
    {
        int Quantity { get; }
    }

    internal sealed class HiddenLine : ILine
    {
        public int Quantity { get; set; }
    }

    public sealed class PublicLine : ILine
    {
        public int Quantity { get; set; }
    }

    public sealed class ReadsNumberedLine : IAsyncActivity
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public ILine? Line { get; set; }
        public int Quantity { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Quantity = Line!.Quantity;
            return Task.CompletedTask;
        }
    }

    public sealed class ReadsLine : IAsyncActivity
    {
        public ILine? Line { get; set; }
        public int Quantity { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Quantity = Line!.Quantity;
            return Task.CompletedTask;
        }
    }

    /// <summary>Read back only through <see cref="MoneyConverter"/>: its constructor's parameter names no property.</summary>
    public class Money(int cents)
    {
        public int Value { get; } = cents;
    }

    public sealed class TaggedMoney(int cents) : Money(cents)
    {
        public string Tag { get; set; } = "tagged";
    }

    public sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => new(reader.GetInt32());

        public override void Write(System.Text.Json.Utf8JsonWriter writer, Money value,
            System.Text.Json.JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }

    public sealed class TakesMoney : IAsyncActivity
    {
        [JsonConverter(typeof(MoneyConverter))]
        public Money? Amount { get; set; }
        public int Cents { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Cents = Amount!.Value;
            return Task.CompletedTask;
        }
    }

    /// <summary>Its constructor's parameter names no property, so it cannot be restored.</summary>
    public sealed class HiddenStateException(string secret) : Exception("hidden state")
    {
        public string Code => secret;
    }

    public sealed class TakesError(Probe probe) : IAsyncActivity
    {
        public Exception? Error { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Appended();
            return Task.CompletedTask;
        }
    }

    /// <summary>Reads any boxed <see cref="Fixed"/> back from its number.</summary>
    public sealed class BoxConverter : JsonConverter<object>
    {
        public override object Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => new Fixed(reader.GetInt32());

        public override void Write(System.Text.Json.Utf8JsonWriter writer, object value,
            System.Text.Json.JsonSerializerOptions options) => writer.WriteNumberValue(((Fixed)value).Number);
    }

    public sealed class TakesBoxed : IAsyncActivity
    {
        [JsonConverter(typeof(BoxConverter))]
        public object? Boxed { get; set; }
        public int Number { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Number = ((Fixed)Boxed!).Number;
            return Task.CompletedTask;
        }
    }

    /// <summary>A converter that cannot be made: it has no parameterless constructor.</summary>
    public sealed class UnmakeableConverter(int unused) : JsonConverter<string>
    {
        public int Unused { get; } = unused;

        public override string Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => reader.GetString()!;

        public override void Write(System.Text.Json.Utf8JsonWriter writer, string value,
            System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    public sealed class IgnoresAConvertedProperty : IAsyncActivity
    {
        public int Count { get; set; }

        [JsonIgnore]
        [JsonConverter(typeof(UnmakeableConverter))]
        public string Note { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class TakesIgnoring : IAsyncActivity
    {
        public IgnoresAConvertedProperty? Other { get; set; }
        public int Count { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Count = Other!.Count;
            return Task.CompletedTask;
        }
    }

    /// <summary>An activity with a computed property, which its saved form holds but nothing can set.</summary>
    public sealed class Computes : IAsyncActivity
    {
        public int Twice => 2;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class TakesActivity(Probe probe) : IAsyncActivity
    {
        public Computes? Other { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Appended();
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
