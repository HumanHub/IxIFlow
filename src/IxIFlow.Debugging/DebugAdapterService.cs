using System.IO.Pipes;
using System.Net.Sockets;
using IxIFlow.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Debugging;

internal sealed class DebugAdapterService(IWorkflowOperations operations, DebugEndpoint endpoint,
    ILogger<DebugAdapterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = new List<Task>();
        TcpListener? listener = null;
        try
        {
            if (endpoint.Address != null)
            {
                listener = new(endpoint.Address, endpoint.Port);
                listener.Start();
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                Stream connection;
                if (listener != null)
                {
                    var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    client.NoDelay = true;
                    connection = client.GetStream();
                }
                else
                {
                    var pipe = new NamedPipeServerStream(endpoint.PipeName!, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(stoppingToken); }
                    catch { pipe.Dispose(); throw; }
                    connection = pipe;
                }
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(ServeAsync(connection, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listener?.Stop();
            await Task.WhenAll(sessions);
        }
    }

    private async Task ServeAsync(Stream connection, CancellationToken stoppingToken)
    {
        await using (connection)
        {
            try
            {
                using var session = new DebugAdapterSession(operations, endpoint, endpoint.PipeName != null);
                await session.RunAsync(connection, connection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception) { logger.LogWarning("An IxIFlow debug connection ended unexpectedly"); }
        }
    }
}
