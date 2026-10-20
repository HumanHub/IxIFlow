using System.Diagnostics;
using System.Text.RegularExpressions;
using IxIFlow.Core;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Authoring;

internal sealed record WorkflowBuildResult(string Name = "", int Version = 0, string StructureHash = "",
    string DataType = "", string EntryType = "", string CombinerSignature = "",
    WorkflowVersionReference[]? Dependencies = null, string? Error = null, int Line = 1, int Column = 1);

internal static class WorkflowBuild
{
    public static async Task<WorkflowBuildResult> RunAsync(WorkflowCompilationResult compilation, TimeSpan timeout,
        ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WorkflowBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A noncooperative Build must neither occupy the pool nor keep the process alive at shutdown.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Inspect(compilation)); }
            // Includes failures while extracting/formatting the original build diagnostic.
            catch (Exception) { completion.TrySetResult(new(Error: "Build() failed while inspecting its result")); }
        })
        { IsBackground = true, Name = "IxIFlow publish Build" };
        using (ExecutionContext.SuppressFlow())
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
            return new(Error: $"Build() exceeded the publish timeout of {timeout}");
        }
        catch (OperationCanceledException) when (!completion.Task.IsCompleted)
        {
            logger.LogWarning("Publishing was canceled; Build() thread {ThreadId} and its scratch load context are abandoned until the process restarts",
                thread.ManagedThreadId);
            throw;
        }
    }

    private static WorkflowBuildResult Inspect(WorkflowCompilationResult compilation)
    {
        try
        {
            return WorkflowCompilation.WithDefinition(compilation.Assembly, compilation.Pdb, definition =>
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
                    WorkflowDefinitionValidator.GetCombinerSignature(definition), WorkflowDefinitionValidator.GetDependencies(definition).ToArray());
            });
        }
        catch (Exception error)
        {
            var location = Regex.Match(error.Message, @"workflow\.yaml:(\d+)");
            var frame = new StackTrace(error, true).GetFrames().FirstOrDefault(frame => frame.GetFileName() == "workflow.yaml");
            return new(Error: error.Message, Line: location.Success && int.TryParse(location.Groups[1].ValueSpan, out var line) && line > 0
                    ? line : Math.Max(1, frame?.GetFileLineNumber() ?? 1),
                Column: Math.Max(1, frame?.GetFileColumnNumber() ?? 1));
        }
    }
}
