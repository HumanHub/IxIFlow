using System.ComponentModel;

namespace IxIFlow.Dsl.Documents;

[Description("A workflow document with reusable templates, code assets and executable steps.")]
public sealed record WorkflowDocument : DocumentNode
{
    [Description("The document contract version.")]
    [DocumentValues("1.0")]
    public string SchemaVersion { get; init; } = "1.0";
    [Description("The workflow identity and data contract.")]
    [DocumentRequired]
    public WorkflowDefinitionDocument Workflow { get; init; } = new();
    [Description("Dependencies that publishing must resolve and pin.")]
    public WorkflowImportsDocument Imports { get; init; } = new();
    [Description("Reusable activity templates keyed by reference name.")]
    public Dictionary<string, ActivityTemplateDocument> Activities { get; init; } = new();
    [Description("Reusable step templates keyed by reference name.")]
    public Dictionary<string, StepTemplateDocument> Steps { get; init; } = new();
    [Description("Code assets keyed by function name.")]
    public Dictionary<string, CodeAssetDocument> Code { get; init; } = new();
    [Description("The workflow steps in execution order.")]
    public List<WorkflowStepDocument> Definitions { get; init; } = new();
}

[Description("The identity, data type and descriptive metadata of a workflow.")]
public sealed record WorkflowDefinitionDocument : DocumentNode
{
    [Description("The name used to register and invoke the workflow.")]
    [DocumentRequired]
    public string Name { get; init; } = string.Empty;
    [Description("The positive workflow version.")]
    public int Version { get; init; } = 1;
    [Description("The workflow data CLR type or resolved catalog key.")]
    [DocumentRequired]
    public string DataType { get; init; } = string.Empty;
    [Description("The display name stored in workflow metadata.")]
    public string? DisplayName { get; init; }
    [Description("The description stored on the workflow definition.")]
    public string? Description { get; init; }
    [Description("Tags stored on the workflow definition.")]
    public string[] Tags { get; init; } = [];
}

[Description("Dependencies resolved by a publishing host before translation.")]
public sealed record WorkflowImportsDocument : DocumentNode
{
    [Description("Activity catalog dependencies to resolve and pin.")]
    public List<string> Catalogs { get; init; } = [];
    [Description("Workflow dependencies to resolve and pin.")]
    public List<string> Workflows { get; init; } = [];
}

[Description("A reusable activity with defaults and input and output mappings.")]
public sealed record ActivityTemplateDocument : DocumentNode
{
    [Description("The activity CLR type or resolved catalog key.")]
    [DocumentRequired]
    public string Activity { get; init; } = string.Empty;
    [Description("The version that must match the resolved activity contract when supplied.")]
    public string ActivityVersion { get; init; } = string.Empty;
    [Description("Scalar activity input defaults overridden by explicit input mappings.")]
    public Dictionary<string, object?> Defaults { get; init; } = new();
    [Description("Mappings from expressions to activity inputs.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from activity outputs to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}

[Description("A reusable block of steps with optional typed parameters.")]
public sealed record StepTemplateDocument : DocumentNode
{
    [Description("The template kind retained in the document.")]
    public string Kind { get; init; } = string.Empty;
    [Description("Parameters bound from reference arguments or scalar defaults.")]
    public Dictionary<string, TemplateParameterDocument> Parameters { get; init; } = new();
    [Description("The steps expanded when the template is referenced.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("A template parameter with an optional type, required flag and scalar default.")]
public sealed record TemplateParameterDocument : DocumentNode
{
    [Description("The parameter type, with local support for string, bool, int, long, double and decimal.")]
    public string Type { get; init; } = string.Empty;
    [Description("Whether an argument must be supplied when no non-null default exists.")]
    public bool Required { get; init; }
    [Description("The scalar fallback used when an argument is omitted.")]
    public object? Default { get; init; }
}

[Description("A code asset referenced by name from function expressions.")]
public sealed record CodeAssetDocument : DocumentNode
{
    [Description("The code language, with local expressions requiring csharp-expression.")]
    [DocumentRequired]
    public string Language { get; init; } = string.Empty;
    [Description("The declared return type retained for publishing.")]
    public string? Returns { get; init; }
    [Description("The declared entry point retained for publishing.")]
    public string? EntryPoint { get; init; }
    [Description("The code source rewritten against the expression context.")]
    [DocumentRequired]
    public string Source { get; init; } = string.Empty;
}

[Description("An executable workflow step selected by its kind.")]
public abstract record WorkflowStepDocument : DocumentNode
{
    [Description("The stable step identifier, generated from its structural path when omitted.")]
    public string Id { get; init; } = string.Empty;
    [Description("The discriminator selecting the concrete step type.")]
    public string Kind { get; init; } = string.Empty;
    [Description("The explicit step name used by the fluent definition.")]
    public string? Name { get; init; }
    [Description("The step description retained in the document.")]
    public string? Description { get; init; }
}

[Description("An activity invocation with defaults and input and output mappings.")]
[DocumentKind("activity")]
public sealed record ActivityStepDocument : WorkflowStepDocument
{
    [Description("The activity CLR type or resolved catalog key.")]
    [DocumentRequired]
    public string Activity { get; init; } = string.Empty;
    [Description("The version that must match the resolved activity contract when supplied.")]
    public string ActivityVersion { get; init; } = string.Empty;
    [Description("Scalar activity input defaults overridden by explicit input mappings.")]
    public Dictionary<string, object?> Defaults { get; init; } = new();
    [Description("Mappings from expressions to activity inputs.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from activity outputs to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}

[Description("An invocation of a reusable activity template.")]
[DocumentKind("activityRef")]
public sealed record ActivityReferenceStepDocument : WorkflowStepDocument
{
    [Description("The key of an activity template in activities.")]
    [DocumentRequired]
    public string Ref { get; init; } = string.Empty;
}

[Description("An expansion of a reusable step template with bound arguments.")]
[DocumentKind("stepRef")]
public sealed record StepReferenceStepDocument : WorkflowStepDocument
{
    [Description("The key of a step template in steps.")]
    [DocumentRequired]
    public string Ref { get; init; } = string.Empty;
    [Description("Expressions bound to declared template parameters.")]
    public Dictionary<string, ExpressionDocument> Arguments { get; init; } = new();
}

[Description("A branch selected by a Boolean expression.")]
[DocumentKind("if")]
public sealed record ConditionalStepDocument : WorkflowStepDocument
{
    [Description("The Boolean expression selecting the then branch.")]
    [DocumentRequired]
    public ExpressionDocument Condition { get; init; } = new ConstantExpressionDocument();
    [Description("Steps executed when the condition is true.")]
    public List<WorkflowStepDocument> Then { get; init; } = [];
    [Description("Steps executed when the condition is false.")]
    public List<WorkflowStepDocument> Else { get; init; } = [];
}

[Description("A nested sequence of workflow steps.")]
[DocumentKind("sequence")]
public sealed record SequenceStepDocument : WorkflowStepDocument
{
    [Description("The nested steps in execution order.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("An iteration over items with an optional parallel join and collected results.")]
[DocumentKind("forEach")]
public sealed record ForEachStepDocument : WorkflowStepDocument
{
    [Description("The expression selecting the items to iterate.")]
    [DocumentRequired]
    public ExpressionDocument Items { get; init; } = new PathExpressionDocument();
    [Description("The item alias exposed to expressions in the loop body.")]
    public string As { get; init; } = "item";
    [Description("Parallel execution and join options when supplied.")]
    public ForEachParallelDocument? Parallel { get; init; }
    [Description("Steps executed for each item.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
    [Description("The expression producing each item result.")]
    public ExpressionDocument? Result { get; init; }
    [Description("The workflow path receiving the list of item results.")]
    public PathExpressionDocument? Into { get; init; }
}

[Description("Parallel iteration concurrency and completion options.")]
public sealed record ForEachParallelDocument : DocumentNode
{
    [Description("The positive maximum number of concurrent items when supplied.")]
    public int? MaxConcurrency { get; init; }
    [Description("The completion policy for parallel items.")]
    [DocumentValues("all", "any", "conditionally")]
    public string Join { get; init; } = "all";
    [Description("The result CLR type required for a conditional join.")]
    public string? ResultType { get; init; }
    [Description("The completion expression required for a conditional join, with workflow and results in scope.")]
    public ExpressionDocument? Condition { get; init; }
}

[Description("A suspension awaiting a typed resume event.")]
[DocumentKind("suspend")]
public sealed record SuspendStepDocument : WorkflowStepDocument
{
    [Description("The resume event CLR type or resolved catalog key.")]
    [DocumentRequired]
    public string Event { get; init; } = string.Empty;
    [Description("The suspension key, falling back to the step identifier when empty.")]
    public string Reason { get; init; } = string.Empty;
    [Description("The optional expression deciding whether a resume event is accepted.")]
    public ExpressionDocument? ResumeCondition { get; init; }
    [Description("Input mappings configured for the suspension.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from resume event outputs to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}

[Description("An invocation of a child workflow with a resolved data contract and version.")]
[DocumentKind("invokeWorkflow")]
public sealed record InvokeWorkflowStepDocument : WorkflowStepDocument
{
    [Description("The child workflow reference resolved before translation.")]
    [DocumentRequired]
    public WorkflowReferenceDocument Workflow { get; init; } = new();
    [Description("Mappings from expressions to child workflow inputs.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from child workflow outputs to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}

[Description("A child workflow name with an optional version resolved by the host.")]
public sealed record WorkflowReferenceDocument : DocumentNode
{
    [Description("The child workflow name to resolve.")]
    [DocumentRequired]
    public string Name { get; init; } = string.Empty;
    [Description("The requested workflow version, or the host-resolved version when omitted.")]
    public int? Version { get; init; }
}

[Description("An expression mapped to an input property.")]
public sealed record InputMappingDocument : DocumentNode
{
    [Description("The CLR input property name.")]
    [DocumentRequired]
    public string Target { get; init; } = string.Empty;
    [Description("The expression supplying the input value.")]
    [DocumentRequired]
    public ExpressionDocument From { get; init; } = new ConstantExpressionDocument();
}

[Description("An output property mapped to a workflow path.")]
public sealed record OutputMappingDocument : DocumentNode
{
    [Description("The CLR output property name.")]
    [DocumentRequired]
    public string Source { get; init; } = string.Empty;
    [Description("The output context declared by the document.")]
    [DocumentValues("activity", "event", "workflow")]
    public string SourceContext { get; init; } = "activity";
    [Description("The workflow path receiving the output value.")]
    [DocumentRequired]
    public PathExpressionDocument To { get; init; } = new();
}

[Description("An expression selected by kind, or inferred from a path member.")]
public abstract record ExpressionDocument : DocumentNode;

[Description("A literal value, which must be scalar for local translation.")]
[DocumentKind("constant")]
public sealed record ConstantExpressionDocument : ExpressionDocument
{
    /// <summary>A null, string, Boolean, number, list or string-keyed mapping; translation requires a scalar.</summary>
    [Description("A null, string, Boolean, number, list or mapping, with local translation requiring a scalar.")]
    public object? Value { get; init; }
}

[Description("A C# expression path rewritten against workflow, previous step, event or item context.")]
[DocumentKind("path", ImpliedBy = "path")]
public sealed record PathExpressionDocument : ExpressionDocument
{
    [Description("The C# expression path to read or assign.")]
    [DocumentRequired]
    public string Path { get; init; } = string.Empty;
}

[Description("A binary expression combining two operands with a supported operator.")]
[DocumentKind("binary")]
public sealed record BinaryExpressionDocument : ExpressionDocument
{
    [Description("The binary operator applied to the operands.")]
    [DocumentRequired]
    [DocumentValues("eq", "ne", "and", "or", "gt", "ge", "lt", "le", "add", "subtract", "multiply", "divide")]
    public string Operator { get; init; } = string.Empty;
    [Description("The left operand expression.")]
    [DocumentRequired]
    public ExpressionDocument Left { get; init; } = new ConstantExpressionDocument();
    [Description("The right operand expression.")]
    [DocumentRequired]
    public ExpressionDocument Right { get; init; } = new ConstantExpressionDocument();
}

[Description("A named code asset or C# method invocation.")]
[DocumentKind("function")]
public sealed record FunctionExpressionDocument : ExpressionDocument
{
    [Description("The code asset key or C# method name.")]
    [DocumentRequired]
    public string Function { get; init; } = string.Empty;
    [Description("Arguments for a C# method, with local code assets taking none.")]
    public List<ExpressionDocument> Arguments { get; init; } = [];
}
