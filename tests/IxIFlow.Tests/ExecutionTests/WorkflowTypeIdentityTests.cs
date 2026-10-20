using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public class WorkflowTypeIdentityTests
{
    [Fact]
    public void StructureHashSurvivesAnActivityAssemblyVersionChange()
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

        Assert.Equal(new WorkflowScopeCatalog(firstDefinition).StructureHash,
            new WorkflowScopeCatalog(secondDefinition).StructureHash);
    }

    [Fact]
    public void SavedTypeResolvesAgainstTheCurrentlyLoadedAssemblyVersion()
    {
        var loadedType = typeof(WorkflowTypeIdentityTests);
        var savedContext = new AssemblyLoadContext("saved-type-version", isCollectible: true);
        try
        {
            Type savedType;
            using (savedContext.EnterContextualReflection())
            {
                var assembly = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName(loadedType.Assembly.GetName().Name!) { Version = new Version(99, 0, 0, 0) },
                    AssemblyBuilderAccess.RunAndCollect);
                savedType = assembly.DefineDynamicModule("Tests")
                    .DefineType(loadedType.FullName!, TypeAttributes.Public).CreateType()!;
            }
            var savedName = WorkflowTypeIdentity.SavedName(savedType);
            Assert.NotEqual(loadedType.AssemblyQualifiedName, savedType.AssemblyQualifiedName);
            Assert.Equal(WorkflowTypeIdentity.SavedName(loadedType), savedName);

            Assert.Equal(loadedType, WorkflowTypeIdentity.Resolve(savedName));
        }
        finally { savedContext.Unload(); }
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
