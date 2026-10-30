using System.Data.Common;
using System.Reflection;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Relational;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class CancelledCommandTranslationTests
{
    [Theory]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    [InlineData("postgresql", false)]
    [InlineData("postgresql", true)]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    public async Task ProviderFailureIsTranslatedOnlyAfterCallerCancellation(string provider, bool cancelled)
    {
        var folder = NewFolder();

        try
        {
            var store = NewStore(provider, folder);
            await using var owned = store as IAsyncDisposable;

            foreach (var operation in new[] { "Schema", "Read", "CancellationRead", "LeaseClaim", "Commit" })
            {
                using var cancel = new CancellationTokenSource();
                var providerFailure = new ProviderFailure();

                var error = await Record.ExceptionAsync(() => RunAsync(
                    store, operation, async () =>
                    {
                        await Task.Yield();

                        if (cancelled)
                            cancel.Cancel();

                        throw providerFailure;
                    }, cancel.Token));

                if (cancelled)
                {
                    var cancellation = Assert.IsType<OperationCanceledException>(error);
                    Assert.Equal(cancel.Token, cancellation.CancellationToken);
                    Assert.Same(providerFailure, cancellation.InnerException);
                }
                else
                    Assert.Same(providerFailure, error);
            }
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    [InlineData("postgresql", false)]
    [InlineData("postgresql", true)]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    public async Task NativeCancellationAndNonProviderFailuresKeepTheirIdentity(string provider, bool nativeCancellation)
    {
        var folder = NewFolder();

        try
        {
            var store = NewStore(provider, folder);
            await using var owned = store as IAsyncDisposable;
            using var cancel = new CancellationTokenSource();

            Exception original = nativeCancellation
                ? new OperationCanceledException(cancel.Token)
                : new InvalidOperationException("A store validation failure");

            var error = await Record.ExceptionAsync(() => RunAsync(
                store, "Read", () =>
                {
                    cancel.Cancel();

                    return Task.FromException<int>(original);
                }, cancel.Token));

            Assert.Same(original, error);
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("schema", false)]
    [InlineData("schema", true)]
    public async Task SqliteBusyFailureUsesCallerCancellationAndPreservesUncancelledClassification(string operation, bool cancelled)
    {
        var folder = NewFolder();

        try
        {
            var connectionString = "Data Source=" + Path.Combine(folder, "store.db") + ";Pooling=False";
            await using var store = new SqliteWorkflowStateRepository(connectionString);

            if (operation != "schema")
                await store.ListPublishersAsync();

            await using var holder = new SqliteConnection(connectionString);
            await holder.OpenAsync();

            // Rollback journaling makes BEGIN EXCLUSIVE block reads as well as writes.
            await holder.ExecuteAsync("PRAGMA journal_mode=DELETE");
            await holder.ExecuteAsync("BEGIN EXCLUSIVE");
            using var cancel = new CancellationTokenSource();

            if (cancelled)
                cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

            try
            {
                var error = await Record.ExceptionAsync(async () =>
                {
                    if (operation == "write")
                        await store.RegisterPublisherAsync(
                            "busy-publisher", [OutboxEvent.WorkflowCompleted], cancellationToken: cancel.Token);
                    else
                        await store.ListPublishersAsync(cancel.Token);
                });

                SqliteException providerFailure;

                if (cancelled)
                {
                    var cancellation = Assert.IsType<OperationCanceledException>(error);
                    Assert.Equal(cancel.Token, cancellation.CancellationToken);
                    providerFailure = Assert.IsType<SqliteException>(cancellation.InnerException);
                }
                else if (operation == "read")
                    providerFailure = Assert.IsType<SqliteException>(error);
                else
                    providerFailure = Assert.IsType<SqliteException>(Assert.IsType<InvalidOperationException>(error).InnerException);

                Assert.Equal(5, providerFailure.SqliteErrorCode);
            }
            finally
            {
                await holder.ExecuteAsync("ROLLBACK");
            }

            Assert.Empty(await store.ListPublishersAsync());
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnlistedPayloadFailureUsesThePayloadOperationsOwnToken(bool cancelled)
    {
        var folder = NewFolder();

        try
        {
            var connectionString = "Data Source=" + Path.Combine(folder, "store.db") + ";Pooling=False;Default Timeout=1";
            await using var store = new SqliteWorkflowStateRepository(connectionString);
            await store.ListPublishersAsync();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction(deferred: true);
            var payloads = store.PayloadStore.Enlist(transaction);
            await using var holder = new SqliteConnection(connectionString);
            await holder.OpenAsync();
            await holder.ExecuteAsync("PRAGMA journal_mode=DELETE");
            await holder.ExecuteAsync("BEGIN EXCLUSIVE");
            using var cancel = new CancellationTokenSource();

            if (cancelled)
                cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

            try
            {
                var error = await Record.ExceptionAsync(async () =>
                    await payloads.PutAsync("{}"u8.ToArray(), cancellationToken: cancel.Token));

                var providerFailure = cancelled
                    ? Assert.IsType<SqliteException>(Assert.IsType<OperationCanceledException>(error).InnerException)
                    : Assert.IsType<SqliteException>(error);

                if (cancelled)
                    Assert.Equal(cancel.Token, ((OperationCanceledException)error!).CancellationToken);

                Assert.Equal(5, providerFailure.SqliteErrorCode);
            }
            finally
            {
                await holder.ExecuteAsync("ROLLBACK");
            }

            await transaction.RollbackAsync();
            Assert.Equal(PayloadReference.Of("{}"u8), await store.PayloadStore.PutAsync("{}"u8.ToArray()));
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    private static Task<int> RunAsync(
        RelationalWorkflowStateRepository store,
        string operation,
        Func<Task<int>> call,
        CancellationToken cancellationToken)
    {
        // Invoke the actual provider runner without opening a server connection or expanding friend access.
        var runner = typeof(RelationalWorkflowStateRepository).GetMethod(
            "ExecuteOperationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));

        var kind = Enum.Parse(runner.GetParameters()[0].ParameterType, operation);

        return (Task<int>)runner.Invoke(store, [kind, call, cancellationToken])!;
    }

    private static RelationalWorkflowStateRepository NewStore(string provider, string folder) => provider switch
    {
        "sqlserver" => new SqlServerWorkflowStateRepository("Server=unused;Database=unused;Integrated Security=True"),
        "postgresql" => new PostgreSqlWorkflowStateRepository("Host=unused;Database=unused"),
        _ => new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "store.db"))
    };

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CancelledCommand", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return folder;
    }

    private sealed class ProviderFailure : DbException;
}