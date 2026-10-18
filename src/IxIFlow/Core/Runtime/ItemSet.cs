using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// A set of item positions, saved as a bitmap in base 64, so a <c>ForEach</c>'s record of finished items takes one bit
/// per item rather than a number. It enumerates in item order.
/// </summary>
[JsonConverter(typeof(ItemSetJson))]
internal sealed class ItemSet : IEnumerable<int>
{
    private byte[] _bits = [];

    public int Count { get; private set; }

    /// <summary>Adds <paramref name="index"/>; false when it was there already.</summary>
    public bool Add(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index / 8 >= _bits.Length)
            Array.Resize(ref _bits, Math.Max(index / 8 + 1, _bits.Length * 2));
        var mask = (byte)(1 << index % 8);
        if ((_bits[index / 8] & mask) != 0)
            return false;
        _bits[index / 8] |= mask;
        Count++;
        return true;
    }

    public bool Contains(int index) =>
        index >= 0 && index / 8 < _bits.Length && (_bits[index / 8] & 1 << index % 8) != 0;

    /// <summary>How many items a stored chunk of the set holds.</summary>
    public const int ChunkItems = 512;

    private const int ChunkBytes = ChunkItems / 8;

    /// <summary>
    /// The set in chunks of <see cref="ChunkItems"/> items, each a bitmap in base 64 without trailing empty bytes;
    /// chunks with no item are left out. A finished item changes one chunk, whatever the count.
    /// </summary>
    public IEnumerable<(int Chunk, string Bits)> Chunks()
    {
        for (var chunk = 0; chunk * ChunkBytes < _bits.Length; chunk++)
        {
            var bits = _bits.AsSpan(chunk * ChunkBytes, Math.Min(ChunkBytes, _bits.Length - chunk * ChunkBytes));
            var length = bits.Length;
            while (length > 0 && bits[length - 1] == 0)
                length--;
            if (length > 0)
                yield return (chunk, Convert.ToBase64String(bits[..length]));
        }
    }

    /// <summary>Adds the items of a chunk <see cref="Chunks"/> gave.</summary>
    public void AddChunk(int chunk, string bits)
    {
        var bytes = Convert.FromBase64String(bits);
        for (var index = 0; index < bytes.Length * 8; index++)
            if ((bytes[index / 8] & 1 << index % 8) != 0)
                Add(chunk * ChunkItems + index);
    }

    public IEnumerator<int> GetEnumerator()
    {
        for (var index = 0; index < _bits.Length * 8; index++)
            if (Contains(index))
                yield return index;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ItemSetJson : JsonConverter<ItemSet>
    {
        public override ItemSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var bits = reader.GetBytesFromBase64();
            var set = new ItemSet { _bits = bits };
            foreach (var value in bits)
                set.Count += System.Numerics.BitOperations.PopCount(value);
            return set;
        }

        // Trailing empty bytes are not written, so equal sets are written alike.
        public override void Write(Utf8JsonWriter writer, ItemSet value, JsonSerializerOptions options)
        {
            var length = value._bits.Length;
            while (length > 0 && value._bits[length - 1] == 0)
                length--;
            writer.WriteBase64StringValue(value._bits.AsSpan(0, length));
        }
    }
}
