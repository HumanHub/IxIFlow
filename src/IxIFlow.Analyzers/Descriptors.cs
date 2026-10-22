using Microsoft.CodeAnalysis;

namespace IxIFlow.Analyzers;

/// <summary>
/// The diagnostics of the IxIFlow analyzers. Their messages are the ones the engine's build rules throw,
/// so a mistake reads the same at compile time and when the workflow is built.
/// </summary>
public static class Descriptors
{
    private const string Contracts = "IxIFlow.Contracts";
    private const string Purity = "IxIFlow.Purity";

    private const string PurityReason =
        "evaluated again after a recovery, so they may not read the clock or the environment, make values that " +
        "differ per run, wait, or do I/O";

    /// <summary>An input mapping targets a property that is not an input of the activity, child data or event.</summary>
    public static readonly DiagnosticDescriptor NotAnInput = new(
        "IXIF0001",
        "Input mapped into a property that is not an input",
        "{0} property '{1}' is not an input: it has no {2}",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "An input mapping must target an [ActivityInput] or [ActivityInputOutput] property when the activity marks its " +
                     "properties, and a property with a public setter otherwise.");

    /// <summary>An output mapping reads a property that is not an output.</summary>
    public static readonly DiagnosticDescriptor NotAnOutput = new(
        "IXIF0002",
        "Output mapped from a property that is not an output",
        "{0} property '{1}' is not an output: it has no {2}",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "An output mapping must read an [ActivityOutput] or [ActivityInputOutput] property when the activity marks its " +
                     "properties, and a property with a public getter otherwise.");

    /// <summary>A required input of an activity is neither mapped nor defaulted.</summary>
    public static readonly DiagnosticDescriptor RequiredInputNotMapped = new(
        "IXIF0003",
        "Required input is not mapped",
        "{0} property '{1}' is a required input and is not mapped",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Every input with [Required] and without [DefaultValue] must be mapped where the " +
                     "activity is used.");

    /// <summary>A mapping names a member that is not a public instance property.</summary>
    public static readonly DiagnosticDescriptor NoPublicProperty = new(
        "IXIF0004",
        "Mapping names no public property",
        "{0} '{1}' has no public property '{2}' to map",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Mappings are applied to public instance properties by name.");

    /// <summary>Activity input needs a public setter.</summary>
    public static readonly DiagnosticDescriptor InputNeedsSetter = new(
        "IXIF0005",
        "Activity input needs a public setter",
        "Activity property '{0}' is marked as an input but has no public setter",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>Activity output needs a public getter.</summary>
    public static readonly DiagnosticDescriptor OutputNeedsGetter = new(
        "IXIF0006",
        "Activity output needs a public getter",
        "Activity property '{0}' is marked as an output but has no public getter",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>Required is invalid on an output.</summary>
    public static readonly DiagnosticDescriptor RequiredOnOutput = new(
        "IXIF0007",
        "Required is invalid on an output",
        "Activity property '{0}' has [Required] but is output-only",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>DefaultValue needs a public setter.</summary>
    public static readonly DiagnosticDescriptor DefaultNeedsSetter = new(
        "IXIF0008",
        "DefaultValue needs a public setter",
        "Activity property '{0}' has [DefaultValue] but has no public setter",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>DefaultValue cannot be converted.</summary>
    public static readonly DiagnosticDescriptor InvalidDefault = new(
        "IXIF0009",
        "DefaultValue cannot be converted",
        "Activity property '{0}' has a [DefaultValue] that cannot be converted to {1}",
        Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowNotAnInput.</summary>
    public static readonly DiagnosticDescriptor WorkflowNotAnInput = new(
        "IXIF0010", "WorkflowNotAnInput",
        "Child workflow property '{0}' is not an input: it has no [WorkflowInput]", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowNotAnOutput.</summary>
    public static readonly DiagnosticDescriptor WorkflowNotAnOutput = new(
        "IXIF0011", "WorkflowNotAnOutput",
        "Child workflow property '{0}' is not an output: it has no [WorkflowOutput]", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowRequiredInputNotMapped.</summary>
    public static readonly DiagnosticDescriptor WorkflowRequiredInputNotMapped = new(
        "IXIF0012", "WorkflowRequiredInputNotMapped",
        "Child workflow property '{0}' is a required input and is not mapped", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowInputNeedsSetter.</summary>
    public static readonly DiagnosticDescriptor WorkflowInputNeedsSetter = new(
        "IXIF0013", "WorkflowInputNeedsSetter",
        "Workflow property '{0}' is marked as an input but has no public setter", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowOutputNeedsGetter.</summary>
    public static readonly DiagnosticDescriptor WorkflowOutputNeedsGetter = new(
        "IXIF0014", "WorkflowOutputNeedsGetter",
        "Workflow property '{0}' is marked as an output but has no public getter", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowRequiredOnOutput.</summary>
    public static readonly DiagnosticDescriptor WorkflowRequiredOnOutput = new(
        "IXIF0015", "WorkflowRequiredOnOutput",
        "Workflow property '{0}' has [Required] but is output-only", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowDefaultNeedsSetter.</summary>
    public static readonly DiagnosticDescriptor WorkflowDefaultNeedsSetter = new(
        "IXIF0016", "WorkflowDefaultNeedsSetter",
        "Workflow property '{0}' has [DefaultValue] but has no public setter", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>WorkflowInvalidDefault.</summary>
    public static readonly DiagnosticDescriptor WorkflowInvalidDefault = new(
        "IXIF0017", "WorkflowInvalidDefault",
        "Workflow property '{0}' has a [DefaultValue] that cannot be converted to {1}", Contracts, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>A workflow expression uses a member that is not pure.</summary>
    public static readonly DiagnosticDescriptor ImpureExpression = new(
        "IXIF0101",
        "Workflow expression is not pure",
        "The expression uses {0}. Expressions are " + PurityReason + "; do that in an activity and map its output.",
        Purity, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Conditions, mappings, wait predicates, loop conditions and selectors are evaluated again " +
                     "after a recovery and must be pure; read time from the workflow context's UtcNow.");

    /// <summary>A combiner uses a member that is not pure.</summary>
    public static readonly DiagnosticDescriptor ImpureCombiner = new(
        "IXIF0102",
        "Combiner is not pure",
        "Combiner '{0}' uses {1}. Combiners are " + PurityReason + ".",
        Purity, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "A combiner may run again after a recovery and must be pure.");

    /// <summary>The diagnostic property that holds the code the clock fix writes in place of the member.</summary>
    public const string ClockReplacement = "Replacement";
}
