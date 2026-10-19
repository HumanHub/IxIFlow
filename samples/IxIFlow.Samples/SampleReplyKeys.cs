using System.Security.Cryptography;
using IxIFlow.Core;

namespace IxIFlow.Samples;

// These executable samples use an ephemeral ring. Deployed hosts load a persistent shared ring
// from their secret manager; never generate a new key at each durable-store host startup.
internal static class SampleReplyKeys
{
    public static ReplyTokenKeyRing Ring { get; } = new(RandomNumberGenerator.GetBytes(32));
}
