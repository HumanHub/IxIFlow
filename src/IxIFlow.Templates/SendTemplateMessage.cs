using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Templates;

/// <summary>
/// Sends a rendered template through the application's <see cref="ITemplateMessageSender"/> (H-02). The message ID is
/// the step's invocation ID: the same for every attempt of the step, a retry or a recovery after a crash included, and
/// different for every other step, loop iteration or branch. The sender uses it as its idempotency key, so a send that
/// a crash interrupted is sent again on recovery and delivered once.
/// </summary>
public sealed class SendTemplateMessage : IRecoverableActivity<TemplateMessageRecovery>
{
    /// <summary>The channel the application sends through, such as <c>email</c> or <c>sms</c>.</summary>
    [WorkflowInput(Label = "Channel", Required = true)]
    public string Channel { get; set; } = "";

    /// <summary>Who receives the message, in the channel's own addressing.</summary>
    [WorkflowInput(Label = "Recipients", Required = true)]
    public List<string> Recipients { get; set; } = [];

    /// <summary>The subject, for channels that have one.</summary>
    [WorkflowInput(Label = "Subject")]
    public string? Subject { get; set; }

    /// <summary>The body, as <see cref="RenderTemplate{TModel}"/> rendered it.</summary>
    [WorkflowInput(Label = "Body", Required = true)]
    public RenderedTemplate Body { get; set; } = null!;

    /// <summary>The ID the message was sent with.</summary>
    [WorkflowOutput]
    public string? MessageId { get; private set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(Channel))
            throw new InvalidOperationException("SendTemplateMessage needs a channel");
        if (Recipients is not { Count: > 0 })
            throw new InvalidOperationException("SendTemplateMessage needs at least one recipient");
        if (Body is null)
            throw new InvalidOperationException("SendTemplateMessage needs a rendered body");
        var message = new TemplateMessage(context.InvocationId, Channel, Recipients.ToList(), Subject, Body);
        await context.Services.GetRequiredService<ITemplateMessageSender>().SendAsync(message, cancellationToken);
        MessageId = message.MessageId;
    }

    public TemplateMessageRecovery CaptureRecoveryState(IActivityContext context) => new(context.InvocationId);

    /// <summary>
    /// The sender deduplicates by message ID, so recovery sends again with the same ID rather than guessing whether
    /// the interrupted attempt's message went out.
    /// </summary>
    public Task<ActivityRecoveryResult<TemplateMessageRecovery>> RecoverAsync(TemplateMessageRecovery state,
        IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<ActivityRecoveryResult<TemplateMessageRecovery>>(
            new ActivityRecoveryResult<TemplateMessageRecovery>.Execute());
}

/// <summary>The message ID a <see cref="SendTemplateMessage"/> attempt sends with, saved before it runs.</summary>
public sealed record TemplateMessageRecovery(string MessageId);

/// <summary>A message for <see cref="ITemplateMessageSender"/> to deliver.</summary>
/// <param name="MessageId">The idempotency key: a message with an ID already delivered must not be delivered again.</param>
/// <param name="Channel">The channel to send through.</param>
/// <param name="Recipients">Who receives it.</param>
/// <param name="Subject">The subject, for channels that have one.</param>
/// <param name="Body">The rendered body.</param>
public sealed record TemplateMessage(
    string MessageId,
    string Channel,
    IReadOnlyList<string> Recipients,
    string? Subject,
    RenderedTemplate Body);

/// <summary>
/// Delivers messages for <see cref="SendTemplateMessage"/>; the application provides it, for email, SMS or any other
/// channel. It must deliver a message at most once per <see cref="TemplateMessage.MessageId"/>, using the ID as its
/// provider's idempotency key or recording the IDs it delivered, because a step that a crash interrupted sends the
/// same message again.
/// </summary>
public interface ITemplateMessageSender
{
    Task SendAsync(TemplateMessage message, CancellationToken cancellationToken = default);
}
