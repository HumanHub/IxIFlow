using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Analyzers;

/// <summary>
/// The members that read the clock or the environment, make values that differ per run, wait, or do I/O: the list
/// the engine's build rule refuses in expressions (D-42), applied to symbols. Each is named as the build error names
/// it, such as <c>DateTime.UtcNow</c>, <c>new Random(...)</c> or <c>new StreamReader(path)</c>.
/// </summary>
internal static class ImpureMembers
{
    private static readonly HashSet<string> ClockMembers = ["Now", "UtcNow", "Today"];

    // Whole types whose every member reads the environment, varies per run, waits or does I/O.
    private static readonly HashSet<string> ImpureTypes =
    [
        "System.Random", "System.Environment", "System.IO.File", "System.IO.Directory", "System.IO.FileInfo",
        "System.IO.DirectoryInfo", "System.IO.FileStream", "System.Net.Http.HttpMessageInvoker",
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.Dns", "System.Data.IDbConnection",
        "System.Data.IDbCommand", "System.Data.IDataReader"
    ];

    /// <summary>The name of <paramref name="member"/> when it is impure; none when it is pure.</summary>
    public static string? Named(ISymbol member)
    {
        var type = member.ContainingType;
        if (type == null)
            return null;
        if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            return OpensAPath(constructor) ? $"new {type.Name}(path)"
                : Impure(type, ".ctor") ? $"new {type.Name}(...)" : null;
        var name = member is IMethodSymbol { AssociatedSymbol: IPropertySymbol property } ? property.Name : member.Name;
        return Impure(type, name) ? $"{type.Name}.{name}" : null;
    }

    private static bool OpensAPath(IMethodSymbol constructor) =>
        Full(constructor.ContainingType) is "System.IO.StreamReader" or "System.IO.StreamWriter" &&
        constructor.Parameters.Length > 0 && constructor.Parameters[0].Type.SpecialType == SpecialType.System_String;

    private static bool Impure(INamedTypeSymbol type, string member)
    {
        var full = Full(type);
        return (full is "System.DateTime" or "System.DateTimeOffset") && ClockMembers.Contains(member) ||
               full == "System.Guid" && member == "NewGuid" ||
               full == "System.Threading.Tasks.Task" && member == "Delay" ||
               full == "System.Threading.Thread" && member == "Sleep" ||
               full == "System.TimeProvider" && member == "System" ||
               type.ContainingNamespace?.ToDisplayString() == "System.Net.Sockets" ||
               InHierarchy(type);
    }

    // The type, its bases, or an interface it implements, so a call through IDbCommand or a derived client counts.
    private static bool InHierarchy(INamedTypeSymbol type)
    {
        for (var each = type; each != null; each = each.BaseType)
            if (ImpureTypes.Contains(Full(each)))
                return true;
        return type.AllInterfaces.Any(each => ImpureTypes.Contains(Full(each)));
    }

    private static string Full(INamedTypeSymbol type) =>
        type.ContainingType != null ? $"{Full(type.ContainingType)}+{type.MetadataName}"
        : type.ContainingNamespace is { IsGlobalNamespace: false } space ? $"{space.ToDisplayString()}.{type.MetadataName}"
        : type.MetadataName;
}
