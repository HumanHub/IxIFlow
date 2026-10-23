using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

public sealed record CatalogNames(IReadOnlyList<string>? Names = null, IReadOnlyList<string>? Prefixes = null);

public sealed record CatalogScope
{
    public CatalogNames Workflows { get; }
    public CatalogNames Activities { get; }
    public CatalogNames Packages { get; }
    public CatalogNames Templates { get; }
    public static CatalogScope Unrestricted { get; } = new(new(), new(), new(), new());
    public string Hash { get; }

    public CatalogScope(CatalogNames Workflows, CatalogNames Activities, CatalogNames Packages, CatalogNames Templates)
    {
        this.Workflows = Normalize(Workflows, 200, false);
        this.Activities = Normalize(Activities, 100, false);
        this.Packages = Normalize(Packages, 100, true);
        this.Templates = Normalize(Templates, 100, true);
        Hash = CatalogPaging.Hash(new[] { this.Workflows, this.Activities, this.Packages, this.Templates });
    }

    private static CatalogNames Normalize(CatalogNames names, int max, bool lower)
    {
        ArgumentNullException.ThrowIfNull(names);
        IReadOnlyList<string>? List(IReadOnlyList<string>? values)
        {
            if (values == null) return null;
            ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Count, 100);
            return Array.AsReadOnly(values.Select(value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
                WorkflowCatalogRules.ValidateUtf16(value);
                value = value.Trim();
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, max);
                return lower ? value.ToLowerInvariant() : value;
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        // A missing list and an empty list mean the same thing in a restricted scope.
        return names.Names == null && names.Prefixes == null ? new() : new(List(names.Names) ?? [], List(names.Prefixes) ?? []);
    }

    internal CatalogNames For(string kind) => kind switch
    {
        WorkflowDraftKinds.Workflow => Workflows,
        WorkflowDraftKinds.Activity => Activities,
        "package" => Packages,
        WorkflowDraftKinds.Template => Templates,
        _ => throw new ArgumentException("Unknown catalog kind", nameof(kind))
    };

    public bool Allows(string kind, string name)
    {
        var names = For(kind);
        if (kind == "package") name = name.ToLowerInvariant();
        return names.Names == null && names.Prefixes == null ||
            (names.Names?.Contains(name, StringComparer.Ordinal) ?? false) ||
            (names.Prefixes?.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ?? false);
    }
}

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, string? NextCursor, long CatalogRevision);
public sealed record CatalogVersionQuery(CatalogScope Scope, string? After = null, int Limit = 100);

internal static class CatalogPaging
{
    // UTF-8 binary order is lexicographic Unicode scalar order, unlike UTF-16 ordinal order.
    internal static readonly IComparer<string> DraftNames = Comparer<string>.Create(CompareDraftNames);
    internal static int CompareDraftNames(string left, string right)
    {
        var x = left.EnumerateRunes();
        var y = right.EnumerateRunes();
        while (x.MoveNext())
        {
            if (!y.MoveNext()) return 1;
            var compared = x.Current.Value.CompareTo(y.Current.Value);
            if (compared != 0) return compared;
        }
        return y.MoveNext() ? -1 : 0;
    }

    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private sealed record Cursor(int Version, string Filter, string[] Key);
    internal static string Encode(string filter, params string[] key) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(1, filter, key)));
    internal static string[]? Decode(string? after, string filter, int keyCount, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (after == null) return null;
        try
        {
            if (after.Length > 8192) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(after));
            if (cursor is not { Version: 1 } || cursor.Filter != filter || cursor.Key?.Length != keyCount)
                throw new FormatException();
            return cursor.Key;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("Invalid catalog cursor or scope/filter mismatch", nameof(after), error); }
    }
}
