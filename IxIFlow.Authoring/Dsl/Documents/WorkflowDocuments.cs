using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Dsl.Documents;

public sealed record WorkflowDocument
{
    public string SchemaVersion { get; init; } = "1.0";
    public WorkflowDefinitionDocument Workflow { get; init; } = new();
    public WorkflowImportsDocument Imports { get; init; } = new();
    public Dictionary<string, ActivityTemplateDocument> Activities { get; init; } = new();
    public Dictionary<string, StepTemplateDocument> Steps { get; init; } = new();
    public Dictionary<string, CodeAssetDocument> Code { get; init; } = new();
    public List<WorkflowStepDocument> Definitions { get; init; } = new();
}

public sealed record WorkflowDefinitionDocument
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public string DataType { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
}

public sealed record WorkflowImportsDocument
{
    public List<string> Catalogs { get; init; } = [];
    public List<string> Workflows { get; init; } = [];
}

public sealed record ActivityTemplateDocument
{
    public string Activity { get; init; } = string.Empty;
    public Dictionary<string, JsonElement> Defaults { get; init; } = new();
    public List<InputMappingDocument> Input { get; init; } = [];
    public List<OutputMappingDocument> Output { get; init; } = [];
}

public sealed record StepTemplateDocument
{
    public string Kind { get; init; } = string.Empty;
    public Dictionary<string, TemplateParameterDocument> Parameters { get; init; } = new();
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

public sealed record TemplateParameterDocument
{
    public string Type { get; init; } = string.Empty;
    public bool Required { get; init; }
}

public sealed record CodeAssetDocument
{
    public string Language { get; init; } = string.Empty;
    public string? Returns { get; init; }
    public string? EntryPoint { get; init; }
    public string Source { get; init; } = string.Empty;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ActivityStepDocument), typeDiscriminator: "activity")]
[JsonDerivedType(typeof(ActivityReferenceStepDocument), typeDiscriminator: "activityRef")]
[JsonDerivedType(typeof(StepReferenceStepDocument), typeDiscriminator: "stepRef")]
[JsonDerivedType(typeof(ConditionalStepDocument), typeDiscriminator: "if")]
[JsonDerivedType(typeof(SequenceStepDocument), typeDiscriminator: "sequence")]
[JsonDerivedType(typeof(SuspendStepDocument), typeDiscriminator: "suspend")]
[JsonDerivedType(typeof(InvokeWorkflowStepDocument), typeDiscriminator: "invokeWorkflow")]
public abstract record WorkflowStepDocument
{
    public string Id { get; init; } = string.Empty;
    [JsonIgnore]
    public string Kind { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed record ActivityStepDocument : WorkflowStepDocument
{
    public string Activity { get; init; } = string.Empty;
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

public sealed record WorkflowReferenceDocument
{
    public string Name { get; init; } = string.Empty;
    public int? Version { get; init; }
}

public sealed record InputMappingDocument
{
    public string Target { get; init; } = string.Empty;
    public ExpressionDocument From { get; init; } = new ConstantExpressionDocument();
}

public sealed record OutputMappingDocument
{
    public string Source { get; init; } = string.Empty;
    public string SourceContext { get; init; } = "activity";
    public PathExpressionDocument To { get; init; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConstantExpressionDocument), typeDiscriminator: "constant")]
[JsonDerivedType(typeof(PathExpressionDocument), typeDiscriminator: "path")]
[JsonDerivedType(typeof(BinaryExpressionDocument), typeDiscriminator: "binary")]
[JsonDerivedType(typeof(FunctionExpressionDocument), typeDiscriminator: "function")]
public abstract record ExpressionDocument;

public sealed record ConstantExpressionDocument : ExpressionDocument
{
    public JsonElement Value { get; init; }
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
