using System.Net;
using System.Security.Claims;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Samples;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "F-09"), Trait("Spec", "F-14")]
public sealed class PublishingHttpSampleTests
{
    [Fact]
    public async Task MappedPublishingRequiresPermissionsScopeAndDraftPreconditions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        PublishingHttpExamples.Register(builder.Services);
        await using var app = builder.Build();

        app.Use((http, next) =>
        {
            var permissions = http.Request.Headers["Sample-Permissions"].ToString().Split(',');

            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                permissions.Select(permission => new Claim("publishing", permission)).Append(new Claim(ClaimTypes.Name, "sample-author")),
                "sample"));

            return next(http);
        });

        app.UseAuthorization();
        PublishingHttpExamples.Map(app);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        http.DefaultRequestHeaders.Add("Sample-Permissions", "read");
        const string path = "/ixiflow/publishing/drafts/workflow/SampleDraft/1";
        const string yaml = "workflow: {name: SampleDraft, dataType: IxIFlow.Samples.PublishedNumber}\ndefinitions: []";

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Put, path)
            { Content = new StringContent(yaml, Encoding.UTF8, "application/yaml") };

        deniedRequest.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var denied = await http.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        http.DefaultRequestHeaders.Remove("Sample-Permissions");
        http.DefaultRequestHeaders.Add("Sample-Permissions", "read,author,download");
        using var missing = await http.PutAsync(path, new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
            { Content = new StringContent(yaml, Encoding.UTF8, "application/yaml") };

        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var saved = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.NotNull(saved.Headers.ETag);
        using var source = await http.GetAsync(path + "/source");
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        Assert.Equal(yaml, await source.Content.ReadAsStringAsync());
        var store = app.Services.GetRequiredService<IWorkflowStateRepository>();
        await store.SaveDefinitionAsync(new("OutsideScope", 1, "workflow", yaml.Replace("SampleDraft", "OutsideScope")), null);
        using var hidden = await http.GetAsync("/ixiflow/publishing/drafts/workflow/OutsideScope/1");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.NotNull(await store.GetDefinitionAsync("workflow", "OutsideScope", 1));
        Assert.Equal("sample-author", (await store.GetDefinitionAsync("workflow", "SampleDraft", 1))!.UpdatedBy);
        http.DefaultRequestHeaders.Remove("Sample-Permissions");
        http.DefaultRequestHeaders.Add("Sample-Permissions", "read");
        using var noDownload = await http.GetAsync(path + "/source");
        Assert.Equal(HttpStatusCode.Forbidden, noDownload.StatusCode);
        await app.StopAsync();
    }
}