using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.AspNetCore.WorkflowPublishingHttp;

namespace IxIFlow.AspNetCore;

internal static class WorkflowPublishingTemplates
{
    internal static async Task<IResult> PreviewAsync(HttpContext http, WorkflowPublishingOptions options)
    {
        var name = Route(http, "name");
        var version = Version(http);

        if (!Scope(http).Allows("template", name))
            return Missing();

        var draft = await Store(http).GetDefinitionAsync("template", name, version, cancellationToken: http.RequestAborted);

        if (draft == null)
            return Missing();

        if (await WorkflowHttp.AuthorizeAsync(http, Summary(draft), options.PreviewPolicy!) is { } denied)
            return denied;

        if (!http.Request.HasJsonContentType())
            return Problem(415, "Preview requires application/json");

        var publisherOptions = http.RequestServices.GetRequiredService<WorkflowPublisherOptions>();

        // Bound the envelope before JSON parsing; the publisher separately bounds the serialized model.
        var maximum = (int)Math.Min(int.MaxValue, (long)publisherOptions.MaxPreviewModelBytes + 4096);
        var json = await WorkflowPublishingHttp.SourceAsync(http, maximum);

        var request = JsonSerializer.Deserialize<TemplatePreviewRequest>(json!, WorkflowHttp.JsonOptions)
            ?? throw new ArgumentException("A preview request is required");

        var result = await http.RequestServices.GetRequiredService<WorkflowPublisher>()
            .PreviewTemplateAsync(draft, request, http.RequestAborted);

        var status = result.Diagnostics.Count == 0 ? 200 : Status(result.Diagnostics.MaxBy(item => Status(item.Class))!.Class);

        return Json(new TemplatePreviewResponse(result.Rendered?.Content, result.Rendered?.Format.ToString(),
            result.Diagnostics.Select(PublishingResponses.Project).ToArray()), status);
    }

    internal static async Task<IResult> SourceAsync(HttpContext http, string key, int version)
    {
        // Source authorization grants the complete immutable model closure, including otherwise masked pins.
        var entry = await Store(http).GetTemplateVersionAsync(key, version, cancellationToken: http.RequestAborted);

        if (entry == null)
            return Missing();

        var template = entry.Version;

        var document = new TemplateDocument
        {
            Template = new()
            {
                Key = key, Version = version, Engine = template.Engine,
                Format = template.Format.ToString().ToLowerInvariant(), Model = template.ModelType,
                DisplayName = template.DisplayName, Description = template.Description
            },
            Imports = new() { Catalogs = template.ModelPackages.Select(pin => pin.Id + "@" + pin.Version).ToList() },
            Content = template.Variants.Single(variant => variant.Culture == "").Content,
            Cultures = template.Variants.Where(variant => variant.Culture != "")
                .ToDictionary(variant => variant.Culture, variant => variant.Content)
        };

        // YAML comments preserve the saved identity and exact byte pins beyond the authoring import grammar.
        var source = "# modelTypeId: " + template.ModelTypeId + "\n" +
            string.Concat(template.ModelPackages.Select(pin =>
                "# package: " + pin.Id + "@" + pin.Version + " " + pin.Digest.Value + "\n")) +
            WorkflowYamlSerializer.Write(document);

        var bytes = Encoding.UTF8.GetBytes(source);
        var tag = "\"" + ArtifactDigest.Of(bytes).Value + "\"";
        http.Response.Headers.ETag = tag;
        http.Response.Headers.CacheControl = "private, no-cache";

        return Matches(http, tag) ? Results.StatusCode(304) : Results.Bytes(bytes, "application/yaml");
    }
}