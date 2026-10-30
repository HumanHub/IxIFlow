using System.Runtime.InteropServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeThreadAffinityTests
{
    [Fact]
    public async Task CapturedAwaitsStayOnOneThreadAndWindowsUsesStaBeforeFirstCallback()
    {
        var registry = new ScopeThreadRegistry(1);
        var scheduler = registry.Create();

        try
        {
            await scheduler.RunAsync(async () =>
            {
                var thread = Environment.CurrentManagedThreadId;

                if (OperatingSystem.IsWindows())
                    Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());

                await Task.Yield();
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                await Task.Delay(10);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);

                return true;
            });
        }
        finally
        {
            await scheduler.StopAsync();
        }

        Assert.Equal(0, registry.Count);
    }

    [WindowsFact]
    public async Task WindowsNativeCallbackIsPumpedWhileManagedCallbackAwaits()
    {
        var scheduler = new ScopeThreadRegistry(1).Create();

        try
        {
            await scheduler.RunAsync(async () =>
            {
                using var window = new MessageWindow();
                var thread = Environment.CurrentManagedThreadId;
                await Task.Run(() => window.Signal());
                Assert.Equal(thread, await window.Received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(thread, Environment.CurrentManagedThreadId);

                return true;
            });
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task HostLimitIsNonblockingAndSlotReturnsOnlyAfterPhysicalThreadExit()
    {
        var registry = new ScopeThreadRegistry(1);
        var scheduler = registry.Create();
        Assert.Throws<ScopeThreadLimitExceededException>(() => registry.Create());
        await scheduler.StopAsync();
        var replacement = registry.Create();
        await replacement.RunAsync(() => Task.FromResult(true));
        await replacement.StopAsync();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task CrossThreadSendIsRefusedWithoutBlocking()
    {
        var scheduler = new ScopeThreadRegistry(1).Create();

        try
        {
            Assert.Throws<InvalidOperationException>(() => scheduler.Send(_ =>
            {
            }, null));
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task NestedAffineAndOrdinaryScopesShareTheEnclosingThread()
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe);

        var definition = ScopeActivityTests.Start("NestedAffine").Scope<ScopeActivityTests.TestScope>(
                s => s.Threading(ScopeThreading.ThreadAffine),
                b => b.Scope<ScopeActivityTests.TestScope>(null, x => x.Step<ScopeActivityTests.Read>())
                    .Scope<ScopeActivityTests.TestScope>(s => s.Threading(ScopeThreading.ThreadAffine),
                        x => x.Step<ScopeActivityTests.Read>()))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Single(probe.CallbackThreads.Distinct());
    }

    [Fact]
    public async Task WrongThreadAndExpiredOperationCannotLookUpAnAffineValue()
    {
        var scheduler = new ScopeThreadRegistry(1).Create();
        var value = new ScopeBuildRuleTests.Value();
        var values = new ScopeValues([new() { Value = value, Scheduler = scheduler }]);

        try
        {
            Assert.Throws<InvalidOperationException>(() => values.Find<ScopeBuildRuleTests.Value>());
            ScopeOperation? operation = null;

            await values.RunAsync(async () =>
            {
                Assert.Same(value, values.Find<ScopeBuildRuleTests.Value>());
                await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => values.Find<ScopeBuildRuleTests.Value>()));
                operation = ScopeOperation.Current.Value;

                return true;
            });

            Assert.False(operation!.Active);

            await scheduler.RunAsync(() =>
            {
                Assert.Throws<InvalidOperationException>(() => values.Find<ScopeBuildRuleTests.Value>());

                return Task.FromResult(true);
            });
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    private sealed class MessageWindow : IDisposable
    {
        private const uint SignalMessage = 0x8001;
        private readonly Procedure _procedure;
        private readonly string _name = "IxIFlowScopeTest" + Guid.NewGuid().ToString("N");
        private readonly IntPtr _instance;
        private readonly IntPtr _window;
        public TaskCompletionSource<int> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MessageWindow()
        {
            _instance = GetModuleHandle(null);
            _procedure = Handle;

            var definition = new WindowClass
                { Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Instance = _instance, ClassName = _name };

            Assert.NotEqual((ushort)0, RegisterClass(ref definition));
            _window = CreateWindowEx(0, _name, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, _instance, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, _window);
        }

        public void Signal() => Assert.True(PostMessage(_window, SignalMessage, UIntPtr.Zero, IntPtr.Zero));

        private IntPtr Handle(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam)
        {
            if (message == SignalMessage)
            {
                Received.TrySetResult(Environment.CurrentManagedThreadId);

                return IntPtr.Zero;
            }

            return DefWindowProc(window, message, wparam, lparam);
        }

        public void Dispose()
        {
            DestroyWindow(_window);
            UnregisterClass(_name, _instance);
            GC.KeepAlive(_procedure);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr Procedure(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public IntPtr Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClass(ref WindowClass definition);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width,
            int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string name, IntPtr instance);
    }
}