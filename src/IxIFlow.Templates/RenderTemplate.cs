using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Templates;

/// <summary>
/// Renders a pinned template version against a typed model (H-02). Rendering has no effects: it reads a published
/// version, which never changes, and produces text. A render that a crash interrupted is therefore simply run again
/// on recovery, from the inputs its first attempt saved, and produces the same text. Sending what it rendered is a
/// separate step, such as <see cref="SendTemplateMessage"/>.
/// </summary>
/// <typeparam name="TModel">The model type the template version was written against.</typeparam>
public sealed class RenderTemplate<TModel> : IRecoverableActivity<TemplateRenderRecovery>
{
    /// <summary>The template version to render, pinned by the workflow definition.</summary>
    [ActivityInput, Required, Display(Name = "Template")]
    public TemplateReference Template { get; set; } = null!;

    /// <summary>The model the template renders.</summary>
    [ActivityInput, Required, Display(Name = "Model")]
    public TModel Model { get; set; } = default!;

    /// <summary>The culture to render for, such as <c>de-AT</c>; none for the base variant.</summary>
    [ActivityInput, Display(Name = "Culture")]
    public string? Culture { get; set; }

    /// <summary>The rendered text, with the version and culture variant that produced it.</summary>
    [ActivityOutput]
    public RenderedTemplate? Rendered { get; private set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Template is null)
            throw new InvalidOperationException("RenderTemplate needs a template to render");
        Rendered = await context.Services.GetRequiredService<TemplateRenderer>()
            .RenderAsync(Template, Model, Culture, cancellationToken);
    }

    public TemplateRenderRecovery CaptureRecoveryState(IActivityContext context) => new(Template, Culture);

    /// <summary>A render has no effect to check, so recovery renders again.</summary>
    public Task<ActivityRecoveryResult<TemplateRenderRecovery>> RecoverAsync(TemplateRenderRecovery state,
        IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<ActivityRecoveryResult<TemplateRenderRecovery>>(
            new ActivityRecoveryResult<TemplateRenderRecovery>.Execute());
}

/// <summary>What a <see cref="RenderTemplate{TModel}"/> attempt was about to render, saved before it runs.</summary>
public sealed record TemplateRenderRecovery(TemplateReference? Template, string? Culture);
