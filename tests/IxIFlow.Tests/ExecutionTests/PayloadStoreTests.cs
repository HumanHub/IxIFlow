using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "H-06")]
[Trait("Spec", "H-06")]
public sealed class PayloadStoreTests
{
    [Fact]
    public Task MemoryPayloadStoreContract()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time, payloadSettings: new(1024));
        var store = repository.PayloadStore;
        var rows = () => (Dictionary<string, PayloadRecord>)store.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return new PayloadStoreContract(store,
            (reference, body) => { rows()[reference.Digest].Body = body; return Task.CompletedTask; }).VerifyAsync();
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqlitePayloadStoreContract()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Payloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "payloads.db");
        try
        {
            await using var repository = new SqliteWorkflowStateRepository(connectionString, payloadSettings: new(1024));
            await VerifyRelational(repository.PayloadStore, () => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerPayloadStoreContract()
    {
        var connectionString = SqlTestDatabase<PayloadStoreTests>.ConnectionString;
        return VerifyRelational(new SqlWorkflowStateRepository(connectionString, payloadSettings: new(1024)).PayloadStore,
            () => new SqlConnection(connectionString));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlPayloadStoreContract()
    {
        var connectionString = PostgreSqlTestDatabase<PayloadStoreTests>.ConnectionString;
        return VerifyRelational(new PostgreSqlWorkflowStateRepository(connectionString, payloadSettings: new(1024)).PayloadStore,
            () => new NpgsqlConnection(connectionString));
    }

    private static async Task VerifyRelational(IPayloadStore store, Func<DbConnection> open)
    {
        await new PayloadStoreContract(store, async (reference, body) =>
        {
            await using var connection = open();
            var table = connection is SqlConnection ? "dbo.IxIFlowWorkflowPayloads" : "ixiflow_workflow_payloads";
            var column = connection is SqlConnection ? "Body" : "body";
            var digest = connection is SqlConnection ? "Digest" : "digest";
            await connection.ExecuteAsync($"UPDATE {table} SET {column}=@Body WHERE {digest}=@Digest", new { Body = body, reference.Digest });
        }).VerifyAsync();

        // Enlist uses the caller's transaction: rollback and commit decide pin visibility together with
        // the referencing SQL. No independent payload transaction may acknowledge a pin here.
        var body = "\"enlisted\""u8.ToArray();
        var reference = await store.PutAsync(body);
        await using var connection = open();
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var pins = store.Enlist(transaction);
            Assert.Same(transaction, pins.DatabaseTransaction);
            await pins.PinAsync(new("reader", "enlisted"), reference);
            await transaction.RollbackAsync();
        }
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        await store.PutAsync(body);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.Enlist(transaction).PinAsync(new("reader", "enlisted"), reference);
            await transaction.CommitAsync();
        }
        Assert.False(await store.CollectAsync(reference, TimeSpan.Zero));
    }

    [Fact]
    public void BuilderDefaultsUseTheStateStore()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow(options => options.MaxPayloadBytes = 17);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(0, provider.GetRequiredService<PayloadStoreSettings>().PayloadThresholdBytes);
        Assert.Equal(17, provider.GetRequiredService<PayloadStoreSettings>().MaxPayloadBytes);
        Assert.Same(provider.GetRequiredService<IWorkflowStateRepository>().PayloadStore,
            provider.GetRequiredService<IPayloadStore>());
        Assert.Equal(64L * 1024 * 1024, new IxIFlowOptions().MaxPayloadBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(o => o.MaxPayloadBytes = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(o => o.PayloadThresholdBytes = -1));
    }

    [Fact]
    public void StoredJsonRoundTripsBothForms()
    {
        var inline = StoredJson.Inline("{\"value\":\"é\",\"reference\":{\"digest\":\"application-data\"}}");
        Assert.Equal(inline, JsonSerializer.Deserialize<StoredJson>(JsonSerializer.Serialize(inline)));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(inline.InlineText), inline.Bytes);
        var reference = StoredJson.FromReference(inline.Identity);
        Assert.Equal(reference, JsonSerializer.Deserialize<StoredJson>(JsonSerializer.Serialize(reference)));
        Assert.Throws<InvalidOperationException>(() => reference.InlineText);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StoredJson>("{}"));
    }
}
