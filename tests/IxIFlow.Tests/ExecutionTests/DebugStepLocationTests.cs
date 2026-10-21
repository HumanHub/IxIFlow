using System.Runtime.CompilerServices;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class DebugStepLocationTests
{
    [Fact]
    public void EveryStepKindHasACatalogAddressAuthorIdentityAndCallerLocation()
    {
        var definition = Workflow.Create<Data>("StepLocations")
            .Delay(TimeSpan.FromSeconds(1))
            .Step<Noop>().WithId("first")
            .Invoke<Data>("child", 1)
            .Sequence(s => s.Step<Noop>())
            .If(c => true, yes => yes.Step<Noop>(), no => no.Step<Noop>())
            .Parallel(p => p.Do(b => b.Step<Noop>()).Do(b => b.Step<Noop>()))
            .WhileDo(c => false, b => b.Step<Noop>())
            .DoWhile(b => b.Step<Noop>(), c => false)
            .Try(b => b.Step<Noop>()).Catch<Exception>(b => b.Step<Noop>()).Finally(b => b.Step<Noop>())
            .Saga(s => s.Step<Noop>(a => a.CompensateWith<Noop>())).OnError(b => b.Step<Noop>()).Step<Noop>()
            .WaitFor<Go>("go")
            .ForEach(c => c.WorkflowData.Items, (b, item) => b.Result(c => c.Item(item)))
            .Flowchart(f => f.Start("start").Node("start", edges => edges.Always("done")).Node("done"))
            .Build();
        var table = definition.StepTable;
        Assert.Same(table, definition.StepTable);
        Assert.Equal(Enum.GetValues<WorkflowStepType>().Order(), table.Select(e => e.Kind).Distinct().Order());
        Assert.All(table, entry =>
        {
            Assert.StartsWith("root/", entry.CatalogId);
            Assert.Equal(entry.CatalogId[..entry.CatalogId.LastIndexOf('/')], entry.ScopePath);
            Assert.False(string.IsNullOrWhiteSpace(entry.AuthorId));
            Assert.Contains(nameof(DebugStepLocationTests) + ".cs:", entry.SourceLocation);
            Assert.True(int.Parse(entry.SourceLocation![(entry.SourceLocation.LastIndexOf(':') + 1)..]) > 0);
        });
        Assert.Equal("first", table.Single(e => e.CatalogId == "root/1").AuthorId);
        var hash = new WorkflowScopeCatalog(definition).StructureHash;
        foreach (var step in new WorkflowScopeCatalog(definition).AllSteps.Select(e => e.Value)) step.SourceLocation = "elsewhere.cs:900";
        Assert.Equal(hash, new WorkflowScopeCatalog(definition).StructureHash);
    }

    [Fact]
    public void CreationMethodsAndInterfacesCaptureLocationsIncludingDelegatingOverloads()
    {
        var creatingNames = new[] { "Step", "Request", "Delay", "Invoke", "Sequence", "If", "Parallel", "WhileDo", "DoWhile", "ForEach", "Result", "Try", "Saga", "Suspend", "WaitFor", "OutcomeOn", "CompensateWith" };
        var types = typeof(Workflow).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith("IxIFlow.Builders") == true);
        foreach (var method in types.SelectMany(t => t.GetMethods()).Where(m => creatingNames.Contains(m.Name) && m.DeclaringType?.Name.Contains("ForEachSetup") != true))
        {
            Assert.Contains(method.GetParameters(), p => p.GetCustomAttributes(typeof(CallerFilePathAttribute), false).Length == 1);
            Assert.Contains(method.GetParameters(), p => p.GetCustomAttributes(typeof(CallerLineNumberAttribute), false).Length == 1);
        }
        var builder = Workflow.Create<Data>("ExplicitLocation").Step<Noop>(sourcePath: "author.cs", sourceLine: 40)
            .Request<Request>("request", sourcePath: "author.cs", sourceLine: 50)
            .WaitFor<Go>("request", sourcePath: "author.cs", sourceLine: 60)
            .WhileDo(c => false, b => b.Step<Noop>(), sourcePath: "author.cs", sourceLine: 70).Build();
        Assert.Equal(["author.cs:40", "author.cs:50", "author.cs:60", "author.cs:70"], builder.Steps.Select(s => s.SourceLocation));
    }

    [Fact]
    public async Task SnapshotPointersCarryBothIdsAndFramesCarryIterationAndItemIndex()
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("SnapshotIds").Step<Noop>()
            .DoWhile(loop => loop.ForEach(c => c.WorkflowData.Items, (body, item) => body.WaitFor<Go>("go").WithId("author-wait")), c => false).Build();
        var result = await client.RunAsync(definition, new Data());
        var snapshot = await client.GetSnapshotAsync(result.InstanceId);
        var pointer = Assert.Single(snapshot!.Pointers);
        Assert.Equal("author-wait", pointer.AuthorStepId);
        Assert.Equal("root/1/loop/0/foreach/0", pointer.StepId);
        Assert.Contains(snapshot.Frames, f => f.LoopIteration == 1);
        Assert.Contains(snapshot.Frames, f => f.ForEachItemIndex == 0);
    }
    public sealed class Data { public List<int> Items { get; set; } = [1]; }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Request : IAsyncActivity, IRequestActivity<Go>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
