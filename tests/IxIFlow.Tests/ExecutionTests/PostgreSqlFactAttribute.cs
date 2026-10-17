namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A test that needs PostgreSQL. It always runs: against IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING when set,
/// otherwise against a Testcontainers PostgreSQL (see PostgreSqlTestServer). Without Docker it fails.
/// </summary>
public sealed class PostgreSqlFactAttribute : FactAttribute
{
}
