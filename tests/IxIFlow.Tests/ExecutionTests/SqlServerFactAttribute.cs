namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A test that needs SQL Server. It always runs: against IXIFLOW_TEST_SQL_CONNECTION_STRING when set,
/// otherwise against a Testcontainers SQL Server (see SqlServerTestServer). Without Docker it fails.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
}
