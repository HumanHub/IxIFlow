using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScopeAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor ValueProperty = Rule("IXIF0018", "Scope value escapes saved configuration", "Scope property '{0}' can hold its live value; keep the value out of public state", DiagnosticSeverity.Warning);
    public static readonly DiagnosticDescriptor RequirementOwner = Rule("IXIF0019", "Scope requirement needs an activity", "Type '{0}' declares a scope requirement but is not an activity");
    public static readonly DiagnosticDescriptor DualActivity = Rule("IXIF0020", "Scope cannot execute as an ordinary activity", "Type '{0}' implements both IAsyncActivity and IScopeActivity");
    public static readonly DiagnosticDescriptor DelegateInput = Rule("IXIF0021", "Scope lifecycle cannot use delegate inputs", "Scope input '{0}' is a delegate; lifecycle code has no workflow data");
    public static readonly DiagnosticDescriptor InvalidThreading = Rule("IXIF0022", "Invalid scope execution mode", "Type '{0}' has an invalid scope threading or concurrency declaration");
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ValueProperty, RequirementOwner, DualActivity, DelegateInput, InvalidThreading);
    private static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(id, title, message, "IxIFlow.Contracts", severity, true);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Check, SymbolKind.NamedType);
    }

    private static void Check(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class) return;
        var scope = type.AllInterfaces.FirstOrDefault(item => item.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.IScopeActivity<TValue>");
        var ordinary = type.AllInterfaces.Any(item => item.ToDisplayString() == "IxIFlow.Core.IAsyncActivity");
        var attributes = type.GetAttributes();
        var location = type.Locations.FirstOrDefault();
        if (scope != null && ordinary) Report(DualActivity, type.Name);
        if (scope == null && !ordinary && attributes.Any(item => item.AttributeClass?.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.RequiresScopeAttribute<TValue>"))
            Report(RequirementOwner, type.Name);
        foreach (var attribute in attributes.Where(item => item.AttributeClass?.ToDisplayString() is "IxIFlow.Core.ScopeThreadingAttribute" or "IxIFlow.Core.ScopeConcurrencyAttribute"))
            if (scope == null || attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not int mode || mode < 0 || mode > 1)
                Report(InvalidThreading, type.Name);
        if (scope == null) return;
        var value = scope.TypeArguments[0];
        var properties = type.GetMembers().OfType<IPropertySymbol>().Where(item => item.DeclaredAccessibility == Accessibility.Public && !item.IsStatic).ToArray();
        var marked = properties.Any(item => item.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() is
            "IxIFlow.ActivitySdk.ActivityInputAttribute" or "IxIFlow.ActivitySdk.ActivityOutputAttribute" or "IxIFlow.ActivitySdk.ActivityInputOutputAttribute"));
        foreach (var property in properties)
        {
            if (property.Type.SpecialType == SpecialType.System_Object || SymbolEqualityComparer.Default.Equals(property.Type, value) ||
                value is INamedTypeSymbol named && named.AllInterfaces.Contains(property.Type, SymbolEqualityComparer.Default))
                context.ReportDiagnostic(Diagnostic.Create(ValueProperty, property.Locations.FirstOrDefault(), type.Name + "." + property.Name));
            var input = marked ? property.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() is
                "IxIFlow.ActivitySdk.ActivityInputAttribute" or "IxIFlow.ActivitySdk.ActivityInputOutputAttribute") : property.SetMethod?.DeclaredAccessibility == Accessibility.Public;
            if (input && property.Type.TypeKind == TypeKind.Delegate)
                context.ReportDiagnostic(Diagnostic.Create(DelegateInput, property.Locations.FirstOrDefault(), type.Name + "." + property.Name));
        }
        void Report(DiagnosticDescriptor descriptor, string name) => context.ReportDiagnostic(Diagnostic.Create(descriptor, location, name));
    }
}
