using System.Globalization;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Templates.Razor;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.TemplateTests.TemplateFixtures;

namespace IxIFlow.Tests.TemplateTests;

/// <summary>
/// The Liquid engine (Fluid) reads only the model's members; the Razor engine compiles with Roslyn against the model.
/// Both validate a template against its model when it is published (spec H-03).
/// </summary>
[Trait("Feature", "templates")]
[Trait("Spec", "H-03")]
public sealed class TemplateEngineTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public async Task LiquidRendersTheModelWithLoopsVariablesAndCulture()
    {
        var engine = new LiquidTemplateEngine();
        var template = Source("""
            {%- assign count = Lines.size -%}
            {%- capture who %}{{ Customer.Name | upcase }}{% endcapture -%}
            {{ who }}: {{ count }} lines, first {{ Lines.first.Sku }}, total {{ Total }}
            {%- for line in Lines %} [{{ forloop.index }} {{ line.Sku }} x{{ line.Quantity }}]{% endfor %} {{ Labels.gift }}
            """);
        var order = SampleOrder();
        order.Labels["gift"] = "wrapped";

        Assert.Empty(await engine.ValidateAsync(template, typeof(Order)));
        var text = await engine.RenderAsync(template, order, German);

        Assert.Equal("ANA: 2 lines, first pen, total 2,5 [1 pen x2] [2 ink x1] wrapped", text);
    }

    [Fact]
    public async Task LiquidEncodesValuesInHtmlTemplatesOnly()
    {
        var engine = new LiquidTemplateEngine();
        var order = SampleOrder();
        order.Customer.Name = "<script>x</script>";

        var html = await engine.RenderAsync(Source("<b>{{ Customer.Name }}</b>", TemplateFormat.Html), order,
            CultureInfo.InvariantCulture);
        var text = await engine.RenderAsync(Source("{{ Customer.Name }}"), order, CultureInfo.InvariantCulture);

        Assert.Equal("<b>&lt;script&gt;x&lt;/script&gt;</b>", html);
        Assert.Equal("<script>x</script>", text);
    }

    [Theory]
    [InlineData("{{ Customer.Nmae }}", "'Customer' has no member 'Nmae'")]
    [InlineData("{{ Missing }}", "'Missing' is not a member of the model or a variable of the template")]
    [InlineData("{% for line in Lines %}{{ line.Price }}{% endfor %}", "'line' has no member 'Price'")]
    [InlineData("{{ Number | shout }}", "There is no filter named 'shout'")]
    [InlineData("{{ Number.Length }}", "'Number' has no member 'Length'")]
    [InlineData("{% for line in Lines %}{{ forloop.parent }}{% endfor %}", "'forloop' has no member 'parent'")]
    [InlineData("{% include 'footer' %}", "cannot include another template")]
    public async Task LiquidPublishingRejectsWhatTheModelDoesNotHave(string content, string expected)
    {
        using var services = Services();

        var error = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            services.PublishAsync<Order>("bad", 1, "liquid", TemplateFormat.Text, Base(content)));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Message.Contains(expected));
    }

    [Fact]
    public async Task LiquidReportsWhereATemplateDoesNotParse()
    {
        var diagnostics = await new LiquidTemplateEngine().ValidateAsync(Source("Hello\n{{ Number"), typeof(Order));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(2, diagnostic.Line);
        Assert.Contains("'}}' was expected", diagnostic.Message);
    }

    [Fact]
    public async Task LiquidCannotReachBeyondTheModel()
    {
        var engine = new LiquidTemplateEngine();
        var order = new DerivedOrder { Number = "A-1", Secret = "s3cret", Extra = new Credentials { Password = "pw" } };

        // A member typed object could hold anything, so publishing reads nothing through it, and a render reads
        // nothing of a type the model does not reach, even from a template that skipped publishing.
        var throughObject = Source("{{ Extra.Password }}");
        Assert.Contains(await engine.ValidateAsync(throughObject, typeof(Order)),
            diagnostic => diagnostic.Message.Contains("an object of unknown type"));
        await Assert.ThrowsAsync<TemplateRenderException>(() =>
            engine.RenderAsync<Order>(throughObject, order, CultureInfo.InvariantCulture).AsTask());
        // A model of a derived type shows only the members of the type the template was written against.
        await Assert.ThrowsAsync<TemplateRenderException>(() =>
            engine.RenderAsync<Order>(Source("{{ Secret }}"), order, CultureInfo.InvariantCulture).AsTask());
        Assert.Equal("A-1", await engine.RenderAsync<Order>(Source("{{ Number }}"), order,
            CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LiquidHasNoClock()
    {
        var engine = new LiquidTemplateEngine();

        var error = await Assert.ThrowsAsync<TemplateRenderException>(() =>
            engine.RenderAsync(Source("{{ 'now' | date: '%Y' }}"), SampleOrder(), CultureInfo.InvariantCulture)
                .AsTask());

        Assert.Contains("has no clock", error.Message);
    }

    [Fact]
    public async Task RazorRendersTheModelInTheCultureAskedFor()
    {
        var engine = new RazorTemplateEngine();
        var template = Source("""
            @foreach (var line in Model.Lines)
            {
            <text>@line.Sku x@(line.Quantity);</text>
            }
            Total @Model.Total, formatted @Model.Total.ToString("N2"), for @Model.Customer.Name
            """);

        Assert.Empty(await engine.ValidateAsync(template, typeof(Order)));
        var text = await engine.RenderAsync(template, SampleOrder(), German);

        Assert.Equal("pen x2;ink x1;Total 2,5, formatted 2,50, for Ana", Normalize(text));
    }

    [Fact]
    public async Task RazorEncodesValuesInHtmlTemplatesUnlessWrittenRaw()
    {
        var engine = new RazorTemplateEngine();
        var order = SampleOrder();
        order.Customer.Name = "<i>Ana</i>";

        var html = await engine.RenderAsync(Source(
            "<a href=\"mailto:@Model.Customer.Email\" title=\"@Model.Customer.Name\">@Model.Customer.Name</a>@Raw(\"<hr>\")",
            TemplateFormat.Html), order, CultureInfo.InvariantCulture);
        var text = await engine.RenderAsync(Source("@Model.Customer.Name"), order, CultureInfo.InvariantCulture);

        Assert.Equal("<a href=\"mailto:ana@example.com\" title=\"&lt;i&gt;Ana&lt;/i&gt;\">&lt;i&gt;Ana&lt;/i&gt;</a><hr>",
            html);
        Assert.Equal("<i>Ana</i>", text);
    }

    [Fact]
    public async Task RazorPublishingRejectsATemplateThatDoesNotCompileAgainstTheModel()
    {
        using var services = Services();

        var error = await Assert.ThrowsAsync<TemplateValidationException>(() =>
            services.PublishAsync<Order>("bad", 1, "razor", TemplateFormat.Text,
                Base("Hello\n@Model.Customer.Nmae"), In("de", "Hallo @Model.Customer.Name")));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Contains("CS1061", diagnostic.Message);
        Assert.Contains("Nmae", diagnostic.Message);
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal("", error.Template.Culture);
    }

    [Fact]
    public async Task RazorCompilesAgainstGenericAndNestedModelTypes()
    {
        var engine = new RazorTemplateEngine();
        var template = Source("@Model.Count: @Model[0].Sku");
        var lines = new List<OrderLine> { new() { Sku = "pen" } };

        Assert.Empty(await engine.ValidateAsync(template, typeof(List<OrderLine>)));
        Assert.Equal("1: pen", await engine.RenderAsync(template, lines, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EnginesAreRegisteredByName()
    {
        using var services = Services();

        var engines = services.GetRequiredService<TemplateEngines>();

        Assert.IsType<LiquidTemplateEngine>(engines.Get("liquid"));
        Assert.IsType<RazorTemplateEngine>(engines.Get("Razor"));
        Assert.Throws<TemplateNotFoundException>(() => engines.Get("handlebars"));
    }

    private static TemplateSource Source(string content, TemplateFormat format = TemplateFormat.Text)
    {
        var variant = Base(content);
        return new TemplateSource("test", 1, variant.Culture, format, content, variant.Digest);
    }

    private static string Normalize(string text) =>
        string.Join("", text.Split('\n').Select(line => line.Trim()));

    public sealed class Credentials
    {
        public string Password { get; set; } = "";
    }

    public sealed class DerivedOrder : Order
    {
        public string Secret { get; set; } = "";
    }
}
