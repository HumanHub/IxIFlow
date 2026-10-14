using System.Reflection;
using System.Reflection.Emit;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

public class WorkflowTypeIdentityTests
{
    [Fact]
    public void DefinitionFingerprintSurvivesAnActivityAssemblyVersionChange()
    {
        var first = VersionedType(new Version(1, 0, 0, 0));
        var second = VersionedType(new Version(2, 0, 0, 0));
        Assert.NotEqual(first.AssemblyQualifiedName, second.AssemblyQualifiedName);

        var firstDefinition = new WorkflowDefinition
        {
            Steps = [new WorkflowStep { StepType = WorkflowStepType.Activity, ActivityType = first }]
        };
        var secondDefinition = new WorkflowDefinition
        {
            Steps = [new WorkflowStep { StepType = WorkflowStepType.Activity, ActivityType = second }]
        };

        Assert.Equal(new WorkflowScopeCatalog(firstDefinition).Fingerprint,
            new WorkflowScopeCatalog(secondDefinition).Fingerprint);
    }

    [Fact]
    public void SavedTypeResolvesAgainstTheCurrentlyLoadedAssemblyVersion()
    {
        var savedName = typeof(WorkflowTypeIdentityTests).AssemblyQualifiedName!;
        var changedVersion = System.Text.RegularExpressions.Regex.Replace(savedName,
            @"Version=\d+\.\d+\.\d+\.\d+", "Version=99.0.0.0");
        Assert.NotEqual(savedName, changedVersion);

        Assert.Equal(typeof(WorkflowTypeIdentityTests), WorkflowTypeIdentity.Resolve(changedVersion));
    }

    private static Type VersionedType(Version version)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("IxIFlow.VersionedActivity") { Version = version },
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Activities");
        return module.DefineType("Sample.ReserveActivity", TypeAttributes.Public).CreateType()!;
    }
}
