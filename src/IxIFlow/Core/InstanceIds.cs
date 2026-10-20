using System.Runtime.CompilerServices;

namespace IxIFlow.Core;

// Shared portable limit: SQL Server's instance/lease keys are nvarchar(100),
// PostgreSQL's are varchar(100). Count UTF-16 code units to fit both providers.
internal static class InstanceIds
{
    internal const int MaxLength = 100;

    internal static void Validate(string id, [CallerArgumentExpression(nameof(id))] string? parameter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, parameter);
        if (id.Length > MaxLength)
            throw new ArgumentException("Instance IDs must be at most 100 UTF-16 code units", parameter);
    }

    internal static void ValidateAll(IEnumerable<string> ids)
    {
        foreach (var id in ids) Validate(id);
    }
}
