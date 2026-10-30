using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>A resource container whose saved public state is configuration and outputs.</summary>
public interface IScopeActivity;

/// <summary>Opens a pass-local resource. Opening must tolerate replay with the same invocation ID.</summary>
public interface IScopeActivity<TValue> : IScopeActivity where TValue : class
{
    /// <summary>Opens or reopens the live resource for this pass. The implementation must tolerate replay with the same invocation ID.</summary>
    /// <param name="opening">Whether this is a reopen and how many opens have already been recorded.</param>
    /// <param name="context">Lifecycle services, identities and enclosing scopes.</param>
    /// <param name="cancellationToken">Signals that opening should stop.</param>
    /// <returns>The live resource, which is never serialized into workflow state.</returns>
    Task<TValue> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken);

    /// <summary>Releases the pass-local resource when the body completes, fails, is cancelled or suspends.</summary>
    /// <param name="value">The resource returned by opening this scope.</param>
    /// <param name="closing">The close reason and associated failure, if any.</param>
    /// <param name="context">Lifecycle services, identities and enclosing scopes.</param>
    /// <param name="cancellationToken">Signals that bounded cleanup should stop.</param>
    Task CloseAsync(TValue value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken);
}

public sealed record ScopeOpening(bool Reopen, int RecordedOpens);

public sealed record ScopeClosing(ScopeCloseReason Reason, Exception? Error);

public enum ScopeCloseReason
{
    Completed,
    Failed,
    Cancelled,
    Suspended
}

public enum ScopeConcurrency
{
    Serialized,
    Shared
}

public enum ScopeThreading
{
    AnyThread,
    ThreadAffine
}

/// <summary>Lifecycle context. Workflow data and previous values are deliberately unavailable.</summary>
public interface IScopeContext
{
    IServiceProvider Services { get; }
    string WorkflowInstanceId { get; }
    string CorrelationId { get; }
    string WorkflowName { get; }
    int WorkflowVersion { get; }
    string InvocationId { get; }
    ILogger Logger { get; }
    TValue Scope<TValue>() where TValue : class;
    TValue? ScopeOrDefault<TValue>() where TValue : class;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiresScopeAttribute<TValue> : Attribute where TValue : class;

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ScopeConcurrencyAttribute(ScopeConcurrency mode) : Attribute
{
    public ScopeConcurrency Mode { get; } = mode;
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ScopeThreadingAttribute(ScopeThreading mode) : Attribute
{
    public ScopeThreading Mode { get; } = mode;
}