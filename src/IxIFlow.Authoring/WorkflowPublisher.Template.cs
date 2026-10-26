using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Templates;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

public sealed partial class WorkflowPublisher
{
    private sealed record TemplatePreparation(TemplateVersion? Version, RenderedTemplate? Rendered,
        IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics);

    private async Task<WorkflowPublishResult> RunTemplateAsync(WorkflowDraft draft, PackageAcquisition acquisition, string? actor, CancellationToken token)
    {
        var prepared = await PrepareTemplateAsync(draft, acquisition, null, token);
        if (prepared.Version == null || acquisition == PackageAcquisition.Scratch)
            return new(draft.Kind, null, null, null, prepared.Diagnostics);
        try
        {
            token.ThrowIfCancellationRequested();
            var entry = await catalog.PublishTemplateDefinitionAsync(draft.Id, prepared.Version, artifacts, actor);
            return new(draft.Kind, null, null, entry, prepared.Diagnostics);
        }
        catch (TemplateVersionConflictException)
        {
            return Failed(draft.Kind, TemplateDiagnostic("TPL007", "The template publication identity is already occupied", WorkflowPublishStage.Publish,
                DocumentModel.GetLocation(WorkflowYamlSerializer.ReadTemplate(draft.Source), "template.key")));
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(error, "Template publication failed for {Draft}", draft.Id);
            return Failed(draft.Kind, WorkflowPublishFailures.Classify(error) with { Path = "template.yaml" });
        }
    }

    private static WorkflowPublishDiagnostic TemplateDiagnostic(string code, string message, WorkflowPublishStage stage, DocumentLocation location) =>
        WorkflowPublishFailures.Diagnostic(code, message, WorkflowPublishFailures.ClassFor(code), stage, location) with { Path = "template.yaml" };

    private async Task<TemplatePreparation> PrepareTemplateAsync(WorkflowDraft draft, PackageAcquisition acquisition, TemplatePreviewRequest? preview, CancellationToken token)
    {
        var stage = WorkflowPublishStage.Validate;
        var location = new DocumentLocation(1, 1);
        TemplatePreparation Failure(string code, string message) => new(null, null, [TemplateDiagnostic(code, message, stage, location)]);
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.BuildTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaxPreviewModelBytes, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.MaxPreviewOutputBytes, 0);
            token.ThrowIfCancellationRequested();
            try { WorkflowCatalogRules.ValidateText(draft.Source); }
            catch (ArgumentException) { return Failure("IXIYAML001", "The document contains invalid text"); }
            if (WorkflowYamlSerializer.ReadRootKind(draft.Source) != WorkflowDraftKinds.Template)
                return Failure("PUB005", "The document identity does not match the draft identity");
            var document = WorkflowYamlSerializer.ReadTemplate(draft.Source);
            var validation = TemplateDocumentValidator.Validate(document);
            if (!validation.IsValid)
                return new(null, null, validation.Diagnostics.Select(item => TemplateDiagnostic(item.Code, item.Message, stage,
                    DocumentModel.GetLocation(document, item.Path))).ToArray());
            foreach (var member in new[] { ("displayName", document.Template.DisplayName), ("description", document.Template.Description) })
            {
                location = DocumentModel.GetLocation(document, "template." + member.Item1);
                try { WorkflowCatalogRules.ValidateText(member.Item2); }
                catch (ArgumentException) { return Failure("IXIYAML001", "Template metadata contains invalid text"); }
            }
            var identity = WorkflowDocumentKinds.Identify(draft.Source);
            location = identity.Location;
            if ((identity.Kind, identity.Name, identity.Version) != (draft.Kind, draft.Name, draft.Version))
                return Failure("PUB005", "The document identity does not match the draft identity");
            var occupied = await catalog.GetTemplateVersionAsync(draft.Name, draft.Version);
            if (occupied != null)
            {
                if (acquisition == PackageAcquisition.Persist && occupied.PublishedDraft == draft.Id)
                    return new(occupied.Version, null, []);
                location = DocumentModel.GetLocation(document, "template.key");
                return Failure("TPL007", "The template publication identity is already occupied");
            }
            location = DocumentModel.GetLocation(document, "template.engine");
            if (templateEngines == null || templateEngines.Count == 0) return Failure("TPL012", "No template engines are registered");
            ITemplateEngine engine;
            try { engine = templateEngines.Get(document.Template.Engine); }
            catch (TemplateNotFoundException) { return Failure("TPL001", "The declared template engine is not registered"); }
            if (!WorkflowSharedAssemblies.IsUsable) return Failure("PUB008", "This process has no usable shared compatibility profile");

            byte[]? model = null;
            if (preview != null)
            {
                location = DocumentModel.GetLocation(document, "template.model");
                if (preview.Model.ValueKind == JsonValueKind.Undefined) return Failure("TPL021", "Preview requires a JSON model");
                using var buffer = new TemplateModelBuffer(_options.MaxPreviewModelBytes);
                try
                {
                    using (var writer = new Utf8JsonWriter(buffer)) preview.Model.WriteTo(writer);
                    model = buffer.ToArray();
                }
                catch (TemplateModelLimitException) { return Failure("TPL018", "Preview model exceeds the configured byte limit"); }
                if (!string.IsNullOrEmpty(preview.Culture))
                    try { _ = TemplateVariant.CultureNamed(preview.Culture); }
                    catch (ArgumentException) { return Failure("TPL003", "Preview culture is not a known culture"); }
            }
            location = DocumentModel.GetLocation(document, "imports.catalogs");
            using var packages = await new WorkflowPackageResolver(catalog, artifacts, _options)
                .ResolveAsync(document.Imports.Catalogs, acquisition, [], token);
            stage = WorkflowPublishStage.Compile;
            location = DocumentModel.GetLocation(document, "template.model");
            var references = WorkflowCompilation.CreateReferences(new(), packages.References);
            var syntax = SyntaxFactory.ParseTypeName(document.Template.Model);
            if (syntax.ContainsDiagnostics) return Failure("TPL005", "Template model must be a complete C# type expression");
            var source = "public static class TemplateModelProbe { public static global::System.Type Model => typeof(" + syntax + "); }";
            var compilation = WorkflowCompilation.Compile(source, references, "template.yaml", token);
            if (!compilation.Success) return Failure("TPL005", "Template model could not be bound: " +
                string.Join("; ", compilation.Diagnostics.Where(item => item.Severity == Dsl.Compilation.WorkflowDiagnosticSeverity.Error).Select(item => item.Message)));
            var (privateEntries, _) = BundleAssemblies(compilation.Assembly, references.Paths, packages);
            var implementations = packages.RuntimeAssemblies.ToDictionary();
            foreach (var bytes in privateEntries.Values) implementations.TryAdd(ResolvedWorkflowPackages.AssemblyIdentity(bytes).Name!, bytes);
            stage = WorkflowPublishStage.Test;
            var result = await TemplateBuild.RunAsync(document, compilation, packages, implementations, engine, model, preview?.Culture,
                _options, _logger, token);
            return new(result.Version, result.Rendered, result.Diagnostics);
        }
        catch (WorkflowPublishException error)
        {
            if (error.InnerException != null) _logger.LogWarning(error, "Template dependency resolution failed for {Draft}", draft.Id);
            // Resolver positions refer to its import list; keep the template's authored import location.
            return Failure(error.Code, error.Message);
        }
        catch (WorkflowSourceException error) { location = new(error.Line, error.Column); return Failure("IXIYAML001", error.Detail); }
        catch (YamlException error) { location = new((int)error.Start.Line, (int)error.Start.Column); return Failure("IXIYAML001", error.Message); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(error, "Template preparation failed for {Draft}", draft.Id);
            return new(null, null, [WorkflowPublishFailures.Classify(error, stage, location) with { Path = "template.yaml" }]);
        }
    }

    private sealed class TemplateModelLimitException : Exception;
    private sealed class TemplateModelBuffer(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new TemplateModelLimitException();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new TemplateModelLimitException();
            base.Write(buffer);
        }
    }
}
