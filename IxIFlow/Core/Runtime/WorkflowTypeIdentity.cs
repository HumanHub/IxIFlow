using System.Reflection;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Identifies registered types without tying saved workflow state to an assembly version.
/// </summary>
public static class WorkflowTypeIdentity
{
    public static string StableName(Type? type)
    {
        if (type == null)
            return "";
        if (type.IsArray)
            return $"{StableName(type.GetElementType())}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsConstructedGenericType)
            return $"{StableName(type.GetGenericTypeDefinition())}<" +
                string.Join(",", type.GetGenericArguments().Select(StableName)) + ">";
        return $"{type.FullName}, {type.Assembly.GetName().Name}";
    }

    public static Type? Resolve(string? savedName)
    {
        if (string.IsNullOrWhiteSpace(savedName))
            return null;
        return Type.GetType(savedName, ResolveAssembly,
            (assembly, name, ignoreCase) => assembly?.GetType(name, false, ignoreCase),
            throwOnError: false);
    }

    private static Assembly? ResolveAssembly(AssemblyName requested)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, requested.Name, StringComparison.Ordinal));
        if (loaded != null)
            return loaded;
        try
        {
            return Assembly.Load(new AssemblyName(requested.Name));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
