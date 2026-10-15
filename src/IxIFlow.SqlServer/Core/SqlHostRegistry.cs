using Microsoft.Data.SqlClient;
using Dapper;

namespace IxIFlow.Core;

/// <summary>
/// SQL Server-based host registry implementation for distributed workflow communication
/// Manages host registration, discovery, and health tracking with SQL persistence
/// </summary>
public class SqlHostRegistry : IHostRegistry, IHostRegistryMaintenance
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public SqlHostRegistry(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task RegisterHostAsync(string hostId, string endpointUrl, HostCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));
        if (string.IsNullOrWhiteSpace(endpointUrl)) throw new ArgumentException("EndpointUrl cannot be empty", nameof(endpointUrl));
        if (capabilities == null) throw new ArgumentNullException(nameof(capabilities));

        await using var connection = await OpenConnectionAsync();

        await connection.ExecuteAsync(@"
            MERGE HostRegistry AS target
            USING (VALUES (@HostId, @EndpointUrl, @Tags, @Weight, @MaxConcurrentWorkflows, @LastHeartbeat, 1, GETUTCDATE(), GETUTCDATE()))
                AS source (HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive, CreatedAt, UpdatedAt)
            ON target.HostId = source.HostId
            WHEN MATCHED THEN
                UPDATE SET 
                    EndpointUrl = source.EndpointUrl,
                    Tags = source.Tags,
                    Weight = source.Weight,
                    MaxConcurrentWorkflows = source.MaxConcurrentWorkflows,
                    LastHeartbeat = source.LastHeartbeat,
                    IsActive = 1,
                    UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive, CreatedAt, UpdatedAt)
                VALUES (source.HostId, source.EndpointUrl, source.Tags, source.Weight, source.MaxConcurrentWorkflows, source.LastHeartbeat, source.IsActive, source.CreatedAt, source.UpdatedAt);",
            new
            {
                HostId = hostId,
                EndpointUrl = endpointUrl,
                Tags = string.Join(",", capabilities.Tags),
                Weight = capabilities.Weight,
                MaxConcurrentWorkflows = capabilities.MaxConcurrentWorkflows,
                LastHeartbeat = DateTime.UtcNow
            });
    }

    public async Task UnregisterHostAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        await using var connection = await OpenConnectionAsync();

        await connection.ExecuteAsync(@"
            UPDATE HostRegistry 
            SET IsActive = 0, UpdatedAt = GETUTCDATE(), LastHeartbeat = GETUTCDATE()
            WHERE HostId = @HostId",
            new { HostId = hostId });
    }

    public async Task<HostRegistration[]> GetRegisteredHostsAsync()
    {
        await using var connection = await OpenConnectionAsync();

        var hosts = await connection.QueryAsync<HostRegistrationRow>(@"
            SELECT HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive
            FROM HostRegistry 
            WHERE IsActive = 1");

        return hosts.Select(h => new HostRegistration
        {
            HostId = h.HostId,
            EndpointUrl = h.EndpointUrl,
            Tags = ParseTags(h.Tags),
            Weight = h.Weight,
            MaxConcurrentWorkflows = h.MaxConcurrentWorkflows,
            LastHeartbeat = h.LastHeartbeat,
            IsActive = h.IsActive
        }).ToArray();
    }

    public async Task<HostRegistration?> GetHostAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        await using var connection = await OpenConnectionAsync();

        var host = await connection.QuerySingleOrDefaultAsync<HostRegistrationRow>(@"
            SELECT HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive
            FROM HostRegistry 
            WHERE HostId = @HostId AND IsActive = 1",
            new { HostId = hostId });

        if (host == null) return null;

        return new HostRegistration
        {
            HostId = host.HostId,
            EndpointUrl = host.EndpointUrl,
            Tags = ParseTags(host.Tags),
            Weight = host.Weight,
            MaxConcurrentWorkflows = host.MaxConcurrentWorkflows,
            LastHeartbeat = host.LastHeartbeat,
            IsActive = host.IsActive
        };
    }

    public async Task UpdateHeartbeatAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        await using var connection = await OpenConnectionAsync();

        await connection.ExecuteAsync(@"
            UPDATE HostRegistry 
            SET LastHeartbeat = GETUTCDATE(), IsActive = 1, UpdatedAt = GETUTCDATE()
            WHERE HostId = @HostId",
            new { HostId = hostId });
    }

    public async Task<HostRegistration[]> GetActiveHostsAsync(TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromMinutes(5));

        await using var connection = await OpenConnectionAsync();

        var hosts = await connection.QueryAsync<HostRegistrationRow>(@"
            SELECT HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive
            FROM HostRegistry 
            WHERE IsActive = 1 AND LastHeartbeat >= @Cutoff",
            new { Cutoff = cutoff });

        return hosts.Select(h => new HostRegistration
        {
            HostId = h.HostId,
            EndpointUrl = h.EndpointUrl,
            Tags = ParseTags(h.Tags),
            Weight = h.Weight,
            MaxConcurrentWorkflows = h.MaxConcurrentWorkflows,
            LastHeartbeat = h.LastHeartbeat,
            IsActive = h.IsActive
        }).ToArray();
    }

    public async Task<HostRegistration[]> FindHostsByTagsAsync(string[] requiredTags, string[]? preferredTags = null)
    {
        if (requiredTags == null) throw new ArgumentNullException(nameof(requiredTags));

        await using var connection = await OpenConnectionAsync();

        string sql = @"
            SELECT HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive
            FROM HostRegistry 
            WHERE IsActive = 1";

        var parameters = new DynamicParameters();

        if (requiredTags.Length > 0)
        {
            var tagConditions = new List<string>();
            for (int i = 0; i < requiredTags.Length; i++)
            {
                tagConditions.Add($"EXISTS (SELECT 1 FROM STRING_SPLIT(HostRegistry.Tags, ',') tag " +
                    $"WHERE LTRIM(RTRIM(tag.value)) = @RequiredTag{i})");
                parameters.Add($"RequiredTag{i}", requiredTags[i]);
            }
            sql += $" AND ({string.Join(" AND ", tagConditions)})";
        }

        var hosts = await connection.QueryAsync<HostRegistrationRow>(sql, parameters);

        var result = hosts.Select(h => new HostRegistration
        {
            HostId = h.HostId,
            EndpointUrl = h.EndpointUrl,
            Tags = ParseTags(h.Tags),
            Weight = h.Weight,
            MaxConcurrentWorkflows = h.MaxConcurrentWorkflows,
            LastHeartbeat = h.LastHeartbeat,
            IsActive = h.IsActive
        }).ToList();

        // Sort by preferred tags if specified
        if (preferredTags?.Length > 0)
        {
            result = result.OrderByDescending(h => 
                preferredTags.Count(tag => h.Tags.Contains(tag))).ToList();
        }

        return result.ToArray();
    }

    public async Task<int> CleanupStaleHostsAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        await using var connection = await OpenConnectionAsync();

        return await connection.ExecuteAsync(@"
            UPDATE HostRegistry 
            SET IsActive = 0, UpdatedAt = GETUTCDATE()
            WHERE LastHeartbeat < @Cutoff AND IsActive = 1",
            new { Cutoff = cutoff });
    }

    public async Task UpdateHostStatusAsync(string hostId, HostStatus status)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));
        if (status == null) throw new ArgumentNullException(nameof(status));

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var updated = await connection.ExecuteAsync("""
            UPDATE dbo.HostRegistry
            SET LastHeartbeat = SYSUTCDATETIME(), IsActive = @IsHealthy,
                UpdatedAt = SYSUTCDATETIME()
            WHERE HostId = @HostId
            """, new { HostId = hostId, status.IsHealthy }, transaction);
        if (updated != 1)
            throw new KeyNotFoundException($"Host '{hostId}' is not registered");

        await connection.ExecuteAsync(@"
            INSERT INTO HostMetrics (Id, HostId, CurrentWorkflowCount, MaxWorkflowCount, CpuUsage, MemoryUsage, Status, RecordedAt)
            VALUES (@Id, @HostId, @CurrentWorkflowCount, @MaxWorkflowCount, @CpuUsage, @MemoryUsage, @Status, @RecordedAt)",
            new
            {
                Id = Guid.NewGuid().ToString(),
                HostId = hostId,
                CurrentWorkflowCount = status.CurrentWorkflowCount,
                MaxWorkflowCount = status.MaxConcurrentWorkflows,
                CpuUsage = status.CpuUsage,
                MemoryUsage = status.MemoryUsage,
                Status = status.Status,
                RecordedAt = DateTime.UtcNow
            }, transaction);
        await transaction.CommitAsync();
    }

    public async Task<HostStatus[]> GetAllHostStatusAsync()
    {
        await using var connection = await OpenConnectionAsync();

        // Include unhealthy hosts so operators can see their last report.
        var metrics = await connection.QueryAsync<HostMetricRow>(@"
            SELECT m.HostId, m.CurrentWorkflowCount, m.MaxWorkflowCount, m.CpuUsage, m.MemoryUsage, m.Status, m.RecordedAt,
                   r.EndpointUrl, r.Tags, r.Weight, r.LastHeartbeat, r.IsActive
            FROM HostRegistry r
            CROSS APPLY (
                SELECT TOP (1) * FROM HostMetrics
                WHERE HostId = r.HostId
                ORDER BY RecordedAt DESC, Id DESC
            ) m");

        return metrics.Select(m => new HostStatus
        {
            HostId = m.HostId,
            IsHealthy = m.IsActive,
            CurrentWorkflowCount = m.CurrentWorkflowCount,
            MaxConcurrentWorkflows = m.MaxWorkflowCount,
            Weight = m.Weight,
            Tags = ParseTags(m.Tags),
            EndpointUrl = m.EndpointUrl,
            LastHeartbeat = m.LastHeartbeat,
            CpuUsage = m.CpuUsage,
            MemoryUsage = m.MemoryUsage,
            Status = m.Status
        }).ToArray();
    }

    public async Task<int> PruneMetricsAsync(TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        if (batchSize < 1 || batchSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE TOP (@BatchSize) FROM dbo.HostMetrics
            WHERE RecordedAt < @CutoffUtc
            """, new { BatchSize = batchSize, CutoffUtc = DateTime.UtcNow - retention },
            cancellationToken: cancellationToken));
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        await EnsureSchemaAsync();
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;

        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var lockResult = await connection.ExecuteScalarAsync<int>("""
                DECLARE @result INT;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'IxIFlow.HostRegistrySchema',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 30000;
                SELECT @result;
                """, transaction: transaction);
            if (lockResult < 0)
                throw new InvalidOperationException($"Could not lock the host registry schema ({lockResult})");

            await connection.ExecuteAsync("""
                IF OBJECT_ID(N'dbo.HostRegistry', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.HostRegistry (
                        HostId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        EndpointUrl NVARCHAR(500) NOT NULL,
                        Tags NVARCHAR(1000) NOT NULL DEFAULT '',
                        Weight INT NOT NULL DEFAULT 1,
                        MaxConcurrentWorkflows INT NOT NULL DEFAULT 100,
                        LastHeartbeat DATETIME2 NOT NULL,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                    CREATE INDEX IX_HostRegistry_IsActive_LastHeartbeat
                        ON dbo.HostRegistry (IsActive, LastHeartbeat);
                END;
                IF OBJECT_ID(N'dbo.HostMetrics', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.HostMetrics (
                        Id NVARCHAR(50) NOT NULL PRIMARY KEY,
                        HostId NVARCHAR(100) NOT NULL,
                        CurrentWorkflowCount INT NOT NULL,
                        MaxWorkflowCount INT NOT NULL,
                        CpuUsage DECIMAL(5,2) NOT NULL DEFAULT 0,
                        MemoryUsage DECIMAL(5,2) NOT NULL DEFAULT 0,
                        Status NVARCHAR(50) NOT NULL,
                        RecordedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT FK_HostMetrics_HostRegistry FOREIGN KEY (HostId)
                            REFERENCES dbo.HostRegistry (HostId)
                    );
                    CREATE INDEX IX_HostMetrics_HostId_RecordedAt
                        ON dbo.HostMetrics (HostId, RecordedAt DESC);
                END;
                """, transaction: transaction);
            await transaction.CommitAsync();
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static string[] ParseTags(string tagsString)
    {
        if (string.IsNullOrWhiteSpace(tagsString))
            return Array.Empty<string>();

        return tagsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToArray();
    }

    /// <summary>
    /// Database row mapping for host registration
    /// </summary>
    private class HostRegistrationRow
    {
        public string HostId { get; set; } = "";
        public string EndpointUrl { get; set; } = "";
        public string Tags { get; set; } = "";
        public int Weight { get; set; }
        public int MaxConcurrentWorkflows { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Database row mapping for host metrics
    /// </summary>
    private class HostMetricRow
    {
        public string HostId { get; set; } = "";
        public int CurrentWorkflowCount { get; set; }
        public int MaxWorkflowCount { get; set; }
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public string Status { get; set; } = "";
        public DateTime RecordedAt { get; set; }
        public string EndpointUrl { get; set; } = "";
        public string Tags { get; set; } = "";
        public int Weight { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsActive { get; set; }
    }
}
