using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability"), Trait("Spec", "A-03"), Trait("Spec", "C-11")]
public sealed class CheckpointRaceTests : IClassFixture<SqliteTestDatabase<CheckpointRaceTests>>
{
    [Theory, InlineData(0), InlineData(1)]
    public Task MemoryWritersRaceAtZeroAndExistingRevision(int revision)
    {
        var store = new InMemoryWorkflowStateRepository();
        return CheckpointRaceContract.ConcurrentWritersPersistOnlyTheWinner(() => store, revision);
    }
    [Theory, InlineData(0), InlineData(1), Trait("Category", "SqliteIntegration")]
    public async Task SqliteWritersRaceThroughTheSingleRepository(int revision)
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<CheckpointRaceTests>.ConnectionString);
        await CheckpointRaceContract.ConcurrentWritersPersistOnlyTheWinner(() => store, revision);
    }
    [Theory, InlineData(0), InlineData(1), Trait("Category", "SqlIntegration")]
    public Task IndependentSqlServerWritersRaceAtZeroAndExistingRevision(int revision) =>
        CheckpointRaceContract.ConcurrentWritersPersistOnlyTheWinner(() =>
            new SqlWorkflowStateRepository(SqlTestDatabase<CheckpointRaceTests>.ConnectionString), revision);

    [Theory, InlineData(0), InlineData(1), Trait("Category", "PostgreSqlIntegration")]
    public Task IndependentPostgreSqlWritersRaceAtZeroAndExistingRevision(int revision) =>
        CheckpointRaceContract.ConcurrentWritersPersistOnlyTheWinner(() =>
            new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CheckpointRaceTests>.ConnectionString), revision);
}
