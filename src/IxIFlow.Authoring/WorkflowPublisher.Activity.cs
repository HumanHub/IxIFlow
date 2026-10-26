using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

public sealed partial class WorkflowPublisher
{
    private async Task<WorkflowPublishResult> RunActivityAsync(WorkflowDraft draft, PackageAcquisition acquisition, string? actor, CancellationToken token)
    {
        const string sourcePath = ActivityCompilation.SourceResourceName;
        var diagnostics = new List<WorkflowPublishDiagnostic>();
        var stage = WorkflowPublishStage.Validate;
        var location = new DocumentLocation(1, 1);
        WorkflowPublishResult Failure(string code, string message, WorkflowPublishDiagnosticClass? @class = null) =>
            new(draft.Kind, null, null, null, [.. diagnostics, WorkflowPublishFailures.Diagnostic(code, message,
                @class ?? WorkflowPublishFailures.ClassFor(code), stage, location) with { Path = sourcePath }]);
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.BuildTimeout, TimeSpan.Zero);
            token.ThrowIfCancellationRequested();
            try { WorkflowCatalogRules.ValidateText(draft.Source); }
            catch (ArgumentException) { return Failure("IXIYAML001", "The document contains invalid text"); }
            var identity = WorkflowDocumentKinds.Identify(draft.Source);
            location = identity.Location;
            if ((identity.Kind, identity.Name, identity.Version) != (draft.Kind, draft.Name, draft.Version))
                return Failure("PUB005", "The document identity does not match the draft identity");
            var document = WorkflowYamlSerializer.ReadActivity(draft.Source);
            var packageIdentity = new ActivityPackageIdentity(draft.Name, draft.Version);
            var occupiedIdentity = await catalog.GetPackageAsync(packageIdentity.PackageId, packageIdentity.PackageVersion);
            if (acquisition == PackageAcquisition.Persist && occupiedIdentity?.PublishedDraft == draft.Id)
                return new(draft.Kind, null, occupiedIdentity, null, []);
            if (occupiedIdentity != null)
                return Failure("PUB022", "The package publication identity is already occupied");
            var validation = ActivityDocumentValidator.Validate(document, draft.Source);
            diagnostics.AddRange(validation.Diagnostics.Select(item =>
            {
                var at = DocumentModel.GetLocation(document, item.Path);
                return new WorkflowPublishDiagnostic(stage, item.Code, item.Message, item.Severity, sourcePath, at.Line, at.Column);
            }));
            if (!validation.IsValid) return new(draft.Kind, null, null, null, diagnostics);
            if (_options.TargetFramework != "net10.0") return Failure("PUB020", "Activity publication requires net10.0");
            if (!WorkflowSharedAssemblies.IsUsable) return Failure("PUB008", "This process has no usable shared compatibility profile");
            using var packages = await new WorkflowPackageResolver(catalog, artifacts, _options).ResolveAsync(document.Packages, acquisition, [], token);
            stage = WorkflowPublishStage.Compile;
            var compileOptions = new ActivityCompilationOptions { PackageReferencePaths = packages.References,
                SharedContractAssemblies = _options.SharedProfile.Contracts };
            var compilation = ActivityCompilation.Compile(draft.Source, sourcePath, compileOptions, token);
            diagnostics.AddRange(compilation.Diagnostics);
            if (!compilation.Success) return new(draft.Kind, null, null, null, diagnostics);
            stage = WorkflowPublishStage.Test;
            var descriptor = await ActivityBuild.RunAsync(compilation, packageIdentity, packages, _options.BuildTimeout, _logger, token);
            var (privateEntries, shared) = BundleAssemblies(compilation.Assembly, ActivityCompilation.RuntimeReferencePaths(compileOptions), packages);
            if (privateEntries.Count != 0) return Failure("PUB020", "Activity references must belong to prepared packages or the declared shared profile");
            var candidate = await ActivityPackageConstruction.CreateAsync(draft, packageIdentity, compilation, packages, shared, _options, token);
            if (acquisition == PackageAcquisition.Scratch) return new(draft.Kind, null, null, null, diagnostics);
            stage = WorkflowPublishStage.Publish;
            await using var content = new MemoryStream(candidate.Bytes.ToArray(), writable: false);
            var digest = await artifacts.PutAsync(ArtifactKind.Package, content, token);
            if (digest != candidate.Package.Digest) return Failure("PUB024", "The artifact store returned a different package digest");
            token.ThrowIfCancellationRequested();
            var closure = packages.Pins.OrderBy(pin => pin.Id, StringComparer.Ordinal)
                .Select(pin => new WorkflowPackageReference(pin.Id, pin.Version, pin.Digest)).ToArray();
            var registration = await catalog.PublishPackageDefinitionAsync(draft.Id,
                candidate.Package with { Origin = WorkflowPackageOrigin.Published, PublishedDraft = draft.Id },
                new([descriptor], PackageExtraction.Indexed, null, closure, []), artifacts, actor);
            return new(draft.Kind, null, registration.Stored, null, diagnostics);
        }
        catch (WorkflowPublishException error)
        {
            location = error.Location ?? location;
            return Failure(error.Code == "PUB003" ? "PUB020" : error.Code, error.Message);
        }
        catch (PackageClosureUnavailableException) { return Failure("PUB020", "The activity package dependency closure is unavailable"); }
        catch (WorkflowSourceException error)
        { location = new(error.Line, error.Column); return Failure("IXIYAML001", error.Detail); }
        catch (YamlException error)
        { location = new((int)error.Start.Line, (int)error.Start.Column); return Failure("IXIYAML001", error.Message); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            if (error is WorkflowEngineException { Kind: WorkflowErrorKind.InvalidState } &&
                await catalog.GetPackageAsync(draft.Name, PackageIndexRules.PublishedVersion(draft.Id)) is { } occupied && occupied.PublishedDraft != draft.Id)
                return Failure("PUB022", "The package publication identity is already occupied");
            _logger.LogError(error, "Activity pipeline failed for {Draft}", draft.Id);
            return new(draft.Kind, null, null, null, [.. diagnostics, WorkflowPublishFailures.Classify(error, stage, location) with { Path = sourcePath }]);
        }
    }
}
