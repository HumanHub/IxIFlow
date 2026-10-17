using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Templates;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.TemplateTests.TemplateFixtures;

namespace IxIFlow.Tests.TemplateTests;

/// <summary>
/// Templates are versioned resources: a definition pins the version a step renders, a version never changes once
/// published, and a culture resolves to its own variant, then its parent's, then the base variant (spec H-01).
/// </summary>
[Trait("Feature", "templates")]
[Trait("Spec", "H-01")]
public sealed class VersionedTemplateTests
{
    [Fact]
    public async Task AnInstanceRendersThePinnedVersionEvenAfterANewerOneIsPublished()
    {
        using var services = Services();
        await services.PublishAsync<Order>("confirmation", 1, "liquid", TemplateFormat.Text,
            Base("v1 {{ Number }}"));
        var definition = Workflow.Create<OrderData>("PinnedTemplate")
            .Step<NoopActivity>()
            .WaitFor<Reply>("go")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference("confirmation", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(definition, new OrderData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await services.PublishAsync<Order>("confirmation", 2, "liquid", TemplateFormat.Text,
            Base("v2 {{ Number }}"));
        var result = await client.ResumeAsync(started.InstanceId, "go", new Reply());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var body = ((OrderData)result.WorkflowData!).Body!;
        Assert.Equal("v1 A-17", body.Content);
        Assert.Equal(1, body.Version);
    }

    [Theory]
    [InlineData("de-AT", "de-AT", "Servus Ana, 2,5")]
    [InlineData("de-CH", "de", "Hallo Ana, 2.5")]
    [InlineData("de", "de", "Hallo Ana, 2,5")]
    [InlineData("fr-FR", "", "Hello Ana, 2,5")]
    [InlineData(null, "", "Hello Ana, 2.5")]
    public async Task ACultureFallsBackToItsParentThenToTheBaseAndFormatsAsAsked(string? culture,
        string variant, string expected)
    {
        using var services = Services();
        await services.PublishAsync<Order>("greeting", 1, "liquid", TemplateFormat.Text,
            Base("Hello {{ Customer.Name }}, {{ Total }}"),
            In("de", "Hallo {{ Customer.Name }}, {{ Total }}"),
            In("de-AT", "Servus {{ Customer.Name }}, {{ Total }}"));

        var rendered = await services.GetRequiredService<TemplateRenderer>()
            .RenderAsync(new TemplateReference("greeting", 1), SampleOrder(), culture);

        Assert.Equal(expected, rendered.Content);
        Assert.Equal(variant, rendered.Culture);
    }

    [Fact]
    public async Task APublishedVersionNeverChanges()
    {
        using var services = Services();
        await services.PublishAsync<Order>("receipt", 1, "liquid", TemplateFormat.Text, Base("Receipt {{ Number }}"));

        // The same content again changes nothing; other content under the same version is refused.
        await services.PublishAsync<Order>("receipt", 1, "liquid", TemplateFormat.Text, Base("Receipt {{ Number }}"));
        await Assert.ThrowsAsync<TemplateVersionConflictException>(() =>
            services.PublishAsync<Order>("receipt", 1, "liquid", TemplateFormat.Text, Base("Changed {{ Number }}")));
        // A culture added to a published version would change what that culture resolves to, so it is refused too.
        await Assert.ThrowsAsync<TemplateVersionConflictException>(() =>
            services.PublishAsync<Order>("receipt", 1, "liquid", TemplateFormat.Text,
                Base("Receipt {{ Number }}"), In("de", "Beleg {{ Number }}")));

        var rendered = await services.GetRequiredService<TemplateRenderer>()
            .RenderAsync(new TemplateReference("receipt", 1), SampleOrder(), "de");
        Assert.Equal("Receipt A-17", rendered.Content);
    }

    [Fact]
    public void AVersionHasABaseVariantAndOneVariantPerCulture()
    {
        var noBase = Assert.Throws<ArgumentException>(() =>
            TemplateVersion.Create<Order>("t", 1, "liquid", TemplateFormat.Text, In("de", "x")));
        Assert.Contains("no base variant", noBase.Message);
        var twice = Assert.Throws<ArgumentException>(() =>
            TemplateVersion.Create<Order>("t", 1, "liquid", TemplateFormat.Text, Base("x"), In("de", "a"),
                In("DE", "b")));
        Assert.Contains("two variants for culture 'de'", twice.Message);
        Assert.Throws<ArgumentException>(() => In("not-a-culture-name", "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateReference("t", 0));
    }

    [Fact]
    public async Task AStepFailsForAnUnpublishedVersionOrAnotherModel()
    {
        using var services = Services();
        await services.PublishAsync<Customer>("customer", 1, "liquid", TemplateFormat.Text, Base("{{ Name }}"));
        var renderer = services.GetRequiredService<TemplateRenderer>();

        var missing = await Assert.ThrowsAsync<TemplateNotFoundException>(() =>
            renderer.RenderAsync(new TemplateReference("customer", 2), new Customer(), null));
        Assert.Contains("customer v2 is not published", missing.Message);
        var otherModel = await Assert.ThrowsAsync<TemplateNotFoundException>(() =>
            renderer.RenderAsync(new TemplateReference("customer", 1), SampleOrder(), null));
        Assert.Contains("written against", otherModel.Message);

        // In a workflow the failure is the step's, and reaches the workflow's fault handling.
        var definition = Workflow.Create<OrderData>("MissingTemplate")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference("customer", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order))
            .Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new OrderData());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("written against", result.ErrorMessage);
    }
}
