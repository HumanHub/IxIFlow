using IxIFlow.Core;

// Deliberately public test bytes, shared by independently constructed test hosts.
internal static class TestReplyKeys
{
    public static ReplyTokenKeyRing Ring { get; } = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
}
