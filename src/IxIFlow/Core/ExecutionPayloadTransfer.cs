using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Host-side bounded payload exchange for one assignment. Executors only use messages.</summary>
public sealed class ExecutionPayloadTransfer(IPayloadStore store, ExecutionLimits limits) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PayloadReference, Upload> _uploads = [];
    private readonly Dictionary<PayloadReference, ReadOnlyMemory<byte>> _reads = [];
    private readonly PayloadOwner _reader = new("reader", Guid.NewGuid().ToString("N"));
    private long _bytes;

    public async Task AcceptAsync(PayloadChunk chunk)
    {
        await _gate.WaitAsync();
        try
        {
            if (chunk.Body.Length > PayloadChunk.MaxChunkBytes || chunk.Offset < 0 ||
                chunk.Body.Length > chunk.Reference.Bytes - chunk.Offset)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid payload chunk");
            if (!_uploads.TryGetValue(chunk.Reference, out var upload))
            {
                if (chunk.Offset != 0) throw new InvalidDataException("Payload upload must start at zero");
                Reserve(chunk.Reference.Bytes);
                upload = new(new byte[checked((int)chunk.Reference.Bytes)], chunk.Dependencies.ToArray());
                _uploads.Add(chunk.Reference, upload);
            }
            if (!upload.Dependencies.SequenceEqual(chunk.Dependencies))
                throw new InvalidDataException("Payload dependencies changed during upload");
            // A failed/unknown publication may have filled the buffer. A whole-checkpoint resend
            // restarts that upload and retries publication, even when the prior put actually applied.
            if (chunk.Offset == 0 && upload.Offset == upload.Body.Length) upload.Offset = 0;
            if (chunk.Offset < upload.Offset && chunk.Offset + chunk.Body.Length <= upload.Offset &&
                upload.Body.AsSpan((int)chunk.Offset, chunk.Body.Length).SequenceEqual(chunk.Body)) return;
            if (chunk.Offset != upload.Offset)
                throw new InvalidDataException("Payload upload changed or arrived out of order");
            chunk.Body.CopyTo(upload.Body.AsSpan(upload.Offset));
            upload.Offset += chunk.Body.Length;
            if (upload.Offset == upload.Body.Length)
            {
                // Publication precedes the referencing transaction. Replays publish the same immutable bytes.
                if (upload.Dependencies.Count == 0)
                {
                    var actual = await store.PutAsync(upload.Body);
                    if (actual != chunk.Reference) throw new InvalidDataException("Payload identity differs");
                }
                else
                {
                    chunk.Reference.Verify(upload.Body);
                    var reservation = await store.ReserveAsync(chunk.Reference, TimeSpan.FromMinutes(5));
                    if (!await store.PublishAsync(reservation, upload.Body, upload.Dependencies))
                        throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload publication expired");
                }
                _uploads.Remove(chunk.Reference);
                _bytes -= upload.Body.Length;
            }
        }
        catch (Exception error) when (error is not WorkflowEngineException and not OperationCanceledException and not InvalidDataException)
        { throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload publication failed", error); }
        finally { _gate.Release(); }
    }

    public async Task<ReadPayloadReply> ReadAsync(ReadPayload request)
    {
        await _gate.WaitAsync();
        try
        {
            var reference = request.Reference;
            if (request.Offset < 0 || request.Offset > reference.Bytes)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid payload read offset");
            if (!_reads.TryGetValue(reference, out var body))
            {
                if (request.Offset != 0) throw new InvalidDataException("Payload read must start at zero");
                Reserve(reference.Bytes);
                try
                {
                    try
                    {
                        await store.InTransactionAsync(async (pins, ct) => await pins.PinAsync(_reader, reference, ct));
                    }
                    catch (InvalidOperationException error) when (error.Message == "Payload unavailable or collection already owns its generation")
                    { return new(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable); }
                    try
                    {
                        body = await store.GetAsync(reference) ?? throw new InvalidDataException("Payload missing");
                        reference.Verify(body.Span);
                    }
                    finally
                    {
                        // Get has snapshotted and verified the body; collection cannot affect our copy.
                        await store.InTransactionAsync(async (pins, ct) => await pins.ReleaseAsync(_reader, ct));
                    }
                    _reads.Add(reference, body);
                }
                catch (InvalidDataException)
                { return new(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable); }
                finally { if (!_reads.ContainsKey(reference)) _bytes -= reference.Bytes; }
            }
            var length = (int)Math.Min(PayloadChunk.MaxChunkBytes, reference.Bytes - request.Offset);
            var chunk = body.Slice((int)request.Offset, length).ToArray();
            if (request.Offset + length == reference.Bytes)
            { _reads.Remove(reference); _bytes -= reference.Bytes; }
            return new(request.AssignmentId, chunk);
        }
        catch (Exception error) when (error is not WorkflowEngineException and not OperationCanceledException)
        { throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Payload read failed", error); }
        finally { _gate.Release(); }
    }

    private void Reserve(long bytes)
    {
        if (bytes > limits.MaxPayloadBytes || bytes > int.MaxValue || bytes > limits.MaxHydratedPayloadBytes - _bytes)
            throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Payload transfer byte limit exceeded");
        _bytes += bytes;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _uploads.Clear(); _reads.Clear(); _bytes = 0; }
        finally { _gate.Release(); }
    }
    private sealed class Upload(byte[] body, IReadOnlyList<PayloadReference> dependencies)
    {
        public byte[] Body { get; } = body;
        public IReadOnlyList<PayloadReference> Dependencies { get; } = dependencies;
        public int Offset { get; set; }
    }
}
