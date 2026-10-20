using System.Reflection;
using IxIFlow.Core;

namespace IxIFlow.Tests;

/// <summary>
/// The host lives in the core package and the coordinator in its own; the old distributed package is gone (spec C-19).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "C-19")]
public sealed class ProjectBoundaryTests
{
    [Fact]
    public void TheHostIsInTheCoreAndTheCoordinatorInItsOwnPackage()
    {
        var core = typeof(WorkflowHost).Assembly;
        Assert.Equal("IxIFlow", core.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(),
            reference => reference.Name is "IxIFlow.Coordinator" or "IxIFlow.Distributed");

        var coordinator = Assembly.Load("IxIFlow.Coordinator");
        Assert.Contains(coordinator.GetTypes(), type => type.FullName == "IxIFlow.Coordinator.CoordinatorService");
        Assert.DoesNotContain(core.GetTypes(), type => type.Name == "CoordinatorService");
    }

    [Fact]
    public void TheDistributedPackageNoLongerExists()
    {
        Assert.Throws<FileNotFoundException>(() => Assembly.Load("IxIFlow.Distributed"));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "IxIFlow.Distributed");
    }
}
