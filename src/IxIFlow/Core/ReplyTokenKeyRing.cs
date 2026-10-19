using System.Security.Cryptography;

namespace IxIFlow.Core;

/// <summary>Immutable signing and verification keys shared by every host of a store.</summary>
public sealed class ReplyTokenKeyRing
{
    private readonly byte[][] _keys;
    private readonly byte[][] _cursorKeys;
    private static ReadOnlySpan<byte> CursorPurpose => "IxIFlow wait pagination v1"u8;

    /// <summary>Each key must contain at least 32 cryptographically random bytes. Keys are copied.</summary>
    public ReplyTokenKeyRing(byte[] currentKey, params byte[][] previousKeys)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        ArgumentNullException.ThrowIfNull(previousKeys);
        if (currentKey.Length < 32 || previousKeys.Any(key => key == null || key.Length < 32))
            throw new ArgumentException("Reply token keys must contain at least 256 bits");
        _keys = new[] { currentKey }.Concat(previousKeys).Select(key => key.ToArray()).ToArray();
        _cursorKeys = _keys.Select(key => HMACSHA256.HashData(key, CursorPurpose)).ToArray();
    }

    internal byte[] Sign(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_keys[0], payload);

    internal bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        // Check every key, including after a match, without revealing its position in the ring.
        var valid = false;
        Span<byte> expected = stackalloc byte[32];
        foreach (var key in _keys)
        {
            HMACSHA256.HashData(key, payload, expected);
            valid |= CryptographicOperations.FixedTimeEquals(expected, signature);
        }
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }

    internal byte[] ProtectCursor(ReadOnlySpan<byte> payload)
    {
        var result = new byte[28 + payload.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var cipher = new AesGcm(_cursorKeys[0], 16);
        cipher.Encrypt(result.AsSpan(0, 12), payload, result.AsSpan(28), result.AsSpan(12, 16), CursorPurpose);
        return result;
    }

    internal bool TryUnprotectCursor(byte[] cursor, out byte[] payload)
    {
        payload = [];
        if (cursor.Length <= 28) return false;
        var clear = new byte[cursor.Length - 28];
        foreach (var key in _cursorKeys)
        {
            using var cipher = new AesGcm(key, 16);
            try
            {
                cipher.Decrypt(cursor.AsSpan(0, 12), cursor.AsSpan(28), cursor.AsSpan(12, 16), clear, CursorPurpose);
                payload = clear;
                return true;
            }
            catch (AuthenticationTagMismatchException) { }
        }
        CryptographicOperations.ZeroMemory(clear);
        return false;
    }
}
