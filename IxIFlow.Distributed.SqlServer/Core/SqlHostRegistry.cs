using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Dapper;

namespace IxIFlow.Core;

/// <summary>
/// SQL Server-based host registry implementation for distributed workflow communication
/// Manages host registration, discovery, and health tracking with SQL persistence
/// </summary>
public class SqlHostRegistry : IHostRegistry
{
    private readonly string _connectionString;

    public SqlHostRegistry(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task RegisterHostAsync(string hostId, string endpointUrl, HostCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));
        if (string.IsNullOrWhiteSpace(endpointUrl)) throw new ArgumentException("EndpointUrl cannot be empty", nameof(endpointUrl));
        if (capabilities == null) throw new ArgumentNullException(nameof(capabilities));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            UPDATE HostRegistry 
            SET IsActive = 0, UpdatedAt = GETUTCDATE(), LastHeartbeat = GETUTCDATE()
            WHERE HostId = @HostId",
            new { HostId = hostId });
    }

    public async Task<HostRegistration[]> GetRegisteredHostsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
            UPDATE HostRegistry 
            SET LastHeartbeat = GETUTCDATE(), IsActive = 1, UpdatedAt = GETUTCDATE()
            WHERE HostId = @HostId",
            new { HostId = hostId });
    }

    public async Task<HostRegistration[]> GetActiveHostsAsync(TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromMinutes(5));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string sql = @"
            SELECT HostId, EndpointUrl, Tags, Weight, MaxConcurrentWorkflows, LastHeartbeat, IsActive
            FROM HostRegistry 
            WHERE IsActive = 1";

        var parameters = new DynamicParameters();

        // Add required tags filter
        if (requiredTags.Length > 0)
        {
            var tagConditions = new List<string>();
            for (int i = 0; i < requiredTags.Length; i++)
            {
                tagConditions.Add($"Tags LIKE @RequiredTag{i}");
                parameters.Add($"RequiredTag{i}", $"%{requiredTags[i]}%");
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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Update host registry heartbeat
        await UpdateHeartbeatAsync(hostId);

        // Store host metrics
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
            });
    }

    public async Task<HostStatus[]> GetAllHostStatusAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Get latest metrics for each active host
        var metrics = await connection.QueryAsync<HostMetricRow>(@"
            SELECT m.HostId, m.CurrentWorkflowCount, m.MaxWorkflowCount, m.CpuUsage, m.MemoryUsage, m.Status, m.RecordedAt,
                   r.EndpointUrl, r.Tags, r.Weight, r.LastHeartbeat
            FROM HostMetrics m
            INNER JOIN HostRegistry r ON m.HostId = r.HostId
            WHERE r.IsActive = 1
              AND m.RecordedAt = (
                  SELECT MAX(RecordedAt) 
                  FROM HostMetrics 
                  WHERE HostId = m.HostId
              )");

        return metrics.Select(m => new HostStatus
        {
            HostId = m.HostId,
            IsHealthy = m.Status != "Unhealthy",
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
    }
}
