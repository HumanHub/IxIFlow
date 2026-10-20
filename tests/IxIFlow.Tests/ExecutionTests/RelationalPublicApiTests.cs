using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Core;
using IxIFlow.Relational;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public class RelationalPublicApiTests
{
    [Fact]
    public void PublicSurfaceContainsOnlyTheAbstractProtocolAndInterfaceMethods()
    {
        var protocol = typeof(RelationalWorkflowStateRepository);
        Assert.True(protocol.IsAbstract);
        Assert.Equal([protocol], protocol.Assembly.GetExportedTypes());
        Assert.All(protocol.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsAssembly));
        Assert.Single(protocol.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        Type[] contracts = [typeof(IWorkflowStateRepository), typeof(IWorkflowOutbox), typeof(IWorkflowInbox), typeof(IWorkflowAdmissionStore), typeof(IWorkflowCatalogStore), typeof(IArtifactStore)];
        var implementations = contracts.SelectMany(contract => protocol.GetInterfaceMap(contract).TargetMethods).ToHashSet();
        var members = protocol.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(implementations.Count, members.Length);
        Assert.All(members, member => Assert.Contains(Assert.IsAssignableFrom<MethodInfo>(member), implementations));
        Assert.All(protocol.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.IsVirtual), method => Assert.True(method.IsAssembly));
        Assert.All(protocol.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic),
            nested => Assert.True(nested.IsNestedAssembly || nested.IsNestedPrivate));
    }

    [Fact]
    public void OnlyTheThreeStoreAssembliesHaveFriendAccess()
    {
        var friends = typeof(RelationalWorkflowStateRepository).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["IxIFlow.PostgreSql", "IxIFlow.SqlServer", "IxIFlow.Sqlite"], friends);
    }

    [Theory]
    [InlineData(typeof(SqlWorkflowStateRepository))]
    [InlineData(typeof(PostgreSqlWorkflowStateRepository))]
    [InlineData(typeof(SqliteWorkflowStateRepository))]
    public void StoresInheritTheProtocolDirectly(Type store)
    {
        Assert.True(store.IsPublic);
        Assert.True(store.IsSealed);
        Assert.Equal(typeof(RelationalWorkflowStateRepository), store.BaseType);
    }
}
