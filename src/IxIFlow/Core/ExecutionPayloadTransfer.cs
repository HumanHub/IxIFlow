using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Host-side whole payload exchange. Executors only use messages.</summary>
public sealed class ExecutionPayloadTransfer(IPayloadStore store, ExecutionLimits limits)
{
    public async Task AcceptAsync(IReadOnlyList<PayloadBody> payloads)
    {
        long bytes = 0;
        foreach (var payload in payloads)
        {
            CheckLength(payload.Reference.Bytes);
            if (payload.Reference.Bytes > limits.MaxHydratedPayloadBytes - bytes)
                throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Payload transfer byte limit exceeded");
            bytes += payload.Reference.Bytes;
            payload.Reference.Verify(payload.Body);
        }
        try
        {
            foreach (var payload in payloads)
            {
                // Publication precedes the referencing transaction. Replays publish the same immutable bytes.
                if (payload.Dependencies.Count == 0)
                {
                    var actual = await store.PutAsync(payload.Body);
                    if (actual != payload.Reference) throw new InvalidDataException("Payload identity differs");
                }
                else
                {
                    var reservation = await store.ReserveAsync(payload.Reference, TimeSpan.FromMinutes(5));
                    if (!await store.PublishAsync(reservation, payload.Body, payload.Dependencies))
                        throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload publication expired");
                }
            }
        }
        catch (Exception error) when (error is not WorkflowEngineException and not OperationCanceledException and not InvalidDataException)
        { throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload publication failed", error); }
    }

    public async Task<ReadPayloadReply> ReadAsync(ReadPayload request)
    {
        CheckLength(request.Reference.Bytes);
        var reader = new PayloadOwner("reader", Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                await store.InTransactionAsync(async (pins, ct) => await pins.PinAsync(reader, request.Reference, ct));
            }
            catch (InvalidOperationException error) when (error.Message == "Payload unavailable or collection already owns its generation")
            { return new(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable); }
            try
            {
                var stored = await store.GetAsync(request.Reference) ?? throw new InvalidDataException("Payload missing");
                var body = stored.ToArray();
                request.Reference.Verify(body);
                return new(request.AssignmentId, body);
            }
            finally
            {
                // The verified body is an independent snapshot before the reader's pins are released.
                await store.InTransactionAsync(async (pins, ct) => await pins.ReleaseAsync(reader, ct));
            }
        }
        catch (InvalidDataException)
        { return new(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable); }
        catch (Exception error) when (error is not WorkflowEngineException and not OperationCanceledException)
        { throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload read failed", error); }
    }

    private void CheckLength(long bytes)
    {
        if (bytes > limits.MaxPayloadBytes || bytes > int.MaxValue || bytes > limits.MaxHydratedPayloadBytes)
            throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Payload transfer byte limit exceeded");
    }
}
