using IxIFlow.Authoring;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class TimerIntervalRegistrationTests
{
    private static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    [Theory]
    [InlineData(nameof(IxIFlowOptions.PollInterval))]
    [InlineData(nameof(IxIFlowOptions.UnschedulableScanInterval))]
    [InlineData(nameof(IxIFlowOptions.RetentionScanInterval))]
    [InlineData(nameof(IxIFlowOptions.DrainTimeout))]
    public void EngineRegistrationRejectsIntervalsAboveTheTimerLimit(string name)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(options =>
            typeof(IxIFlowOptions).GetProperty(name)!.SetValue(options, Maximum + TimeSpan.FromTicks(1))));

        Assert.Equal(name, error.ParamName);
        Assert.Contains("4294967294", error.Message);
    }

    [Theory]
    [InlineData(nameof(IxIFlowOptions.PollInterval))]
    [InlineData(nameof(IxIFlowOptions.UnschedulableScanInterval))]
    [InlineData(nameof(IxIFlowOptions.RetentionScanInterval))]
    public void EngineRegistrationAcceptsBothTimerBoundaries(string name)
    {
        foreach (var value in new[] { TimeSpan.FromMilliseconds(1), Maximum })
            new ServiceCollection().AddIxIFlow(options => typeof(IxIFlowOptions).GetProperty(name)!.SetValue(options, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(4294967295)]
    public void PackageIndexingRejectsInvalidPeriodsBeforeStarting(double milliseconds)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PackageIndexingService(null!, null!)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) });

        Assert.Equal("Interval", error.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishingRegistrationValidatesTimers(bool heartbeat)
    {
        var options = heartbeat
            ? new WorkflowPublisherOptions { PublicationHeartbeatInterval = TimeSpan.MaxValue }
            : new WorkflowPublisherOptions { BuildTimeout = TimeSpan.MaxValue };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow().AddPublishing(options));
        Assert.Equal(heartbeat ? "PublicationHeartbeatInterval" : "BuildTimeout", error.ParamName);
    }

    [Fact]
    public void CoordinatorRegistrationValidatesDiscoveryBeforeStarting()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow()
            .UseCoordinators(options =>
            {
                options.AllowInsecure = true;
                options.DiscoveryInterval = TimeSpan.MaxValue;
            }));

        Assert.Equal("DiscoveryInterval", error.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BundleRegistrationValidatesTimeouts(bool fetch)
    {
        var options = fetch
            ? new WorkflowBundleOptions(Path.GetTempPath()) { FetchTimeout = TimeSpan.MaxValue }
            : new WorkflowBundleOptions(Path.GetTempPath()) { CacheLockTimeout = TimeSpan.MaxValue };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow().UseWorkflowBundles(options));
        Assert.Equal(fetch ? "FetchTimeout" : "CacheLockTimeout", error.ParamName);
    }
}