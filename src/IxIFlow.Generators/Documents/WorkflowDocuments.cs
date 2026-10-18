namespace IxIFlow.Dsl.Documents;

public sealed record WorkflowDocument : DocumentNode
{
    public string SchemaVersion { get; init; } = "1.0";
    public WorkflowDefinitionDocument Workflow { get; init; } = new();
    public WorkflowImportsDocument Imports { get; init; } = new();
    public Dictionary<string, ActivityTemplateDocument> Activities { get; init; } = new();
    public Dictionary<string, StepTemplateDocument> Steps { get; init; } = new();
    public Dictionary<string, CodeAssetDocument> Code { get; init; } = new();
    public List<WorkflowStepDocument> Definitions { get; init; } = new();
}

public sealed record WorkflowDefinitionDocument : DocumentNode
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public string DataType { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
}

public sealed record WorkflowImportsDocument : DocumentNode
{
    public List<string> Catalogs { get; init; } = [];
    public List<string> Workflows { get; init; } = [];
}

public sealed record ActivityTemplateDocument : DocumentNode
{
    public string Activity { get; init; } = string.Empty;
    public string ActivityVersion { get; init; } = string.Empty;
    public Dictionary<string, object?> Defaults { get; init; } = new();
    public List<InputMappingDocument> Input { get; init; } = [];
    public List<OutputMappingDocument> Output { get; init; } = [];
}

public sealed record StepTemplateDocument : DocumentNode
{
    public string Kind { get; init; } = string.Empty;
    public Dictionary<string, TemplateParameterDocument> Parameters { get; init; } = new();
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

public sealed record TemplateParameterDocument : DocumentNode
{
    public string Type { get; init; } = string.Empty;
    public bool Required { get; init; }
    public object? Default { get; init; }
}

public sealed record CodeAssetDocument : DocumentNode
{
    public string Language { get; init; } = string.Empty;
    public string? Returns { get; init; }
    public string? EntryPoint { get; init; }
    public string Source { get; init; } = string.Empty;
}
public abstract record WorkflowStepDocument : DocumentNode
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed record ActivityStepDocument : WorkflowStepDocument
{
    public string Activity { get; init; } = string.Empty;
    public string ActivityVersion { get; init; } = string.Empty;
    public Dictionary<string, object?> Defaults { get; init; } = new();
    public List<InputMappingDocument> Input { get; init; } = [];
    public List<OutputMappingDocument> Output { get; init; } = [];
}

public sealed record ActivityReferenceStepDocument : WorkflowStepDocument
{
    public string Ref { get; init; } = string.Empty;
}

public sealed record StepReferenceStepDocument : WorkflowStepDocument
{
    public string Ref { get; init; } = string.Empty;
    public Dictionary<string, ExpressionDocument> Arguments { get; init; } = new();
}

public sealed record ConditionalStepDocument : WorkflowStepDocument
{
    public ExpressionDocument Condition { get; init; } = new ConstantExpressionDocument();
    public List<WorkflowStepDocument> Then { get; init; } = [];
    public List<WorkflowStepDocument> Else { get; init; } = [];
}

public sealed record SequenceStepDocument : WorkflowStepDocument
{
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

public sealed record ForEachStepDocument : WorkflowStepDocument
{
    public ExpressionDocument Items { get; init; } = new PathExpressionDocument();
    public string As { get; init; } = "item";
    public ForEachParallelDocument? Parallel { get; init; }
    public List<WorkflowStepDocument> Steps { get; init; } = [];
    public ExpressionDocument? Result { get; init; }
    public PathExpressionDocument? Into { get; init; }
}

public sealed record ForEachParallelDocument : DocumentNode
{
    public int? MaxConcurrency { get; init; }
    public string Join { get; init; } = "all";
    public string? ResultType { get; init; }
    public ExpressionDocument? Condition { get; init; }
}

public sealed record SuspendStepDocument : WorkflowStepDocument
{
    public string Event { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public ExpressionDocument? ResumeCondition { get; init; }
    public List<InputMappingDocument> Input { get; init; } = [];
    public List<OutputMappingDocument> Output { get; init; } = [];
}

public sealed record InvokeWorkflowStepDocument : WorkflowStepDocument
{
    public WorkflowReferenceDocument Workflow { get; init; } = new();
    public List<InputMappingDocument> Input { get; init; } = [];
    public List<OutputMappingDocument> Output { get; init; } = [];
}

public sealed record WorkflowReferenceDocument : DocumentNode
{
    public string Name { get; init; } = string.Empty;
    public int? Version { get; init; }
}

public sealed record InputMappingDocument : DocumentNode
{
    public string Target { get; init; } = string.Empty;
    public ExpressionDocument From { get; init; } = new ConstantExpressionDocument();
}

public sealed record OutputMappingDocument : DocumentNode
{
    public string Source { get; init; } = string.Empty;
    public string SourceContext { get; init; } = "activity";
    public PathExpressionDocument To { get; init; } = new();
}
public abstract record ExpressionDocument : DocumentNode;

public sealed record ConstantExpressionDocument : ExpressionDocument
{
    /// <summary>A null, string, Boolean, number, list or string-keyed mapping; translation requires a scalar.</summary>
    public object? Value { get; init; }
}

public sealed record PathExpressionDocument : ExpressionDocument
{
    public string Path { get; init; } = string.Empty;
}

public sealed record BinaryExpressionDocument : ExpressionDocument
{
    public string Operator { get; init; } = string.Empty;
    public ExpressionDocument Left { get; init; } = new ConstantExpressionDocument();
    public ExpressionDocument Right { get; init; } = new ConstantExpressionDocument();
}

public sealed record FunctionExpressionDocument : ExpressionDocument
{
    public string Function { get; init; } = string.Empty;
    public List<ExpressionDocument> Arguments { get; init; } = [];
}
