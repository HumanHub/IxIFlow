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
    [Description("Placement constraints resolved once against the workflow start data.")]
    public List<WorkflowDemandDocument> Demands { get; init; } = [];
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

[Description("A capability required by the workflow, optionally inherited by its children.")]
public sealed record WorkflowDemandDocument : DocumentNode
{
    [Description("The capability key.")]
    [DocumentRequired]
    public string Key { get; init; } = string.Empty;
    [Description("A literal capability value, or null for a key-only demand.")]
    public string? Value { get; init; }
    [Description("A pure expression evaluated against start data instead of a literal value.")]
    public ExpressionDocument? From { get; init; }
    [Description("Whether children inherit this residency constraint.")]
    public bool Inherit { get; init; }
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
    [Description("The code language, which must be csharp-expression.")]
    [DocumentRequired]
    public string Language { get; init; } = string.Empty;
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
}

[Description("An activity invocation with defaults and input and output mappings.")]
[DocumentKind("activity")]
public record ActivityStepDocument : WorkflowStepDocument
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
    [Description("The compensation configured for a saga activity or request.")]
    public List<CompensationDocument> Compensation { get; init; } = [];
    [Description("The ordered error policies configured for a saga activity or request.")]
    public List<StepErrorPolicyDocument> OnError { get; init; } = [];
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
    [DocumentValues("all", "any", "conditionally", DiagnosticCode = "EACH005")]
    public string Join { get; init; } = "all";
    [Description("The result CLR type required for a conditional join.")]
    public string? ResultType { get; init; }
    [Description("The completion expression required for a conditional join, with workflow and results in scope.")]
    public ExpressionDocument? Condition { get; init; }
    [Description("Combiner overrides used when merging parallel item outputs.")]
    public List<JoinCombinerDocument> Combine { get; init; } = [];
}

[Description("A typed event wait with mappings, a predicate and an optional timeout path.")]
public abstract record WaitStepDocument : WorkflowStepDocument
{
    [Description("The event CLR type or resolved catalog key.")]
    [DocumentRequired]
    public string Event { get; init; } = string.Empty;
    [Description("The optional expression deciding whether an event is accepted.")]
    public ExpressionDocument? ResumeCondition { get; init; }
    [Description("The initial suggested event payload, overlaid with input mappings when the wait opens; never used as an answer automatically.")]
    public object? DefaultPayload { get; init; }
    [Description("Pure input mappings evaluated once against workflow data and the previous step when the wait opens, overlaid on defaultPayload or a new event instance and saved as the suggested payload.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from the event or reply to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
    [Description("The positive invariant TimeSpan after which the wait times out.")]
    public string? Timeout { get; init; }
    [Description("The optional steps executed when the wait times out.")]
    public List<WorkflowStepDocument>? OnTimeout { get; init; }
}

[Description("A suspension awaiting a typed resume event.")]
[DocumentKind("suspend")]
public sealed record SuspendStepDocument : WaitStepDocument
{
    [Description("The suspension key, falling back to the step identifier when empty.")]
    public string Reason { get; init; } = string.Empty;
}

[Description("An invocation of a child workflow with a resolved data contract and version.")]
[DocumentKind("invokeWorkflow")]
public sealed record InvokeWorkflowStepDocument : WorkflowStepDocument
{
    [Description("Whether cancelling the invoking branch cancels the child and waits for it to finish.")]
    public bool CancelWithParent { get; init; } = true;
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
    [Description("The CLR output property name, read from the activity, the wait event or the invoked child workflow data according to the step kind.")]
    [DocumentRequired]
    public string Source { get; init; } = string.Empty;
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
    [DocumentValues("eq", "ne", "and", "or", "gt", "ge", "lt", "le", "add", "subtract", "multiply", "divide", DiagnosticCode = "EXPR002")]
    public string Operator { get; init; } = string.Empty;
    [Description("The left operand expression.")]
    [DocumentRequired]
    public ExpressionDocument Left { get; init; } = new ConstantExpressionDocument();
    [Description("The right operand expression.")]
    [DocumentRequired]
    public ExpressionDocument Right { get; init; } = new ConstantExpressionDocument();
}

[Description("A reference to a named csharp-expression code asset, taking no arguments.")]
[DocumentKind("function")]
public sealed record FunctionExpressionDocument : ExpressionDocument
{
    [Description("The key of a code asset whose language is csharp-expression; method calls belong in the asset source.")]
    [DocumentRequired]
    public string Function { get; init; } = string.Empty;
}

[Description("Parallel branches whose output changes are merged when the join completes.")]
[DocumentKind("parallel")]
public sealed record ParallelStepDocument : WorkflowStepDocument
{
    [Description("The branches configured with Do, in declaration order.")]
    [DocumentRequired]
    public List<ParallelBranchDocument> Branches { get; init; } = [];
    [Description("The completion policy: all, any or conditionally.")]
    [DocumentValues("all", "any", "conditionally")]
    public string Join { get; init; } = "all";
    [Description("The expression over provisional merged workflow data required for a conditional join.")]
    public ExpressionDocument? Condition { get; init; }
    [Description("Combiner overrides used when merging branch outputs.")]
    public List<JoinCombinerDocument> Combine { get; init; } = [];
}

[Description("One parallel branch, with its steps in execution order.")]
public sealed record ParallelBranchDocument : DocumentNode
{
    [Description("Steps executed in this branch.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("A combiner selected for a workflow data property at a parallel join.")]
public sealed record JoinCombinerDocument : DocumentNode
{
    [Description("The workflow data property merged by this combiner.")]
    [DocumentRequired]
    public PathExpressionDocument Property { get; init; } = new();
    [Description("The CLR type of the selected property.")]
    [DocumentRequired]
    public string PropertyType { get; init; } = string.Empty;
    [Description("The CLR type implementing ICombiner of the property type.")]
    [DocumentRequired]
    public string Combiner { get; init; } = string.Empty;
}

[Description("A loop with a condition and a sequential body.")]
public abstract record LoopStepDocument : WorkflowStepDocument
{
    [Description("The Boolean loop condition.")]
    [DocumentRequired]
    public ExpressionDocument Condition { get; init; } = new ConstantExpressionDocument();
    [Description("Steps executed on each iteration.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("A loop that tests its condition before executing its body.")]
[DocumentKind("whileDo")]
public sealed record WhileDoStepDocument : LoopStepDocument;

[Description("A loop that tests its condition after executing its body.")]
[DocumentKind("doWhile")]
public sealed record DoWhileStepDocument : LoopStepDocument;

[Description("A try body with ordered catches and an optional finally body.")]
[DocumentKind("try")]
public sealed record TryStepDocument : WorkflowStepDocument
{
    [Description("Steps executed in the try body.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
    [Description("Ordered typed or catch-all handlers.")]
    public List<CatchDocument> Catches { get; init; } = [];
    [Description("The finally body; an empty list still declares a finally.")]
    public List<WorkflowStepDocument>? Finally { get; init; }
}

[Description("A catch handler with an optional exception type and saved fault type.")]
public sealed record CatchDocument : DocumentNode
{
    [Description("The stable handler block identifier, generated from its structural path when omitted.")]
    public string? Id { get; init; }
    [Description("The exception CLR type, or a catch-all when omitted.")]
    public string? Exception { get; init; }
    [Description("The saved fault CLR type for Catch of exception and fault.")]
    public string? FaultType { get; init; }
    [Description("Steps executed by the handler.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("A saga with compensable activities and ordered recovery handlers.")]
[DocumentKind("saga")]
public sealed record SagaStepDocument : WorkflowStepDocument
{
    [Description("Saga activities, requests, waits and outcome branches.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
    [Description("Ordered typed or catch-all saga error handlers.")]
    public List<SagaErrorHandlerDocument> OnError { get; init; } = [];
}

[Description("The compensation of one saga activity or request.")]
public sealed record CompensationDocument : DocumentNode
{
    [Description("The compensation activity CLR type or catalog key.")]
    [DocumentRequired]
    public string Activity { get; init; } = string.Empty;
    [Description("The reply type selecting the request-answer compensation overload.")]
    public string? ReplyType { get; init; }
    [Description("The earlier compensation activity type selecting the previous-chain overload.")]
    public string? PreviousActivity { get; init; }
    [Description("Inputs from workflow/currentStep, or reply/answered with replyType; workflow only with previousActivity.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Inputs from the incoming previous step in the previous-chain overload.")]
    public List<InputMappingDocument> PreviousStepInput { get; init; } = [];
    [Description("Inputs from the earlier compensation in the previous-chain overload.")]
    public List<InputMappingDocument> PreviousCompensationInput { get; init; } = [];
    [Description("Outputs mapped into workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}

[Description("One saga activity's typed or general error policy.")]
public sealed record StepErrorPolicyDocument : DocumentNode
{
    [Description("The exception CLR type, or a general handler when omitted.")]
    public string? Exception { get; init; }
    [Description("The action taken when this step fails.")]
    [DocumentRequired]
    [DocumentValues("retry", "terminate", "ignore")]
    public string Then { get; init; } = "retry";
    [Description("The optional retry policy; omission selects ThenRetry(3), supplied objects use RetryPolicy defaults.")]
    public RetryPolicyDocument? Retry { get; init; }
}

[Description("The public RetryPolicy settings, with the same defaults.")]
public sealed record RetryPolicyDocument : DocumentNode
{
    [Description("The maximum number of attempts.")]
    public int MaximumAttempts { get; init; } = 3;
    [Description("The initial retry interval as an invariant TimeSpan.")]
    public string InitialInterval { get; init; } = "00:00:01";
    [Description("The maximum retry interval as an invariant TimeSpan.")]
    public string MaximumInterval { get; init; } = "00:00:10";
    [Description("The exponential backoff coefficient.")]
    public double BackoffCoefficient { get; init; } = 2;
}

[Description("A saga recovery handler's steps, undo choice and continuation choice.")]
public sealed record SagaErrorHandlerDocument : DocumentNode
{
    [Description("The stable handler block identifier, generated from its structural path when omitted.")]
    public string? Id { get; init; }
    [Description("The exception CLR type, or a catch-all when omitted.")]
    public string? Exception { get; init; }
    [Description("The saved fault CLR type for a typed saga handler.")]
    public string? FaultType { get; init; }
    [Description("Activities and waits executed by the handler before its continuation.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
    [Description("How much to undo: all or none. Use compensateUpTo to undo through an activity. Omission emits no undo call.")]
    [DocumentValues("all", "none")]
    public string? Compensate { get; init; }
    [Description("The saga activity type undone inclusively; selects the up-to compensation strategy.")]
    public string? CompensateUpTo { get; init; }
    [Description("What follows the undo: continue, terminate or retry. Omission rethrows the original error.")]
    [DocumentValues("continue", "terminate", "retry")]
    public string? Then { get; init; }
    [Description("The optional retry policy; omission selects ThenRetry(3), supplied objects use RetryPolicy defaults.")]
    public RetryPolicyDocument? Retry { get; init; }
    [Description("Whether ThenContinue ignores compensation errors.")]
    public bool IgnoreCompensationErrors { get; init; }
}

[Description("Saga branching selected by a typed value.")]
[DocumentKind("outcome")]
public sealed record OutcomeStepDocument : WorkflowStepDocument
{
    [Description("The expression selecting an outcome value.")]
    [DocumentRequired]
    public ExpressionDocument Value { get; init; } = new ConstantExpressionDocument();
    [Description("Ordered typed outcome labels and saga bodies.")]
    public List<OutcomeBranchDocument> Branches { get; init; } = [];
    [Description("The saga body selected when no label matches.")]
    public List<WorkflowStepDocument>? Default { get; init; }
}

[Description("One typed outcome label and its saga body.")]
public sealed record OutcomeBranchDocument : DocumentNode
{
    [Description("The label expression evaluated at build time and passed to Outcome.")]
    [DocumentRequired]
    public ExpressionDocument Value { get; init; } = new ConstantExpressionDocument();
    [Description("Saga steps executed for this label.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("A durable timer that retains the incoming previous step.")]
[DocumentKind("delay")]
public sealed record DelayStepDocument : WorkflowStepDocument
{
    [Description("The positive delay as an invariant TimeSpan.")]
    [DocumentRequired]
    public string Duration { get; init; } = string.Empty;
}

[Description("A request activity whose reply token is issued by the engine.")]
[DocumentKind("request")]
public sealed record RequestStepDocument : ActivityStepDocument
{
    [Description("The name pairing this request with exactly one WaitFor in the same body.")]
    [DocumentRequired]
    public string Key { get; init; } = string.Empty;
    [Description("The optional subject expression computed when request inputs are captured.")]
    public ExpressionDocument? Subject { get; init; }
}

[Description("A named event or request-reply wait.")]
[DocumentKind("waitFor")]
public sealed record WaitForStepDocument : WaitStepDocument
{
    [Description("The wait key, paired with a request of the same name when there is one.")]
    [DocumentRequired]
    public string Key { get; init; } = string.Empty;
}

[Description("A ForEach result at the end of its body or of a timeout path leaving it.")]
[DocumentKind("result")]
public sealed record ResultStepDocument : WorkflowStepDocument
{
    [Description("The value returned for the current item.")]
    [DocumentRequired]
    public ExpressionDocument Value { get; init; } = new ConstantExpressionDocument();
}

[Description("A child invocation through the workflow CLR type and its data contract.")]
[DocumentKind("invokeWorkflowType")]
public sealed record InvokeWorkflowTypeStepDocument : WorkflowStepDocument
{
    [Description("The CLR type implementing IWorkflow of dataType.")]
    [DocumentRequired]
    public string WorkflowType { get; init; } = string.Empty;
    [Description("The child workflow data CLR type fallback when IWorkflow<T> cannot be inferred from resolved symbols.")]
    public string? DataType { get; init; }
    [Description("Whether cancelling the invoking branch cancels the child and waits for it to finish.")]
    public bool CancelWithParent { get; init; } = true;
    [Description("Mappings from expressions to child workflow inputs.")]
    public List<InputMappingDocument> Input { get; init; } = [];
    [Description("Mappings from child workflow outputs to workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];
}
