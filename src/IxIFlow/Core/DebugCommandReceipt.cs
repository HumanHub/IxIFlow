namespace IxIFlow.Core;

// Identifies the durable command, rather than whichever work happens to be runnable later.
internal sealed record DebugCommandReceipt(string InstanceId, Guid Incarnation, string CommandId, long Order, DebugCommandKind Kind);
