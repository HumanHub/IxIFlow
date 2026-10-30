using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Tests.Conformance;

/// <summary>One-based i/n; SHA-256 of the UTF-8 full class name, first uint32 big endian, modulo n.</summary>
internal sealed record ConformanceShard(int Index, int Count)
{
    internal static ConformanceShard? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split('/');

        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 1 || index < 1 || index > count)
            throw new ArgumentException($"Invalid IXIFLOW_SHARD: {value}. Expected i/n with 1 <= i <= n.");

        return new(index, count);
    }

    internal bool Includes(string fullClassName) =>
        BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(fullClassName))) % Count == Index - 1;
}