namespace IxIFlow.Core;

internal sealed record InboxPayloads(StoredJson Value, IReadOnlyList<PayloadBody> Bodies,
    IReadOnlyList<PayloadReference> References)
{
    public static PayloadOwner Owner(string id, Guid incarnation) => new("inbox", $"{id}/{incarnation:N}");
    public static InboxPayloads Prepare(WorkflowEvent arrival, PayloadStoreSettings settings)
    {
        var dependencies = arrival.PayloadDependencies.ToArray();
        var value = StoredJson.Inline(arrival.PayloadJson).WithDependencies(dependencies);
        var threshold = arrival.PayloadThresholdBytes ?? settings.PayloadThresholdBytes;
        if (threshold == 0 || value.Bytes <= threshold)
            return new(value, [], dependencies);
        settings.ValidateLength(value.Bytes);
        return new(StoredJson.FromReference(value.Identity),
            [new(value.Identity, value.Encoded.ToArray(), value.Dependencies)], [value.Identity]);
    }
    public async ValueTask PublishAsync(IPayloadTransaction pins, PayloadOwner owner, CancellationToken ct = default)
    {
        foreach (var body in Bodies) await pins.PutAsync(body.Body, body.Dependencies, ct);
        foreach (var reference in References) await pins.PinAsync(owner, reference, ct);
    }
}
