using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Templates;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Authoring;

/// <summary>Only immutable content, rendered text and portable diagnostics leave the scratch thread.</summary>
internal static class TemplateBuild
{
    internal sealed record Result(
        TemplateVersion? Version,
        RenderedTemplate? Rendered,
        IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics);

    internal static async Task<Result> RunAsync(TemplateDocument document, WorkflowCompilationResult compilation,
        ResolvedWorkflowPackages packages, IReadOnlyDictionary<string, byte[]> implementations, ITemplateEngine engine,
        byte[]? model, string? culture, WorkflowPublisherOptions options, ILogger logger, CancellationToken token)
    {
        var profile = WorkflowSharedAssemblies.Current;
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                using var shared = WorkflowSharedAssemblies.Enter(profile);
                completion.TrySetResult(Inspect(document, compilation, packages, implementations, engine, model, culture, options, token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception error)
            {
                try
                {
                    logger.LogError(error, "Template scratch inspection failed");
                }
                catch (Exception)
                {
                }

                completion.TrySetResult(new(null, null, [
                    WorkflowPublishFailures.Classify(error, WorkflowPublishStage.Test,
                            DocumentModel.GetLocation(document, "template.model")) with
                        {
                            Path = "template.yaml"
                        }
                ]));
            }
        }) { IsBackground = true, Name = "IxIFlow template preview and validation" };

        using (ExecutionContextFlow.SuppressIfNeeded())
            thread.Start();

        try
        {
            return await completion.Task.WaitAsync(options.BuildTimeout, token);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Template work exceeded {Timeout}; thread {ThreadId} and its scratch context are abandoned",
                options.BuildTimeout, thread.ManagedThreadId);

            return new(null, null,
                [Diagnostic("PUB043", "Template validation or preview exceeded the configured timeout", document, "template.model")]);
        }
    }

    private static WorkflowPublishDiagnostic Diagnostic(string code, string message, TemplateDocument document, string path,
        int? line = null, int? column = null) => WorkflowPublishFailures.Diagnostic(code, message, WorkflowPublishFailures.ClassFor(code),
            WorkflowPublishStage.Test, DocumentModel.GetTextLocation(document, path, line, column)) with
        {
            Path = "template.yaml"
        };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result Inspect(TemplateDocument document, WorkflowCompilationResult compilation, ResolvedWorkflowPackages packages,
        IReadOnlyDictionary<string, byte[]> implementations, ITemplateEngine engine, byte[]? model, string? culture,
        WorkflowPublisherOptions options, CancellationToken token)
    {
        var context = new PackageScratchContext(implementations, packages.RuntimeSymbols);

        Result Failure(string code, string message, string path = "template.model") =>
            new(null, null, [Diagnostic(code, message, document, path)]);

        try
        {
            using var scope = WorkflowTypeIdentity.Enter(context);
            using var dll = new MemoryStream(compilation.Assembly, writable: false);
            var probe = context.LoadFromStream(dll).GetType("TemplateModelProbe", throwOnError: true)!;
            Type type;

            try
            {
                type = (Type)probe.GetProperty("Model")!.GetValue(null)!;
                TemplateModelTypes.Validate(type);
            }
            catch (Exception error) when (error is ArgumentException or TargetInvocationException or TypeLoadException)
            {
                return Failure("TPL005", "The declared model is not a supported, available closed object type");
            }

            var closure = new Dictionary<string, WorkflowPackageReference>(StringComparer.Ordinal);

            void Visit(Type item)
            {
                if (packages.Owners.TryGetValue(item.Assembly.GetName().Name!, out var owner))
                {
                    var prepared = packages.Prepared.Single(package => package.Candidate.Package.Id == owner.Id);
                    var package = prepared.Candidate.Package;
                    closure[package.Id] = new(package.Id, package.Version, package.Digest);

                    foreach (var pin in prepared.Activities.Closure!)
                        closure[pin.Id] = pin;
                }

                foreach (var argument in item.GenericTypeArguments)
                    Visit(argument);
            }

            Visit(type);
            var header = document.Template;

            var version = new TemplateVersion(header.Key, header.Version, header.Engine,
                header.Format == "html" ? TemplateFormat.Html : TemplateFormat.Text,
                TemplateVersion.ModelTypeName(type), WorkflowTypeIdentity.SavedName(type), closure.Values.ToArray(), header.DisplayName,
                header.Description,
                [new("", document.Content), .. document.Cultures.Select(pair => new TemplateVariant(pair.Key, pair.Value))]);

            var diagnostics = new List<WorkflowPublishDiagnostic>();

            foreach (var variant in version.Variants)
            {
                token.ThrowIfCancellationRequested();

                var path = variant.Culture.Length == 0
                    ? "content"
                    : "cultures." + document.Cultures.Keys.Single(key => TemplateVariant.CultureNamed(key).Name == variant.Culture);

                foreach (var diagnostic in engine.ValidateAsync(TemplateSource.Of(version, variant), type, token).AsTask().GetAwaiter()
                             .GetResult())
                    diagnostics.Add(Diagnostic("TPL006", diagnostic.Message, document, path, diagnostic.Line, diagnostic.Column));
            }

            if (diagnostics.Count > 0)
                return new(null, null, diagnostics);

            if (model == null)
                return new(version, null, []);

            object value;

            try
            {
                value = JsonSerializer.Deserialize(model, type, WorkflowJson.Options(type: type)) ?? throw new JsonException("Null model");
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                return Failure("TPL021", "Preview model JSON could not be constructed as the declared type");
            }

            var formatting = string.IsNullOrEmpty(culture) ? CultureInfo.InvariantCulture : TemplateVariant.CultureNamed(culture);
            var selected = version.Resolve(formatting);
            string content;

            try
            {
                content = engine.RenderAsync(TemplateSource.Of(version, selected), value, type, formatting, token).AsTask().GetAwaiter()
                    .GetResult();
            }
            catch (TemplateRenderException error) when (error.InnerException is not WorkflowEngineException
                                                        {
                                                            Kind: WorkflowErrorKind.ResourceExhausted
                                                        })
            {
                return Failure("TPL006", error.Message, selected.Culture.Length == 0 ? "content" : "cultures." + selected.Culture);
            }

            if (Encoding.UTF8.GetByteCount(content) > options.MaxPreviewOutputBytes)
                return Failure("TPL018", "Preview output exceeds the configured byte limit", "content");

            return new(version, new(version.Key, version.Version, selected.Culture, version.Format, content), []);
        }
        finally
        {
            WorkflowJson.Release(context);
            context.Unload();
        }
    }
}