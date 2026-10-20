using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class CoordinatorRound2RecoveryTests
{
    [Fact]
    public async Task ThrowingRunnableReadCompletesExactlyOnePassAndTheNextPassCanRun()
    {
        var store = DispatchProxy.Create<IWorkflowStateRepository, ThrowingStore>();
        var proxy = (ThrowingStore)(object)store;
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        await using var host = services.BuildServiceProvider();
        var completed = 0;
        host.GetRequiredService<CoordinationState>().ClaimPassCompleted += () => completed++;
        var scanner = host.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
        await Assert.ThrowsAsync<IOException>(() => scanner.RecoverOnceAsync(CancellationToken.None));
        Assert.Equal(1, completed);
        Assert.Equal(HostSettings.DefaultSlots, host.GetRequiredService<WorkflowHost>().FreeSlots);
        proxy.Throw = false;
        await scanner.RecoverOnceAsync(CancellationToken.None);
        Assert.Equal(2, completed);
    }

    public class ThrowingStore : DispatchProxy
    {
        internal bool Throw = true;
        private readonly IWorkflowStateRepository _inner = new InMemoryWorkflowStateRepository();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.GetRunnableInstancesAsync) && Throw)
                return Task.FromException<IReadOnlyList<RunnableInstance>>(new IOException("runnable read failed"));
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }
}
