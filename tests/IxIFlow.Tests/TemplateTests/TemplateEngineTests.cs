using System.Globalization;
using System.Runtime.Loader;
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

        // A member typed object could hold anything, so publishing reads nothing through it, and a render finds
        // nothing in an object of a type the model does not reach, even from a template that skipped publishing.
        var throughObject = Source("[{{ Extra.Password }}][{{ Extra }}]");
        Assert.Contains(await engine.ValidateAsync(throughObject, typeof(Order)),
            diagnostic => diagnostic.Message.Contains("an object of unknown type"));
        Assert.Equal("[][]", await engine.RenderAsync<Order>(throughObject, order, CultureInfo.InvariantCulture));
        // A model of a derived type shows only the members of the type the template was written against, and an
        // object writes no text of its own, so its ToString shows nothing either.
        Assert.Equal("[A-1][][]", await engine.RenderAsync<Order>(
            Source("[{{ Number }}][{{ Secret }}][{{ Customer }}]"), order, CultureInfo.InvariantCulture));
        // json would serialize the object behind a value, every member of its runtime type included.
        Assert.Contains(await engine.ValidateAsync(Source("{{ Extra | json }}"), typeof(Order)),
            diagnostic => diagnostic.Message.Contains("no filter named 'json'"));
        await Assert.ThrowsAsync<TemplateRenderException>(() =>
            engine.RenderAsync<Order>(Source("{{ Customer | json }}"), order, CultureInfo.InvariantCulture).AsTask());
    }

    [Fact]
    public async Task LiquidRendersNullsAndMissingValuesAsNothing()
    {
        var engine = new LiquidTemplateEngine();
        var template = Source(
            "[{{ Customer.Name }}][{{ Customer.Name | default: 'anon' }}][{{ Labels.missing }}][{{ Lines[5].Sku }}]" +
            "{% if Extra %}extra{% endif %}");
        var order = SampleOrder();
        order.Customer.Name = null!;

        Assert.Empty(await engine.ValidateAsync(template, typeof(Order)));
        Assert.Equal("[][anon][][]", await engine.RenderAsync(template, order, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LiquidWritesDatesAsTheModelHasThemOnAnyHost()
    {
        var engine = new LiquidTemplateEngine();
        var template = Source(
            "{{ Local | date: '%Y-%m-%d %H:%M' }}|{{ Unspecified | date: '%Y-%m-%d %H:%M' }}|" +
            "{{ Utc | date: '%H:%M' }}|{{ Offset | date: '%H:%M %z' }}|{{ Duration }}|" +
            "{{ '2026-03-04' | date: '%d.%m.' }}");
        var schedule = new Schedule
        {
            Local = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
            Unspecified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified),
            Utc = new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc),
            Offset = new DateTimeOffset(2026, 1, 1, 8, 15, 0, TimeSpan.FromHours(2)),
            Duration = TimeSpan.FromMinutes(90)
        };

        Assert.Empty(await engine.ValidateAsync(template, typeof(Schedule)));
        // Neither kind of DateTime is shifted by the host's time zone, and a TimeSpan is a duration, not a date.
        Assert.Equal("2026-01-01 12:00|2026-01-01 12:00|09:30|08:15 +0200|01:30:00|04.03.",
            await engine.RenderAsync(template, schedule, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LiquidHasNoClock()
    {
        var engine = new LiquidTemplateEngine();

        Assert.Contains(await engine.ValidateAsync(Source("{{ 'now' | date: '%Y' }}"), typeof(Order)),
            diagnostic => diagnostic.Message.Contains("has no clock"));
        // A clock word that reaches a date filter from the model is refused when rendered.
        var order = SampleOrder();
        order.Number = "today";
        var error = await Assert.ThrowsAsync<TemplateRenderException>(() =>
            engine.RenderAsync(Source("{{ Number | date: '%Y' }}"), order, CultureInfo.InvariantCulture).AsTask());
        Assert.Contains("has no clock", error.Message);
    }

    [Fact]
    public async Task LiquidCheckingFollowsFluidsValues()
    {
        var engine = new LiquidTemplateEngine();

        // A formattable value is text to Fluid, so it has no members to read.
        Assert.Empty(await engine.ValidateAsync(Source("{{ Price }}"), typeof(Priced)));
        Assert.Contains(await engine.ValidateAsync(Source("{{ Price.Amount }}"), typeof(Priced)),
            diagnostic => diagnostic.Message.Contains("'Price' has no member 'Amount'"));
        Assert.Equal("12.50 EUR", await engine.RenderAsync(Source("{{ Price }}"),
            new Priced { Price = new Money(12.5m, "EUR") }, CultureInfo.InvariantCulture));
        // A template reads its model's members; a collection has none it can name.
        Assert.Contains(await engine.ValidateAsync(Source("{{ Count }}"), typeof(List<OrderLine>)),
            diagnostic => diagnostic.Message.Contains("wrap it in a model type"));
        // A variable assigned values of two types in branches the check cannot tell apart is not checked further.
        Assert.Empty(await engine.ValidateAsync(Source(
            "{% assign x = Customer %}{% if Total > 100 %}{% assign x = Number %}{% endif %}{{ x.Name }}"),
            typeof(Order)));
    }

    [Fact]
    public async Task EnginesKeyWhatTheyParseByTheContent()
    {
        var liquid = new LiquidTemplateEngine();
        var razor = new RazorTemplateEngine();

        // The same key and version with other content is other text, whatever the engine has already seen.
        Assert.Equal("one", await liquid.RenderAsync(Source("one"), SampleOrder(), CultureInfo.InvariantCulture));
        Assert.Equal("two", await liquid.RenderAsync(Source("two"), SampleOrder(), CultureInfo.InvariantCulture));
        Assert.Equal("one", await razor.RenderAsync(Source("one"), SampleOrder(), CultureInfo.InvariantCulture));
        Assert.Equal("two", await razor.RenderAsync(Source("two"), SampleOrder(), CultureInfo.InvariantCulture));
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
    public async Task RazorWritesBooleanAndNullAttributesAsRazorDoes()
    {
        var engine = new RazorTemplateEngine();

        var html = await engine.RenderAsync(Source(
            "<input disabled=\"@(Model.Total > 100)\" checked=\"@(Model.Total > 1)\" title=\"@Model.Extra\" " +
            "class=\"line @Model.Number\" />", TemplateFormat.Html), SampleOrder(), CultureInfo.InvariantCulture);

        Assert.Equal("<input checked=\"checked\" class=\"line A-17\" />", html);
    }

    [Fact]
    public async Task RazorTemplatesDeriveFromTheModelsBase()
    {
        var diagnostics = await new RazorTemplateEngine().ValidateAsync(
            Source("@inherits IxIFlow.Templates.Razor.RazorTemplate<object>\n@Model"), typeof(Order));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("@inherits is not available"));
    }

    [Fact]
    public async Task RazorCreatesTheTemplateInTheCultureAskedFor()
    {
        var text = await new RazorTemplateEngine().RenderAsync(
            Source("@functions { readonly string total = (2.5m).ToString(); }@total"), SampleOrder(), German);

        Assert.Equal("2,5", text.Trim());
    }

    [Fact]
    public async Task RazorValidationLoadsNothing()
    {
        var engine = new RazorTemplateEngine();
        var template = Source($"@Model.Number {Guid.NewGuid():N}");
        var loaded = () =>
            AssemblyLoadContext.All.Any(context => context.Name == "Template_" + template.Digest[..16]);

        Assert.Empty(await engine.ValidateAsync(template, typeof(Order)));
        Assert.False(loaded());
        await engine.RenderAsync(template, SampleOrder(), CultureInfo.InvariantCulture);
        Assert.True(loaded());
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

    private static TemplateSource Source(string content, TemplateFormat format = TemplateFormat.Text) =>
        new("test", 1, TemplateVersion.BaseCulture, format, content);

    private static string Normalize(string text) =>
        string.Join("", text.Split('\n').Select(line => line.Trim()));

    public sealed class Schedule
    {
        public DateTime Local { get; set; }
        public DateTime Unspecified { get; set; }
        public DateTime Utc { get; set; }
        public DateTimeOffset Offset { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public readonly record struct Money(decimal Amount, string Currency) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) =>
            $"{Amount.ToString("0.00", provider)} {Currency}";
    }

    public sealed class Priced
    {
        public Money Price { get; set; }
    }

    public sealed class Credentials
    {
        public string Password { get; set; } = "";
    }

    public sealed class DerivedOrder : Order
    {
        public string Secret { get; set; } = "";
    }
}
