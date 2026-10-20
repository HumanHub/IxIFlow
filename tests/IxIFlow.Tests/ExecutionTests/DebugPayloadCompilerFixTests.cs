using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Debugging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class DebugPayloadCompilerFixTests
{
    [Fact]
    public void N_ConditionCacheIsBoundedAndEvictionRequestsUnloadWhileExistingDelegatesWork()
    {
        var compiler = new BreakpointConditionCompiler();
        var compiled = compiler.Compile(new("structure", "context.WorkflowData.Value > 0", typeof(Data)));
        Assert.NotNull(compiled.Evaluate);
        var load = AssemblyLoadContext.GetLoadContext(compiled.Evaluate.Method.DeclaringType!.Assembly)!;
        var unloading = false;
        load.Unloading += _ => unloading = true;
        for (var index = 0; index < 128; index++)
            compiler.Compile(new("structure", "(" + index, typeof(Data)));
        var cache = compiler.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compiler)!;
        Assert.True((int)cache.GetType().GetProperty("Count")!.GetValue(cache)! <= 128);
        Assert.True(unloading);
        Assert.True(compiled.Evaluate(new Data { Value = 1 }, null, null, null, DateTimeOffset.UtcNow));
        ((object)compiler as IDisposable)?.Dispose();
    }

    [Fact]
    public void N_DisposalRequestsUnloadForRemainingConditions()
    {
        var compiler = new BreakpointConditionCompiler();
        var compiled = compiler.Compile(new("structure", "true", typeof(Data)));
        var unloading = false;
        AssemblyLoadContext.GetLoadContext(compiled.Evaluate!.Method.DeclaringType!.Assembly)!.Unloading += _ => unloading = true;
        ((object)compiler as IDisposable)?.Dispose();
        Assert.True(unloading);
    }

    public sealed class Data { public int Value { get; set; } }
}
