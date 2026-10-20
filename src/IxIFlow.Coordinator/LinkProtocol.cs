using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using IxIFlow.Core;

namespace IxIFlow.Coordinator;

// Explicit envelopes keep workflow state, events, and reply capabilities outside the contract.
internal enum HostMessageKind { Hello, Ready, Notify, Await, Unawait, Heartbeat }
internal enum CoordinatorMessageKind { Welcome, Wake, Changed, Awaited, Heartbeat }
internal sealed record HostHello(Guid HostId, string Name, Dictionary<string, string> Capabilities, int Slots,
    int ContractVersion);
internal sealed record InstanceNotice(string InstanceId, bool Claimable, bool Idle,
    WorkflowDemand[] Demands, Guid? LastHostId);
internal sealed record HostMessage(HostMessageKind Kind, HostHello? Hello = null, int FreeSlots = 0,
    InstanceNotice? Notice = null, string? InstanceId = null, long Subscription = 0);
internal sealed record CoordinatorMessage(CoordinatorMessageKind Kind, Guid CoordinatorId = default,
    int ContractVersion = 0, string[]? InstanceIds = null, string? InstanceId = null, long Subscription = 0);

[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(CoordinatorMessage))]
internal partial class LinkJson : JsonSerializerContext;

internal static class LinkProtocol
{
    internal const int Version = 1;
    internal const int MaxMessageBytes = 256 * 1024;
    internal static readonly Method<HostMessage, CoordinatorMessage> Link = new(MethodType.DuplexStreaming,
        "IxIFlow.Coordinator", "Link",
        Marshallers.Create(value => JsonSerializer.SerializeToUtf8Bytes(value, LinkJson.Default.HostMessage),
            bytes => JsonSerializer.Deserialize(bytes, LinkJson.Default.HostMessage) ?? throw Invalid()),
        Marshallers.Create(value => JsonSerializer.SerializeToUtf8Bytes(value, LinkJson.Default.CoordinatorMessage),
            bytes => JsonSerializer.Deserialize(bytes, LinkJson.Default.CoordinatorMessage) ?? throw Invalid()));

    internal static RpcException Invalid(string message = "Invalid coordinator message") =>
        new(new Status(StatusCode.InvalidArgument, message));
}
