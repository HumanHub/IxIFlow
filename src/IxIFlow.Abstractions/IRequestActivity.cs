namespace IxIFlow.Core;

/// <summary>
/// An activity that sends a request the workflow then waits for: an approval email, a payment
/// whose provider calls back, a task for a person. The workflow runs it with
/// <c>.Request&lt;TActivity&gt;(name)</c> and waits with <c>.WaitFor&lt;TReply&gt;(name)</c>. The
/// activity puts <see cref="IActivityContext.Reply"/> into what it sends; only an answer carrying that
/// token can satisfy the wait.
/// </summary>
/// <typeparam name="TReply">The answer the request waits for.</typeparam>
public interface IRequestActivity<TReply> : IAsyncActivity where TReply : class;

/// <summary>
/// Where the answer to a request goes: an opaque token that answers only this request's wait. Holding
/// it is the right to answer, so it goes only to whoever should answer.
/// </summary>
public sealed record ReplyAddress(string Token);
