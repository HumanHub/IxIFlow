using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Debugging;

public enum DebugOperation { Inspect, PauseAndStep, Edit, Breakpoints, Launch }
public sealed record DebugAuthorization(string Actor, string InstanceId, DebugOperation Operation);

/// <summary>Application policy and authenticated local endpoint. No authorization policy is supplied by default.</summary>
public sealed class DebuggingOptions
{
    public Func<DebugAuthorization, CancellationToken, ValueTask<bool>>? Authorize { get; set; }
    internal string? PipeName { get; private set; }
    internal IPAddress? Address { get; private set; }
    internal int Port { get; private set; }
    internal string? Actor { get; private set; }
    internal byte[]? TokenHash { get; private set; }
    internal string? SessionToken { get; private set; }

    /// <summary>Uses an OS-enforced current-user-only pipe. The OS user is the session's audit actor.</summary>
    public void NamedPipe(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['/', '\\', ':']) >= 0) throw new ArgumentException("Use a pipe name, not a path", nameof(name));
        PipeName = name; Address = null; TokenHash = null; SessionToken = null; Actor = Environment.UserDomainName + "\\" + Environment.UserName;
    }

    /// <summary>Uses a loopback listener with an application-supplied token and audit actor.</summary>
    public void Tcp(IPAddress loopback, int port, string sessionToken, string actor)
    {
        ArgumentNullException.ThrowIfNull(loopback);
        if (!IPAddress.IsLoopback(loopback)) throw new ArgumentException("DAP TCP must bind loopback", nameof(loopback));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (Encoding.UTF8.GetByteCount(sessionToken) < 32) throw new ArgumentException("Use at least 32 bytes of session token", nameof(sessionToken));
        Address = loopback; Port = port; PipeName = null; Actor = actor;
        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));
        SessionToken = sessionToken;
    }

    internal DebugEndpoint Snapshot()
    {
        if (Authorize == null) throw new InvalidOperationException("AddDebugging requires an application authorizer");
        if (Actor == null || PipeName == null && Address == null) throw new InvalidOperationException("Configure a named pipe or loopback TCP endpoint");
        return new(PipeName, Address, Port, Actor, TokenHash?.ToArray(), SessionToken, Authorize);
    }
}

internal sealed record DebugEndpoint(string? PipeName, IPAddress? Address, int Port, string Actor, byte[]? TokenHash, string? SessionToken,
    Func<DebugAuthorization, CancellationToken, ValueTask<bool>> Authorize);
