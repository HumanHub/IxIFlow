using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

// Shared store scenarios are compiled here and executed with the owner's database suites.
[Trait("Spec", "H-08")]
[Trait("Feature", "tracing")]
public sealed class RedactionReviewServerStoreTests
{
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerRetainsOperatorNotes() => RedactionReviewFixTests.VerifyNoteRetention(SqlServer());

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRetainsOperatorNotes() => RedactionReviewFixTests.VerifyNoteRetention(PostgreSql());

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerBindsPrivilegedReadsToIncarnations()
    {
        await RedactionReviewFixTests.VerifyIncarnationRead(SqlServer(), false);
        await RedactionReviewFixTests.VerifyIncarnationRead(SqlServer(), true);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlBindsPrivilegedReadsToIncarnations()
    {
        await RedactionReviewFixTests.VerifyIncarnationRead(PostgreSql(), false);
        await RedactionReviewFixTests.VerifyIncarnationRead(PostgreSql(), true);
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerReportsRemainingParallelFailure() => RedactionReviewFixTests.VerifyParallelResult(SqlServer());

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlReportsRemainingParallelFailure() => RedactionReviewFixTests.VerifyParallelResult(PostgreSql());

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerSkipsDiagnosticFreePruning() => RedactionReviewFixTests.VerifyNoPruning(SqlServer());

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlSkipsDiagnosticFreePruning() => RedactionReviewFixTests.VerifyNoPruning(PostgreSql());

    private static SqlWorkflowStateRepository SqlServer() => new(SqlTestDatabase<RedactionReviewServerStoreTests>.ConnectionString);
    private static PostgreSqlWorkflowStateRepository PostgreSql() => new(PostgreSqlTestDatabase<RedactionReviewServerStoreTests>.ConnectionString);
}
