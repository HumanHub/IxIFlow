using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Generators;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Authoring;

internal static class ActivityBuild
{
    private sealed record Inspection(string? Descriptor = null, string? Code = null, string? Message = null);

    internal static async Task<ActivityDescriptor> RunAsync(WorkflowCompilationResult compilation, ActivityPackageIdentity identity,
        ResolvedWorkflowPackages packages, TimeSpan timeout, ILogger logger, CancellationToken token)
    {
        var profile = WorkflowSharedAssemblies.Current;
        var completion = new TaskCompletionSource<Inspection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var shared = WorkflowSharedAssemblies.Enter(profile);
                completion.TrySetResult(new(Descriptor: Inspect(compilation, identity, packages)));
            }
            catch (WorkflowPublishException error) { completion.TrySetResult(new(Code: error.Code, Message: error.Message)); }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidDefinition)
            { completion.TrySetResult(new(Code: "PUB021", Message: error.Message)); }
            catch (Exception error)
            {
                // Exceptions may themselves be package-owned objects. Only portable
                // diagnostics, never exceptions or reflected types, leave this thread.
                try { logger.LogError(error, "Activity Test inspection failed"); } catch (Exception) { }
                var diagnostic = WorkflowPublishFailures.Classify(error);
                completion.TrySetResult(new(Code: diagnostic.Code, Message: diagnostic.Message));
            }
        }) { IsBackground = true, Name = "IxIFlow activity Test" };
        using (ExecutionContextFlow.SuppressIfNeeded()) thread.Start();
        try
        {
            var result = await completion.Task.WaitAsync(timeout, token);
            if (result.Code != null) throw new WorkflowPublishException(result.Code, result.Message!, new(1, 1));
            return ActivityDescriptor.Parse(result.Descriptor!);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Activity Test exceeded {Timeout}; thread {ThreadId} and its scratch context are abandoned", timeout, thread.ManagedThreadId);
            throw new WorkflowPublishException("PUB043", "Activity inspection exceeded the configured timeout", new(1, 1));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Inspect(WorkflowCompilationResult compilation, ActivityPackageIdentity identity, ResolvedWorkflowPackages packages)
    {
        var context = new PackageScratchContext(packages.RuntimeAssemblies, packages.RuntimeSymbols);
        try
        {
            using var types = WorkflowTypeIdentity.Enter(context);
            using var dll = new MemoryStream(compilation.Assembly, writable: false);
            using var pdb = new MemoryStream(compilation.Pdb, writable: false);
            var assembly = context.LoadFromStream(dll, pdb);
            if (assembly.GetName().Name != identity.AssemblyName || assembly.GetName().Version?.ToString() != identity.AssemblyVersion ||
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != identity.InformationalVersion)
                throw new WorkflowPublishException("PUB021", "Generated activity assembly identity differs from the document", new(1, 1));
            var type = assembly.GetType(identity.MetadataTypeName, throwOnError: true)!;
            var ordinary = typeof(IAsyncActivity).IsAssignableFrom(type);
            var scopes = type.GetInterfaces().Count(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IScopeActivity<>));
            if (ordinary == (scopes > 0) || scopes > 1 || !type.IsVisible || type.IsAbstract || type.IsGenericType)
                throw new WorkflowPublishException("PUB021", "Generated activity requires exactly one ordinary or scope implementation", new(1, 1));
            var descriptor = ActivityDescriptor.OfType(type, identity.PackageId, identity.PackageVersion);
            ActivityDescriptorRules.Validate(descriptor);
            if (descriptor.Key != identity.PackageId || descriptor.Version != identity.ActivityVersion)
                throw new WorkflowPublishException("PUB021", "Generated activity declaration differs from the document", new(1, 1));
            return descriptor.ToJson();
        }
        finally { WorkflowJson.Release(context); context.Unload(); }
    }
}
