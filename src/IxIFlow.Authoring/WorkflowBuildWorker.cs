using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Core;

namespace IxIFlow.Authoring;

internal sealed record WorkflowBuildRequest(string AssemblyPath, string PdbPath, string[] References);
internal sealed record WorkflowBuildResult(string Name = "", int Version = 0, string StructureHash = "",
    string DataType = "", string EntryType = "", string CombinerSignature = "",
    WorkflowVersionReference[]? Dependencies = null, string? Error = null, int Line = 1, int Column = 1);

/// <summary>Private process entry point: bounded Build execution, with no catalog or store access.</summary>
internal static class WorkflowBuildWorker
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--ixiflow-build") return 2;
        var request = JsonSerializer.Deserialize<WorkflowBuildRequest>(await File.ReadAllTextAsync(args[1]))!;
        var references = request.References.ToDictionary(path => AssemblyName.GetAssemblyName(path).FullName!, path => path,
            StringComparer.OrdinalIgnoreCase);
        AssemblyLoadContext.Default.Resolving += (_, name) => references.TryGetValue(name.FullName, out var path)
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        var result = Inspect(request);
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(result));
        return 0;
    }

    // Keep engine-dependent JIT compilation after the resolver is installed.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WorkflowBuildResult Inspect(WorkflowBuildRequest request)
    {
        try
        {
            return WorkflowCompilation.WithDefinition(File.ReadAllBytes(request.AssemblyPath), File.ReadAllBytes(request.PdbPath), definition =>
            {
                var structureHash = WorkflowDefinitionValidator.Inspect(definition);
                // Only value metadata leaves the scratch context; validation finishes before Unload.
                using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(request.AssemblyPath));
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var entry = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
                    .Single(type => metadata.GetString(type.Name) != "<Module>" &&
                        type.GetMethods().Any(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Build"));
                return new WorkflowBuildResult(definition.Name, definition.Version, structureHash, definition.WorkflowDataType!.AssemblyQualifiedName!,
                    metadata.GetString(entry.Namespace) + "." + metadata.GetString(entry.Name),
                    WorkflowDefinitionValidator.GetCombinerSignature(definition), WorkflowDefinitionValidator.GetDependencies(definition).ToArray());
            });
        }
        catch (Exception error)
        {
            var location = Regex.Match(error.Message, @"workflow\.yaml:(\d+)");
            var frame = new StackTrace(error, true).GetFrames().FirstOrDefault(frame => frame.GetFileName() == "workflow.yaml");
            return new(Error: error.Message, Line: location.Success ? int.Parse(location.Groups[1].Value) : frame?.GetFileLineNumber() ?? 1,
                Column: Math.Max(1, frame?.GetFileColumnNumber() ?? 1));
        }
    }

    public static async Task<WorkflowBuildResult> RunAsync(WorkflowCompilationResult compilation, string[] references,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.Publish", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "workflow.dll");
            var pdbPath = Path.Combine(directory, "workflow.pdb");
            var requestPath = Path.Combine(directory, "request.json");
            var responsePath = Path.Combine(directory, "response.json");
            var runtimeConfig = Path.Combine(directory, "worker.runtimeconfig.json");
            await File.WriteAllBytesAsync(assemblyPath, compilation.Assembly, cancellationToken);
            await File.WriteAllBytesAsync(pdbPath, compilation.Pdb, cancellationToken);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new WorkflowBuildRequest(assemblyPath, pdbPath, references)), cancellationToken);
            await File.WriteAllTextAsync(runtimeConfig, JsonSerializer.Serialize(new
            { runtimeOptions = new { tfm = "net10.0", framework = new { name = "Microsoft.NETCore.App", version = Environment.Version.ToString() } } }), cancellationToken);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfig, typeof(WorkflowBuildWorker).Assembly.Location,
                         "--ixiflow-build", requestPath, responsePath }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the publisher worker");
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await stdout;
                await stderr;
                cancellationToken.ThrowIfCancellationRequested();
                return new(Error: $"Build() exceeded the publish timeout of {timeout}");
            }
            await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0 || !File.Exists(responsePath))
                return new(Error: $"The publisher worker failed ({process.ExitCode}): {errors}");
            return JsonSerializer.Deserialize<WorkflowBuildResult>(await File.ReadAllTextAsync(responsePath, cancellationToken))!;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
