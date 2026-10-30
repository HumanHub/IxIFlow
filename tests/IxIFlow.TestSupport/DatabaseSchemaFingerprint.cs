using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// An immutable reference captured from a separate, freshly initialized store, never from a test's database.
/// Catalog queries use logical names, not object IDs, allocation statistics or generated constraint names.
/// All commands run on the caller's exclusive reset session. A mismatch always selects the old full reset.
/// </summary>
internal sealed partial class DatabaseSchemaFingerprint
{
    internal sealed record Table(string Name, string[] Columns, object?[][] Seeds, bool Identity);

    internal sealed record Sequence(string Name, string Start);

    internal string Schema { get; }
    internal string Data { get; }
    internal Table[] Tables { get; }
    private readonly Sequence[] _sequences;
    private readonly bool _sqlServer;

    private DatabaseSchemaFingerprint(bool sqlServer, string schema, string data, Table[] tables, Sequence[] sequences)
    {
        _sqlServer = sqlServer;
        Schema = schema;
        Data = data;
        Tables = tables;
        _sequences = sequences;
    }

    internal static async Task<DatabaseSchemaFingerprint> CaptureAsync(DbConnection connection, bool sqlServer)
    {
        var schema = await ReadSchemaAsync(connection, sqlServer);

        var names = await ReadAsync(connection, sqlServer
            ? "SELECT QUOTENAME(SCHEMA_NAME(schema_id))+'.'+QUOTENAME(name) FROM sys.tables WHERE is_ms_shipped=0"
            : "SELECT quote_ident(n.nspname)||'.'||quote_ident(c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND n.nspname !~ '^pg_' AND n.nspname<>'information_schema'");

        var dependencies = await ReadAsync(connection, sqlServer
            ? "SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+'.'+QUOTENAME(OBJECT_NAME(parent_object_id)), QUOTENAME(OBJECT_SCHEMA_NAME(referenced_object_id))+'.'+QUOTENAME(OBJECT_NAME(referenced_object_id)) FROM sys.foreign_keys"
            : "SELECT format('%I.%I',cn.nspname,c.relname),format('%I.%I',pn.nspname,p.relname) FROM pg_constraint f JOIN pg_class c ON c.oid=f.conrelid JOIN pg_namespace cn ON cn.oid=c.relnamespace JOIN pg_class p ON p.oid=f.confrelid JOIN pg_namespace pn ON pn.oid=p.relnamespace WHERE f.contype='f'");

        var order = DeleteOrder(names.Select(row => (string)row[0]!), dependencies.Select(row => ((string)row[0]!, (string)row[1]!)));
        var tables = new List<Table>();

        foreach (var name in order)
        {
            var identity = sqlServer && (await ReadAsync(connection,
                $"SELECT 1 FROM sys.identity_columns WHERE object_id=OBJECT_ID(N'{name.Replace("'", "''")}')")).Length != 0;

            await using var command = Command(connection, $"SELECT * FROM {name}");
            await using var reader = await command.ExecuteReaderAsync();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = await RowsAsync(reader);
            tables.Add(new(name, columns, rows, identity));
        }

        var sequences = new List<Sequence>();

        foreach (var row in await ReadAsync(connection, sqlServer
                     ? "SELECT QUOTENAME(SCHEMA_NAME(schema_id))+'.'+QUOTENAME(name), CONVERT(varchar(40),start_value) FROM sys.sequences"
                     : "SELECT format('%I.%I',n.nspname,c.relname),s.seqstart::text FROM pg_sequence s JOIN pg_class c ON c.oid=s.seqrelid JOIN pg_namespace n ON n.oid=c.relnamespace"))
        {
            var name = (string)row[0]!;
            sequences.Add(new(name, (string)row[1]!));
        }

        var data = await ReadDataAsync(connection, tables, sequences, sqlServer);

        return new(sqlServer, schema, data, tables.ToArray(), sequences.ToArray());
    }

    // Children precede parents; a cycle cannot be safely deleted with constraints enabled.
    internal static string[] DeleteOrder(IEnumerable<string> tables, IEnumerable<(string Child, string Parent)> dependencies)
    {
        var remaining = tables.ToHashSet(StringComparer.Ordinal);
        var edges = dependencies.Where(edge => edge.Child != edge.Parent).ToArray();
        var result = new List<string>();

        while (remaining.Count != 0)
        {
            var next = remaining.Where(table => !edges.Any(edge => edge.Parent == table && remaining.Contains(edge.Child)))
                .Order(StringComparer.Ordinal).ToArray();

            if (next.Length == 0)
                throw new InvalidOperationException("Cyclic test schema foreign keys require a full reset.");

            result.AddRange(next);
            remaining.ExceptWith(next);
        }

        return result.ToArray();
    }

    internal async Task<bool> TryWipeAsync(DbConnection connection)
    {
        if (Schema != await ReadSchemaAsync(connection, _sqlServer))
            return false;

        // The surrounding reset transaction commits only after the fingerprint is verified.
        if (_sqlServer)
        {
            await ExecuteAsync(connection, string.Join("\n", Tables.Select(table => $"DELETE FROM {table.Name};")));

            foreach (var table in Tables.Where(table => table.Identity))
            {
                var name = table.Name.Replace("'", "''");

                await ExecuteAsync(connection, $"""
                                                DECLARE @seed decimal(38,0) = IDENT_SEED(N'{name}') - IDENT_INCR(N'{name}');
                                                DBCC CHECKIDENT (N'{name}', RESEED, @seed) WITH NO_INFOMSGS;
                                                """);
            }
        }
        else
            await ExecuteAsync(connection, "TRUNCATE TABLE " + string.Join(",", Tables.Select(table => table.Name)) + " RESTART IDENTITY;");

        // Parents are restored before children. These are the exact typed rows from the pristine reference.
        foreach (var table in Tables.Reverse().Where(table => table.Seeds.Length != 0))
        {
            if (table.Identity)
                await ExecuteAsync(connection, $"SET IDENTITY_INSERT {table.Name} ON;");

            foreach (var row in table.Seeds)
            {
                await using var command = Command(connection,
                    $"INSERT INTO {table.Name} ({string.Join(",", table.Columns.Select(Quote))}) VALUES ({string.Join(",", row.Select((_, i) => "@p" + i))});");

                for (var i = 0; i < row.Length; i++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "p" + i;
                    parameter.Value = row[i] ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                await command.ExecuteNonQueryAsync();
            }

            if (table.Identity)
                await ExecuteAsync(connection, $"SET IDENTITY_INSERT {table.Name} OFF;");
        }

        foreach (var sequence in _sequences)
            await ExecuteAsync(connection, $"ALTER SEQUENCE {sequence.Name} RESTART WITH {sequence.Start};");

        if (!await MatchesFreshAsync(connection))
            throw new InvalidOperationException("Row reset differs from the fresh store fingerprint; database retired.");

        return true;
    }

    internal async Task<bool> MatchesFreshAsync(DbConnection connection)
    {
        var schema = await ReadSchemaAsync(connection, _sqlServer);

        if (Schema != schema)
        {
            Console.Error.WriteLine($"Test database schema fingerprint mismatch: expected={Schema}, actual={schema}");

            return false;
        }

        var data = await ReadDataAsync(connection, Tables, _sequences, _sqlServer);

        if (Data == data)
            return true;

        Console.Error.WriteLine($"Test database row/sequence fingerprint mismatch: expected={Data}, actual={data}");

        return false;
    }

    private string Quote(string name) => _sqlServer ? "[" + name.Replace("]", "]]") + "]" : "\"" + name.Replace("\"", "\"\"") + "\"";

    internal static async Task<string> ReadSchemaAsync(DbConnection connection, bool sqlServer)
    {
        await using var command = Command(connection, sqlServer ? SqlServerCatalog : PostgreSqlCatalog);
        await using var reader = await command.ExecuteReaderAsync();
        var sets = new List<string>();

        do
        {
            sets.Add(Canonical(await RowsAsync(reader)));
        } while (await reader.NextResultAsync());

        return Hash(JsonSerializer.Serialize(sets));
    }

    private static async Task<string> ReadDataAsync(DbConnection connection, IEnumerable<Table> tables, IEnumerable<Sequence> sequences,
        bool sqlServer)
    {
        var states = new List<string>();

        // One round trip for all 38 tables, including empty tables and every seed column.
        await using (var command = Command(connection, string.Join("\n", tables.Select(table => $"SELECT * FROM {table.Name};"))))
        await using (var reader = await command.ExecuteReaderAsync())
            do
            {
                states.Add(Canonical(await RowsAsync(reader)));
            } while (await reader.NextResultAsync());

        foreach (var sequence in sequences)
            states.Add(await SequenceStateAsync(connection, sequence.Name, sqlServer));

        if (sqlServer)
            states.Add(Canonical(await ReadAsync(connection,
                "SELECT OBJECT_SCHEMA_NAME(object_id),OBJECT_NAME(object_id),name,CONVERT(varchar(40),last_value) FROM sys.identity_columns")));

        return Hash(JsonSerializer.Serialize(states));
    }

    private static async Task<string> SequenceStateAsync(DbConnection connection, string name, bool sqlServer) =>
        Canonical(await ReadAsync(connection, sqlServer
            ? $"SELECT CONVERT(varchar(40),current_value) FROM sys.sequences WHERE object_id=OBJECT_ID(N'{name.Replace("'", "''")}')"
            : $"SELECT last_value,is_called FROM {name}"));

    internal static string Canonical(IEnumerable<object?[]> rows) =>
        JsonSerializer.Serialize(rows.Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<object?[][]> RowsAsync(DbDataReader reader)
    {
        var rows = new List<object?[]>();

        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(value => value is DBNull ? null : value).ToArray());
        }

        return rows.ToArray();
    }

    private static async Task<object?[][]> ReadAsync(DbConnection connection, string sql)
    {
        await using var command = Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync();

        return await RowsAsync(reader);
    }

    internal static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync();
    }

    private static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;

        return command;
    }
}