using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationHistoryRound5SchemaScenarios
{
    [Theory]
    [InlineData("missing-column")]
    [InlineData("ordinary-column")]
    [InlineData("not-persisted")]
    [InlineData("wrong-expression")]
    [InlineData("wrong-collation")]
    [InlineData("short-key")]
    [InlineData("unicode-key")]
    [InlineData("native-id-index")]
    [InlineData("wrong-order")]
    [InlineData("descending")]
    [InlineData("filtered")]
    [InlineData("disabled")]
    public async Task PublicationSchemaRequiresThePersistedCanonicalIdAndMatchingHistoryIndex(string fault)
    {
        await using var database = await StoreProfiles.CreateAsync(StoreProfiles.DistributedSqlServer, emptySchema: true);
        await database.NewRepository().GetCatalogRevisionAsync();

        // Positive control: run the reopening probe against the intact schema first.
        await database.NewRepository().GetCatalogRevisionAsync();
        await using var connection = database.NewConnection();
        const string table = "dbo.IxIFlowPublicationAttempts";
        const string index = "ix_publication_history";

        if (fault == "disabled")
            await connection.ExecuteAsync($"ALTER INDEX {index} ON {table} DISABLE");
        else
        {
            await connection.ExecuteAsync($"DROP INDEX {index} ON {table}");

            if (fault is "missing-column" or "ordinary-column" or "not-persisted" or "wrong-expression" or "wrong-collation" or "short-key"
                or "unicode-key")
            {
                await connection.ExecuteAsync($"ALTER TABLE {table} DROP COLUMN IdOrder");

                if (fault == "missing-column")
                {
                    await RejectedAsync();

                    return;
                }

                var type = fault == "unicode-key" ? "NVARCHAR" : "VARCHAR";
                var length = fault == "short-key" ? 32 : 36;
                var expression = $"LOWER(CONVERT({type}({length}), Id))";

                if (fault == "wrong-expression")
                    expression = "UPPER(CONVERT(VARCHAR(36), Id))";

                var collation = fault == "wrong-collation" ? "Latin1_General_100_CI_AS" : "Latin1_General_100_BIN2";

                await connection.ExecuteAsync($"ALTER TABLE {table} ADD IdOrder " + (fault == "ordinary-column"
                    ? "VARCHAR(36) COLLATE Latin1_General_100_BIN2 NULL"
                    : $"AS ({expression} COLLATE {collation})" + (fault == "not-persisted" ? "" : " PERSISTED")));
            }

            var keys = fault switch
            {
                "native-id-index" => "Kind, Name, Version, StartedAtUtc, Id",
                "wrong-order" => "Kind, Name, Version, IdOrder, StartedAtUtc",
                "descending" => "Kind, Name, Version, StartedAtUtc, IdOrder DESC",
                _ => "Kind, Name, Version, StartedAtUtc, IdOrder"
            };

            await connection.ExecuteAsync($"CREATE INDEX {index} ON {table} ({keys})" +
                (fault == "filtered" ? " WHERE FinishedAtUtc IS NOT NULL" : ""));
        }

        await RejectedAsync();

        async Task RejectedAsync()
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => database.NewRepository().GetCatalogRevisionAsync());
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("earlier build", error.Message);
        }
    }
}