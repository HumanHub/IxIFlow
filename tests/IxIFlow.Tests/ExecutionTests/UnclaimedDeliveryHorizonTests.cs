using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-11")]
[Trait("Spec", "H-05")]
[Trait("Feature", "durability")]
public sealed class UnclaimedDeliveryHorizonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task MemoryContract(int? horizonDays)
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(time);
        var builder = services.AddIxIFlow();
        if (horizonDays is { } days) builder.ConfigureOutbox(retryHorizon: TimeSpan.FromDays(days));
        await using var provider = services.BuildServiceProvider();
        await OutboxStoreContract.VerifyUnclaimedHorizonAsync(provider.GetRequiredService<IWorkflowStateRepository>(),
            provider.GetRequiredService<OutboxSettings>().RetryHorizon,
            (_, by) => { time.Advance(by); return Task.CompletedTask; }, TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteContract(int? horizonDays)
    {
        var time = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.UnclaimedHorizon", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(time);
        var builder = services.AddIxIFlow().UseSqlite("Data Source=" + Path.Combine(folder, "outbox.db"));
        if (horizonDays is { } days) builder.ConfigureOutbox(retryHorizon: TimeSpan.FromDays(days));
        await using var provider = services.BuildServiceProvider();
        await OutboxStoreContract.VerifyUnclaimedHorizonAsync(provider.GetRequiredService<IWorkflowStateRepository>(),
            provider.GetRequiredService<OutboxSettings>().RetryHorizon,
            (_, by) => { time.Advance(by); return Task.CompletedTask; }, TimeSpan.FromTicks(1));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerContract()
    {
        var connectionString = SqlTestDatabase<UnclaimedDeliveryHorizonTests>.ConnectionString;
        foreach (var horizonDays in new int?[] { null, 2 })
        {
            var builder = new ServiceCollection().AddLogging().AddIxIFlow().UseSqlServer(connectionString);
            if (horizonDays is { } days) builder.ConfigureOutbox(retryHorizon: TimeSpan.FromDays(days));
            await using var provider = builder.Services.BuildServiceProvider();
            await OutboxStoreContract.VerifyUnclaimedHorizonAsync(provider.GetRequiredService<IWorkflowStateRepository>(),
                provider.GetRequiredService<OutboxSettings>().RetryHorizon,
                (ids, by) => AgeRows(new SqlConnection(connectionString), ids, by), TimeSpan.FromMinutes(1));
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlContract()
    {
        var connectionString = PostgreSqlTestDatabase<UnclaimedDeliveryHorizonTests>.ConnectionString;
        foreach (var horizonDays in new int?[] { null, 2 })
        {
            var builder = new ServiceCollection().AddLogging().AddIxIFlow().UsePostgreSql(connectionString);
            if (horizonDays is { } days) builder.ConfigureOutbox(retryHorizon: TimeSpan.FromDays(days));
            await using var provider = builder.Services.BuildServiceProvider();
            Assert.Same(provider.GetRequiredService<IWorkflowStateRepository>(), provider.GetRequiredService<IRunnableWakeSource>());
            await OutboxStoreContract.VerifyUnclaimedHorizonAsync(provider.GetRequiredService<IWorkflowStateRepository>(),
                provider.GetRequiredService<OutboxSettings>().RetryHorizon,
                (ids, by) => AgeRows(new NpgsqlConnection(connectionString), ids, by), TimeSpan.FromMinutes(1));
        }
    }

    // D-35: age only this scenario's durable rows, leaving the server clock authoritative.
    private static async Task AgeRows(DbConnection connection, IReadOnlyCollection<string> ids, TimeSpan by)
    {
        await using (connection)
        {
            var sql = connection is SqlConnection ? """
                UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND,-@Seconds,EndedAtUtc) WHERE InstanceId IN @Ids;
                UPDATE dbo.IxIFlowOutboxEvents SET OccurredAtUtc=DATEADD(SECOND,-@Seconds,OccurredAtUtc) WHERE InstanceId IN @Ids;
                """ : """
                UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=ANY(@Ids);
                UPDATE ixiflow_outbox_events SET occurred_at_utc=occurred_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=ANY(@Ids);
                """;
            await connection.ExecuteAsync(sql, new { Ids = ids.ToArray(), Seconds = (int)by.TotalSeconds });
        }
    }
}
