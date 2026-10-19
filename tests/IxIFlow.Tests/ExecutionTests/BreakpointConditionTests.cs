using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-04")]
public sealed class BreakpointConditionTests
{
    [Theory]
    [InlineData(1, WorkflowExecutionStatus.Success)]
    [InlineData(2, WorkflowExecutionStatus.Paused)]
    public async Task DataConditionPausesOnlyWhenTrue(int value, WorkflowExecutionStatus expected)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = Definition("DataCondition");
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data { Value = value });
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", Condition: "context.WorkflowData.Value == 2")], "setter");
        Assert.Equal(expected, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        Assert.Equal(expected == WorkflowExecutionStatus.Paused ? 0 : 1, probe.Count);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
        if (expected == WorkflowExecutionStatus.Paused)
        {
            Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
            Assert.Equal(1, probe.Count);
        }
    }

    [Fact]
    public async Task PreviousStepIsTypedAtTheTarget()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "PreviousCondition").WaitFor<Go>("go")
            .Step<Previous>().Step<Count>().WithId("target").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", Condition: "context.PreviousStep.Value == 7")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        Assert.Equal(0, probe.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachItemAndIndexAreTyped(bool parallel)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "ItemCondition").WaitFor<Go>("go")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>().WithId("target"),
                setup => { if (parallel) setup.Parallel(2); }).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId,
            [new("bp", "target", Condition: "context.Item == 2 && context.Index == 1")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
        Assert.Equal(3, probe.Count);
        Assert.Equal(3, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Theory]
    [InlineData("context.WorkflowData.Helper()", "Helper")]
    [InlineData("new IxIFlow.Tests.ExecutionTests.BreakpointConditionTests.Data().Value == 0", "new")]
    [InlineData("System.IO.File.Exists(\"any\")", "File")]
    [InlineData("context.WorkflowData.GetType().Name == \"Data\"", "GetType")]
    [InlineData("DateTime.Now.Year > 2000", "DateTime.Now")]
    [InlineData("context.WorkflowData.Value.ToString() == \"2\"", "ToString")]
    [InlineData("((dynamic)context.WorkflowData).Value == 2", "dynamic")]
    [InlineData("context.WorkflowData.Value++ > 0", "++")]
    [InlineData("(context.WorkflowData.Value = 3) == 3", "=")]
    [InlineData("context.WorkflowData.Name.StartsWith(\"a\")", "StartsWith")]
    [InlineData("context.WorkflowData.Name.ToUpper() == \"A\"", "ToUpper")]
    [InlineData("context.WorkflowData.Name.StartsWith(\"a\", StringComparison.CurrentCulture)", "CurrentCulture")]
    [InlineData("context.WorkflowData.Items.Any(x => context.WorkflowData.Helper())", "Helper")]
    [InlineData("context.WorkflowData.Items.Any(x => { context.WorkflowData.Value = x; return true; })", "return")]
    [InlineData("context.WorkflowData.Names.OrderBy(x => x).Any()", "OrderBy")]
    [InlineData("context.WorkflowData.Custom.Any(x => x > 0)", "Any")]
    [InlineData("context.WorkflowData.Items.SelectMany(x => context.WorkflowData.Custom).Any()", "SelectMany")]
    [InlineData("$\"{context.WorkflowData.Value}\" == \"2\"", "context")]
    public void RefusedCategoriesHavePositionedDiagnostics(string expression, string category)
    {
        var text = "true &&\n (" + expression + ")";
        var result = new BreakpointConditionCompiler().Compile(new("hash", text, typeof(Data)));
        Assert.Null(result.Evaluate);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, d => d.Line == 2 && d.Column > 1 && d.Length > 0);
        Assert.All(result.Diagnostics, d => Assert.InRange(d.Start + d.Length, 0, text.Length));
        var position = text.IndexOf(category, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Start <= position && d.Start + d.Length >= position + category.Length);
    }

    [Theory]
    [InlineData("context.WorkflowData.Value > 0 && Math.Abs(-2) == 2")]
    [InlineData("context.WorkflowData.Name.StartsWith(\"a\", StringComparison.OrdinalIgnoreCase)")]
    [InlineData("context.WorkflowData.Name.ToUpperInvariant() == \"ABC\"")]
    [InlineData("context.WorkflowData.Items.Count == 3 && context.WorkflowData.Items[1] == 2")]
    [InlineData("context.WorkflowData.Optional.HasValue && context.WorkflowData.Optional.Value == 2")]
    [InlineData("context.WorkflowData.Items.Where(x => x > 1).Select(x => x * 2).Sum() == 10")]
    [InlineData("context.WorkflowData.Items.All(x => x > 0) && context.WorkflowData.Items.Any(x => x == 2)")]
    [InlineData("context.WorkflowData.Field == 3")]
    [InlineData("context.WorkflowData.Array.Length == 3 && context.WorkflowData.Array[1] == 2")]
    [InlineData("context.WorkflowData.Items.SelectMany(x => context.WorkflowData.Items).Count() == 9")]
    [InlineData("context.UtcNow.Year == 2026")]
    public void ExplicitlyPureMembersAreAccepted(string text)
    {
        var result = new BreakpointConditionCompiler().Compile(new("hash", text, typeof(Data)));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Evaluate);
        Assert.True(result.Evaluate(new Data { Value = 2 }, null, null, null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task RefusedConditionLeavesTheInstalledSetIntact()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition("RefusedSet"), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("old", "target")], "setter");
        var error = await Assert.ThrowsAsync<BreakpointConditionException>(() => ops.SetBreakpointsAsync(idle.InstanceId,
            [new("new", "target", Condition: "context.WorkflowData.Helper()")], "setter"));
        Assert.NotEmpty(error.Diagnostics);
        Assert.Equal("old", Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredConditionOnAnIncapableHostFailsClosed(bool invalid)
    {
        var store = new Store(); var probe = new Probe();
        await using var owner = Services(store, probe, compiler: invalid);
        var client = owner.GetRequiredService<IWorkflowClient>();
        var definition = Definition("IncapableHost");
        var idle = await client.RunAsync(definition, new Data());
        var breakpoint = Assert.Single(BreakpointResolver.Resolve(definition,
            [new("bp", "target", Condition: invalid ? "DateTime.Now.Year > 0" : "false")]));
        Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(idle.InstanceId,
            new("set", DebugCommandKind.SetBreakpoints, 0, "setter", [breakpoint]))).Status);
        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await owner.GetRequiredService<IWorkflowClient>().ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var stop = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).Stop!;
        Assert.Equal(invalid ? "ConditionCompilationFailed" : "ConditionCompilerUnavailable", stop.Reason);
        if (invalid) Assert.NotEmpty(stop.Diagnostics);
        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public async Task HostWithoutCompilerRefusesToInstallACondition()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe, compiler: false);
        var idle = await services.GetRequiredService<IWorkflowClient>().RunAsync(Definition("NoCompiler"), new Data());
        var error = await Assert.ThrowsAsync<BreakpointConditionException>(() => services.GetRequiredService<IWorkflowOperations>()
            .SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", Condition: "true")], "setter"));
        Assert.Equal("BPC000", Assert.Single(error.Diagnostics).Code);
    }

    [Fact]
    public async Task ThrowingConditionPausesWithItsReason()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition("ThrowingCondition"), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId,
            [new("bp", "target", Condition: "context.WorkflowData.Value / context.WorkflowData.Zero > 0")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var stop = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).Stop!;
        Assert.Equal("ConditionEvaluationFailed", stop.Reason);
        Assert.Contains("DivideByZeroException", Assert.Single(stop.Diagnostics).Message);
        var trace = await store.GetTraceAsync(idle.InstanceId);
        Assert.Equal(stop.Reason, Assert.IsType<BreakpointHitTrace>(
            Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.BreakpointHit).Entry.Payload).Reason);
        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public async Task AConditionFailureTakesPrecedenceOverAnUnconditionalHit()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition("MultipleConditions"), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("ordinary", "target"),
            new("failure", "target", Condition: "context.WorkflowData.Value / context.WorkflowData.Zero > 0")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var stop = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).Stop!;
        Assert.Equal("failure", stop.BreakpointId);
        Assert.Equal("ConditionEvaluationFailed", stop.Reason);
        Assert.All(await ops.GetBreakpointsAsync(idle.InstanceId), b => Assert.Equal(1, b.HitCount));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryReusesPersistedTimeAndCountForTheSameHit(bool matches)
    {
        var store = new Store(); var probe = new Probe();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var first = Services(store, probe, time: time); await using var second = Services(store, probe, time: time);
        var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition("ClockRecovery"), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", Condition: matches ?
            "context.UtcNow.Day == 1" : "context.UtcNow.Day != 1")], "setter");
        store.FailAfterPreparation = true;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(idle.InstanceId, "go", new Go()));
        var saved = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!);
        Assert.Equal(time.GetUtcNow().UtcDateTime, saved.Evaluation!.HitAtUtc);
        Assert.Equal(1, Assert.Single(saved.Breakpoints).HitCount);
        time.Advance(TimeSpan.FromDays(1)); store.FailAfterPreparation = false;
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId);
        Assert.Equal(matches ? WorkflowExecutionStatus.Paused : WorkflowExecutionStatus.Success, result.Status);
        var recovered = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!);
        Assert.Null(recovered.Evaluation);
        Assert.Equal(1, Assert.Single(recovered.Breakpoints).HitCount);
        if (matches) Assert.Equal(saved.Evaluation.HitAtUtc, recovered.Stop!.HitAtUtc);
    }

    [Fact]
    public async Task HitConditionRunsBeforeConditionEvaluation()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition("HitBeforeCondition"), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", HitCondition: "== 2",
            Condition: "context.WorkflowData.Value / context.WorkflowData.Zero > 0")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        Assert.Equal(0, store.Preparations);
    }

    [Fact]
    public async Task LoopHitsAreIdenticalWithACrashAndRecovery()
    {
        Assert.Equal(await Run(false), await Run(true));

        static async Task<(long Count, int Value)[]> Run(bool crash)
        {
            var store = new Store(); var probe = new Probe();
            await using var first = Services(store, probe); await using var recovery = Services(store, probe);
            var definition = new WorkflowBuilder<Data, object>([], "LoopCondition").WaitFor<Go>("go")
                .WhileDo(c => c.WorkflowData.Value < 4, body => body.Step<Increment>(s => s
                    .Input(a => a.Value).From(c => c.WorkflowData.Value).Output(a => a.Value).To(c => c.WorkflowData.Value))
                    .WithId("target")).Build();
            var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
            var idle = await client.RunAsync(definition, new Data());
            await ops.SetBreakpointsAsync(idle.InstanceId,
                [new("bp", "target", Condition: "context.WorkflowData.Value % 2 == 1")], "setter");
            store.FailAfterPreparation = crash;
            WorkflowExecutionResult result;
            if (crash)
            {
                await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(idle.InstanceId, "go", new Go()));
                store.FailAfterPreparation = false;
                result = await recovery.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId);
            }
            else result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
            var hits = new List<(long, int)>();
            while (result.Status == WorkflowExecutionStatus.Paused && hits.Count < 5)
            {
                var stop = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).Stop!;
                hits.Add((stop.HitCount, Assert.IsType<Data>(result.WorkflowData).Value));
                result = await ops.ContinueAsync(idle.InstanceId, "continue");
            }
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal([(2L, 1), (4L, 3)], hits);
            Assert.Equal(4, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
            return hits.ToArray();
        }
    }

    [Fact]
    public async Task CacheReusesConcurrentRequestsAndSeparatesStructureAndContextTypes()
    {
        var compiler = new BreakpointConditionCompiler();
        var request = new BreakpointConditionRequest("hash", "context.WorkflowData.Value > 0", typeof(Data));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => compiler.Compile(request))));
        Assert.NotNull(results[0].Evaluate);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.NotSame(results[0], compiler.Compile(request with { StructureHash = "other" }));
        Assert.NotSame(results[0], compiler.Compile(request with { PreviousType = typeof(Previous) }));
        Assert.NotSame(results[0], compiler.Compile(request with { Text = "false" }));
        var refused = request with { Text = "DateTime.Now.Year > 0" };
        Assert.Same(compiler.Compile(refused), compiler.Compile(refused));
        Assert.DoesNotContain(typeof(WorkflowHost).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.CodeAnalysis"));
    }

    private static WorkflowDefinition Definition(string name) => new WorkflowBuilder<Data, object>([], name)
        .WaitFor<Go>("go").Step<Count>().WithId("target").Build();

    private static ServiceProvider Services(Store store, Probe probe, bool compiler = true, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        if (compiler) services.AddBreakpointConditions();
        services.AddSingleton<IWorkflowStateRepository>(store); services.AddSingleton(probe);
        services.AddSingleton<IWorkflowVersionRegistry>(probe.Registry);
        if (time != null) services.AddSingleton(time);
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public int Value { get; set; }
        public int Zero { get; set; }
        public int Field = 3;
        public string Name { get; set; } = "abc";
        public int? Optional { get; set; } = 2;
        public List<int> Items { get; set; } = [1, 2, 3];
        public int[] Array { get; set; } = [1, 2, 3];
        public List<string> Names { get; set; } = ["a", "b"];
        public IEnumerable<int> Custom { get; set; } = [1, 2, 3];
        public bool Helper() => true;
    }
    public sealed class Go;
    public sealed class Probe { public WorkflowVersionRegistry Registry { get; } = new(); public int Count; }
    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Count); return Task.CompletedTask; }
    }
    public sealed class Previous : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value = 7; return Task.CompletedTask; }
    }
    public sealed class Increment : IAsyncActivity
    {
        [ActivityInputOutput] public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    private sealed class Store : InMemoryWorkflowStateRepository
    {
        public bool FailAfterPreparation;
        public int Preparations;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (FailAfterPreparation && Preparations > 0) throw new IOException("Crash after condition preparation");
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (WorkflowDebugRules.Checkpoint(commit.ReadTrunk()).Evaluation != null) Preparations++;
            return result;
        }
    }
}
