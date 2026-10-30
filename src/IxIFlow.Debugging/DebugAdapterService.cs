using System.IO.Pipes;
using System.Net.Sockets;
using IxIFlow.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Debugging;

internal sealed class DebugAdapterService(
    IWorkflowOperations operations,
    DebugEndpoint endpoint,
    ILogger<DebugAdapterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = new List<Task>();
        TcpListener? listener = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (endpoint.Address != null && listener == null)
                    {
                        var candidate = new TcpListener(endpoint.Address, endpoint.Port);

                        try
                        {
                            candidate.Start();
                        }
                        catch
                        {
                            candidate.Stop();

                            throw;
                        }

                        listener = candidate;
                        endpoint.Bound?.Invoke((System.Net.IPEndPoint)candidate.LocalEndpoint);
                    }

                    Stream connection;

                    if (listener != null)
                    {
                        var client = await listener.AcceptTcpClientAsync(stoppingToken);

                        try
                        {
                            client.NoDelay = true;
                            connection = client.GetStream();
                        }
                        catch
                        {
                            client.Dispose();

                            throw;
                        }
                    }
                    else
                    {
                        var pipe = new NamedPipeServerStream(endpoint.PipeName!, PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                        try
                        {
                            await pipe.WaitForConnectionAsync(stoppingToken);
                        }
                        catch
                        {
                            pipe.Dispose();

                            throw;
                        }

                        connection = pipe;
                    }

                    sessions.RemoveAll(t => t.IsCompleted);

                    if (sessions.Count >= endpoint.MaxConcurrentSessions)
                        connection.Dispose();
                    else
                        sessions.Add(ServeAsync(connection, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Could not accept an IxIFlow debug connection; retrying");
                    listener?.Stop();
                    listener = null;
                    endpoint.Bound?.Invoke(null);

                    // Bound retries when the endpoint is temporarily unavailable.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener?.Stop();
            endpoint.Bound?.Invoke(null);
            await Task.WhenAll(sessions);
        }
    }

    private async Task ServeAsync(Stream connection, CancellationToken stoppingToken)
    {
        try
        {
            await using (connection)
            {
                using var session = new DebugAdapterSession(operations, endpoint, endpoint.PipeName != null);
                await session.RunAsync(connection, connection, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "An IxIFlow debug connection ended unexpectedly");
        }
    }
}