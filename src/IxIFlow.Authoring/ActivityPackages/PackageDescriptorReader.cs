using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Authoring;

internal sealed record PreparedPackage(PackageUploadCandidate Candidate, PackageActivities Activities);

internal sealed record PackageDescriptorSet(IReadOnlyDictionary<string, IReadOnlyList<ActivityDescriptor>> Packages,
    IReadOnlyDictionary<string, string> Assemblies);

internal static class PackageDescriptorReader
{
    internal static async Task<PackageDescriptorSet> ReadAsync(
        ResolvedWorkflowPackages packages, TimeSpan timeout, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var completion = new TaskCompletionSource<(PackageDescriptorSet? Result, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult((Inspect(packages), null)); }
            catch (Exception error) { completion.TrySetResult((null, error.Message)); }
        }) { IsBackground = true, Name = "IxIFlow package descriptors" };
        using (ExecutionContextFlow.SuppressIfNeeded()) thread.Start();
        try
        {
            var (result, error) = await completion.Task.WaitAsync(timeout, token);
            if (error != null) throw new WorkflowPublishException("PUB016", error, new(1, 1));
            return result!;
        }
        catch (TimeoutException)
        { throw new WorkflowPublishException("PUB043", "Package descriptor extraction exceeded the configured timeout", new(1, 1)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackageDescriptorSet Inspect(ResolvedWorkflowPackages packages)
    {
        var context = new PackageScratchContext(packages.RuntimeAssemblies);
        try
        {
            using var scope = WorkflowTypeIdentity.Enter(context);
            var result = new Dictionary<string, IReadOnlyList<ActivityDescriptor>>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            foreach (var pin in packages.Pins)
            {
                var selected = pin.AppliesTo(rid) ? pin.RuntimeAssets.GetValueOrDefault(rid) ??
                    (!pin.RequiresRuntimeIdentifier ? pin.RuntimeAssets.GetValueOrDefault("") : null) : null;
                var descriptors = new List<ActivityDescriptor>();
                foreach (var path in selected?.ManagedAssemblies ?? [])
                {
                    var identity = ResolvedWorkflowPackages.AssemblyIdentity(packages.Contents[pin.Id][path]);
                    if (WorkflowSharedAssemblies.IsShared(identity)) continue;
                    var assembly = context.LoadFromAssemblyName(identity);
                    foreach (var descriptor in ActivityDescriptor.Scan(assembly, pin.Id, pin.Version))
                    {
                        if (!keys.Add(descriptor.Key)) throw new InvalidOperationException($"Duplicate package activity key '{descriptor.Key}'");
                        descriptors.Add(ActivityDescriptor.Parse(descriptor.ToJson()));
                        assemblies.Add(descriptor.Key, identity.Name!);
                    }
                }
                result.Add(pin.Id, descriptors.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
            }
            return new(result, assemblies);
        }
        finally
        {
            WorkflowJson.Release(context);
            context.Unload();
        }
    }
}

internal sealed class PackageScratchContext(IReadOnlyDictionary<string, byte[]> assemblies) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly Load(AssemblyName name)
    {
        if (WorkflowSharedAssemblies.IsShared(name)) return Default.LoadFromAssemblyName(name);
        if (assemblies.TryGetValue(name.Name!, out var bytes)) return LoadFromStream(new MemoryStream(bytes, writable: false));
        throw new FileNotFoundException($"Package dependency '{name.Name}' has no implementation in the selected runtime closure");
    }
}

internal sealed class PackageClosureUnavailableException(IReadOnlyList<string> missing) : Exception("Package dependency closure is unavailable")
{
    internal IReadOnlyList<string> MissingPackageIds { get; } = missing;
}
