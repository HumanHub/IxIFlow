using System.Diagnostics;
using System.Text.RegularExpressions;
using IxIFlow.Core;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Authoring;

internal sealed record WorkflowBuildResult(string Name = "", int Version = 0, string StructureHash = "",
    string DataType = "", string EntryType = "", string CombinerSignature = "",
    WorkflowVersionDependency[]? Dependencies = null, string? Error = null, int Line = 1, int Column = 1, string ContractJson = "",
    WorkflowVersionInvocation[]? Invocations = null, string DescriptorJson = "",
    string ErrorCode = "PUB004", WorkflowPublishDiagnosticClass ErrorClass = WorkflowPublishDiagnosticClass.Authoring);

internal static class WorkflowBuild
{
    public static async Task<WorkflowBuildResult> RunAsync(WorkflowCompilationResult compilation, IReadOnlyDictionary<string, byte[]> dependencies, TimeSpan timeout,
        ILogger logger, CancellationToken cancellationToken, IReadOnlyList<PackageScopeClaim>? activityClaims = null,
        IReadOnlyDictionary<string, byte[]>? symbols = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WorkflowBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = WorkflowSharedAssemblies.Current;
        // A noncooperative Build must neither occupy the pool nor keep the process alive at shutdown.
        var thread = new Thread(() =>
        {
            try { using var scope = WorkflowSharedAssemblies.Enter(profile); completion.TrySetResult(Inspect(compilation, dependencies, activityClaims, logger, symbols)); }
            // Includes failures while extracting/formatting the original build diagnostic.
            catch (Exception error)
            {
                try { logger.LogError(error, "Build() failed while inspecting its result"); } catch (Exception) { }
                var diagnostic = WorkflowPublishFailures.Classify(error, WorkflowPublishStage.Test);
                completion.TrySetResult(new(Error: diagnostic.Message, ErrorCode: diagnostic.Code, ErrorClass: diagnostic.Class,
                    Line: diagnostic.Line, Column: diagnostic.Column));
            }
        })
        { IsBackground = true, Name = "IxIFlow publish Build" };
        using (ExecutionContextFlow.SuppressIfNeeded())
            thread.Start();
        try
        {
            var result = await completion.Task.WaitAsync(timeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Build() exceeded the publish timeout of {Timeout}; thread {ThreadId} and its scratch load context are abandoned until the process restarts",
                timeout, thread.ManagedThreadId);
            return new(Error: "Build() exceeded the configured timeout", ErrorCode: "PUB043", ErrorClass: WorkflowPublishDiagnosticClass.Unavailable);
        }
        catch (OperationCanceledException) when (!completion.Task.IsCompleted)
        {
            logger.LogWarning("Publishing was canceled; Build() thread {ThreadId} and its scratch load context are abandoned until the process restarts",
                thread.ManagedThreadId);
            throw;
        }
    }

    private static WorkflowBuildResult Inspect(WorkflowCompilationResult compilation, IReadOnlyDictionary<string, byte[]> dependencies, IReadOnlyList<PackageScopeClaim>? activityClaims, ILogger logger,
        IReadOnlyDictionary<string, byte[]>? symbols)
    {
        try
        {
            return WorkflowCompilation.WithDefinition(compilation.Assembly, compilation.Pdb, dependencies, definition =>
            {
                var structureHash = WorkflowDefinitionValidator.Inspect(definition);
                // Only value metadata leaves the scratch context; validation finishes before Unload.
                using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(compilation.Assembly));
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var entry = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
                    .Single(type => metadata.GetString(type.Name) != "<Module>" &&
                        type.GetMethods().Any(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Build"));
                return new WorkflowBuildResult(definition.Name, definition.Version, structureHash, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!),
                    metadata.GetString(entry.Namespace) + "." + metadata.GetString(entry.Name),
                    WorkflowDefinitionValidator.GetCombinerSignature(definition), WorkflowDefinitionValidator.GetDependencies(definition).ToArray(),
                    ContractJson: WorkflowDefinitionValidator.GetContract(definition).ToJson(),
                    Invocations: WorkflowDefinitionValidator.GetInvocations(definition).ToArray(), DescriptorJson: WorkflowDescriptor.Of(definition).ToJson());
            }, context => ScopePackageContracts.ValidateRuntime(context, activityClaims ?? []), symbols);
        }
        catch (Exception error)
        {
            if (error is WorkflowPublishException curated)
            {
                var diagnostic = WorkflowPublishFailures.Classify(curated, WorkflowPublishStage.Test);
                return new(Error: diagnostic.Message, ErrorCode: diagnostic.Code, ErrorClass: diagnostic.Class,
                    Line: diagnostic.Line, Column: diagnostic.Column);
            }
            var location = Regex.Match(error.Message, @"workflow\.yaml:(\d+)");
            var frame = new StackTrace(error, true).GetFrames().FirstOrDefault(frame => frame.GetFileName() == "workflow.yaml");
            // A dependency the author's closure lacks is the author's error; keep the assembly name.
            if (PublishDependencyNotFoundException.Find(error) is { } refusal)
                return new(Error: refusal.Message, ErrorCode: "PUB003", ErrorClass: WorkflowPublishDiagnosticClass.Authoring,
                    Line: Math.Max(1, frame?.GetFileLineNumber() ?? 1), Column: Math.Max(1, frame?.GetFileColumnNumber() ?? 1));
            if (error is not WorkflowEngineException { Kind: WorkflowErrorKind.InvalidDefinition or WorkflowErrorKind.InvalidRequest })
            {
                try { logger.LogError(error, "Workflow build inspection failed"); } catch (Exception) { }
                var diagnostic = WorkflowPublishFailures.Classify(error, WorkflowPublishStage.Test);
                return new(Error: diagnostic.Message, ErrorCode: diagnostic.Code, ErrorClass: diagnostic.Class);
            }
            return new(Error: error.Message, Line: location.Success && int.TryParse(location.Groups[1].ValueSpan, out var line) && line > 0
                    ? line : Math.Max(1, frame?.GetFileLineNumber() ?? 1),
                Column: Math.Max(1, frame?.GetFileColumnNumber() ?? 1));
        }
    }
}
