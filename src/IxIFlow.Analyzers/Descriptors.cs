using Microsoft.CodeAnalysis;

namespace IxIFlow.Analyzers;

/// <summary>
/// The diagnostics of the IxIFlow analyzers. Their messages are the ones the engine's build rules throw (D-26, D-42),
/// so a mistake reads the same at compile time and when the workflow is built.
/// </summary>
public static class Descriptors
{
    private const string Contracts = "IxIFlow.Contracts";
    private const string Purity = "IxIFlow.Purity";

    private const string PurityReason =
        "evaluated again after a recovery, so they may not read the clock or the environment, make values that " +
        "differ per run, wait, or do I/O (D-07)";

    /// <summary>An input mapping targets a property that is not an input of the activity, child data or event.</summary>
    public static readonly DiagnosticDescriptor NotAnInput = new(
        "IXIF0001",
        "Input mapped into a property that is not an input",
        "{0} property '{1}' is not an input: it has no {2}",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "An input mapping must target a [WorkflowInput] property when the activity marks its " +
                     "properties, and a property with a public setter otherwise (D-26).");

    /// <summary>An output mapping reads a property that is not an output.</summary>
    public static readonly DiagnosticDescriptor NotAnOutput = new(
        "IXIF0002",
        "Output mapped from a property that is not an output",
        "{0} property '{1}' is not an output: it has no {2}",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "An output mapping must read a [WorkflowOutput] property when the activity marks its " +
                     "properties, and a property with a public getter otherwise (D-26).");

    /// <summary>A required input of an activity is neither mapped nor defaulted.</summary>
    public static readonly DiagnosticDescriptor RequiredInputNotMapped = new(
        "IXIF0003",
        "Required input is not mapped",
        "{0} property '{1}' is a required input and is not mapped",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Every [WorkflowInput(Required = true)] property without a Default must be mapped where the " +
                     "activity is used (D-26).");

    /// <summary>A mapping names a member that is not a public instance property.</summary>
    public static readonly DiagnosticDescriptor NoPublicProperty = new(
        "IXIF0004",
        "Mapping names no public property",
        "{0} '{1}' has no public property '{2}' to map",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Mappings are applied to public instance properties by name (D-26).");

    /// <summary>A workflow expression uses a member that is not pure.</summary>
    public static readonly DiagnosticDescriptor ImpureExpression = new(
        "IXIF0101",
        "Workflow expression is not pure",
        "The expression uses {0}. Expressions are " + PurityReason + "; do that in an activity and map its output.",
        Purity, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Conditions, mappings, wait predicates, loop conditions and selectors are evaluated again " +
                     "after a recovery and must be pure; read time from the workflow context's UtcNow (D-07, D-42).");

    /// <summary>A combiner uses a member that is not pure.</summary>
    public static readonly DiagnosticDescriptor ImpureCombiner = new(
        "IXIF0102",
        "Combiner is not pure",
        "Combiner '{0}' uses {1}. Combiners are " + PurityReason + ".",
        Purity, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A combiner may run again after a recovery and must be pure (D-07, D-14, D-42).");

    /// <summary>The diagnostic property that holds the code the clock fix writes in place of the member.</summary>
    public const string ClockReplacement = "Replacement";
}
