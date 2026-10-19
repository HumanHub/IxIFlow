using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "C-11")]
public class RelationalStatementTests
{
    [Theory]
    [InlineData(typeof(SqlWorkflowStateRepository), null)]
    [InlineData(typeof(PostgreSqlWorkflowStateRepository), "")]
    [InlineData(typeof(SqliteWorkflowStateRepository), " ")]
    public void MissingStatementNamesProviderAndStatement(Type publicStore, string? sql)
    {
        // Inspect the internal contract without granting the test assembly friend access.
        var provider = publicStore.Assembly.GetTypes().Single(type =>
            type.BaseType?.Name == "RelationalWorkflowStateRepository");
        var protocol = provider.BaseType!;
        var statement = Enum.Parse(protocol.GetNestedType("Statement", BindingFlags.NonPublic)!, "InsertCommit");
        var require = protocol.GetMethod("RequireSql", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = RuntimeHelpers.GetUninitializedObject(provider);

        var invocation = Assert.Throws<TargetInvocationException>(() => require.Invoke(store, [statement, sql]));
        var error = Assert.IsType<InvalidOperationException>(invocation.InnerException);
        Assert.Equal($"Relational provider '{provider.FullName}' is missing required statement 'InsertCommit'.", error.Message);
    }
}
