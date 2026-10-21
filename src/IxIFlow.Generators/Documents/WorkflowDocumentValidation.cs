using System.Globalization;
using System.Collections;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Dsl.Compilation;

public enum WorkflowDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record WorkflowDiagnostic(string Code, string Message, WorkflowDiagnosticSeverity Severity, string? Path = null);

public sealed record WorkflowValidationResult(IReadOnlyList<WorkflowDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(d => d.Severity != WorkflowDiagnosticSeverity.Error);
}

/// <summary>Document contracts supplied by the authoring or compiler host.</summary>
public interface IWorkflowValidationContext
{
    Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token);
    Task<bool> HasDataTypeAsync(string key, CancellationToken token);
    Task<bool> HasEventAsync(string key, CancellationToken token);
    Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token);
    Task<bool> HasStepTemplateAsync(string key, CancellationToken token);
}

public sealed record ActivityDocumentContract(string Key, string Version, string PackageName = "", string PackageVersion = "");

public interface IWorkflowDocumentValidator
{
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDocument document, IWorkflowValidationContext context, CancellationToken cancellationToken = default);
}

internal enum WorkflowDocumentScope { Workflow, Catch, Saga, SagaHandler }

public sealed class WorkflowDocumentValidator : IWorkflowDocumentValidator
{
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDocument document, IWorkflowValidationContext context, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<WorkflowDiagnostic>();

        ValidateMembers(document, "", document.Members.Count > 0, diagnostics);
        if (!HasMissingMember("workflow", diagnostics) && document.Workflow != null)
        {
            if (document.Workflow.Version < 1)
                Invalid("WF004", "Workflow version must be a positive integer", "workflow.version", diagnostics);

            if (!string.IsNullOrWhiteSpace(document.Workflow.DataType) && !await context.HasDataTypeAsync(document.Workflow.DataType, cancellationToken))
            {
                diagnostics.Add(new WorkflowDiagnostic("WF003", $"Unknown workflow data type '{document.Workflow.DataType}'", WorkflowDiagnosticSeverity.Error, "workflow.dataType"));
            }
        }

        for (var index = 0; index < document.Demands.Count; index++)
        {
            var demand = document.Demands[index];
            var path = $"demands[{index}]";
            if (string.IsNullOrWhiteSpace(demand.Key) || demand.Key.Length > 400)
                Invalid("DEMAND001", "Demand keys must contain 1 to 400 characters", path + ".key", diagnostics);
            if (demand.Value != null && demand.From != null)
                Invalid("DEMAND002", "A demand accepts value or from, not both", path, diagnostics);
            ValidateExpression(demand.From, diagnostics, path + ".from", ["workflow"], document.Code);
        }

        foreach (var activityTemplate in document.Activities)
        {
            await ValidateActivityTemplateAsync(activityTemplate.Key, activityTemplate.Value, document, diagnostics, context, cancellationToken);
        }

        foreach (var stepTemplate in document.Steps)
        {
            await ValidateStepsAsync(stepTemplate.Value.Steps, $"steps.{stepTemplate.Key}.steps", document, diagnostics, context, cancellationToken, stepTemplate.Value.Parameters.Keys);
        }

        await ValidateStepsAsync(document.Definitions, "definitions", document, diagnostics, context, cancellationToken);

        return new WorkflowValidationResult(diagnostics);
    }

    private async Task ValidateStepsAsync(
        IReadOnlyList<WorkflowStepDocument> steps,
        string pathPrefix,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        IWorkflowValidationContext context,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? aliases = null,
        WorkflowDocumentScope scope = WorkflowDocumentScope.Workflow)
    {
        aliases ??= [];
        var inputRoots = new[] { "workflow", "previousStep" }.Concat(aliases).ToArray();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var stepPath = $"{pathPrefix}[{index}]";

            if (!Supports(scope, step))
            {
                diagnostics.Add(new("SCOPE001", $"{step.Kind} is not offered by the {scope} fluent builder", WorkflowDiagnosticSeverity.Error, stepPath));
                continue;
            }
            if (step.Name != null && scope != WorkflowDocumentScope.Workflow)
                diagnostics.Add(new("SCOPE002", $"Named is not offered by the {scope} fluent builder", WorkflowDiagnosticSeverity.Error, $"{stepPath}.name"));

            switch (step)
            {
                case ParallelStepDocument parallelStep:
                {
                    if ((parallelStep.Join == "conditionally") != (parallelStep.Condition != null))
                        Invalid("PAR001", "Only a conditional parallel join requires a condition", stepPath + ".condition", diagnostics);
                    ValidateExpression(parallelStep.Condition, diagnostics, stepPath + ".condition", ["workflow"], document.Code);
                    ValidateCombiners(parallelStep.Combine, stepPath + ".combine", diagnostics);
                    if (parallelStep.Branches != null)
                        foreach (var child in parallelStep.ChildScopes())
                            await ValidateStepsAsync(child.Steps, $"{stepPath}.branches[{child.Key}].steps", document, diagnostics, context, cancellationToken, aliases);
                    break;
                }
                case LoopStepDocument loop:
                {
                    ValidateExpression(loop.Condition, diagnostics, stepPath + ".condition", inputRoots, document.Code);
                    foreach (var child in loop.ChildScopes())
                        await ValidateStepsAsync(child.Steps, stepPath + ".steps", document, diagnostics, context, cancellationToken, aliases);
                    break;
                }
                case TryStepDocument attempt:
                {
                    if (attempt.Catches.Count == 0 && attempt.Finally == null)
                        Invalid("TRY001", "Try requires a catch or finally to complete the fluent chain", stepPath, diagnostics);
                    foreach (var child in attempt.ChildScopes())
                    {
                        if (child.Kind == "catch")
                        {
                            var handler = attempt.Catches[int.Parse(child.Key!, System.Globalization.CultureInfo.InvariantCulture)];
                            var handlerPath = $"{stepPath}.catches[{child.Key}]";
                            Fault(handler.Exception, handler.FaultType, handlerPath, diagnostics);
                            await ValidateStepsAsync(child.Steps, handlerPath + ".steps", document, diagnostics, context, cancellationToken, aliases, WorkflowDocumentScope.Catch);
                        }
                        else
                            await ValidateStepsAsync(child.Steps, stepPath + (child.Kind == "finally" ? ".finally" : ".steps"), document, diagnostics, context, cancellationToken, aliases);
                    }
                    break;
                }
                case SagaStepDocument saga:
                {
                    foreach (var child in saga.ChildScopes())
                    {
                        if (child.Kind == "sequence")
                        {
                            await ValidateStepsAsync(child.Steps, stepPath + ".steps", document, diagnostics, context, cancellationToken, aliases, WorkflowDocumentScope.Saga);
                            continue;
                        }
                        var handler = saga.OnError[int.Parse(child.Key!, System.Globalization.CultureInfo.InvariantCulture)];
                        var handlerPath = $"{stepPath}.onError[{child.Key}]";
                        Fault(handler.Exception, handler.FaultType, handlerPath, diagnostics);
                        if (handler.CompensateUpTo != null)
                        {
                            if (handler.Compensate != null)
                                Invalid("SAGA005", "compensate and compensateUpTo select different compensation strategies", handlerPath + ".compensateUpTo", diagnostics);
                            var activities = SagaActivities(saga.Steps, document).ToArray();
                            var matches = activities.Count(activity => activity == handler.CompensateUpTo);
                            if (matches != 1)
                                Invalid("SAGA011", $"Compensation target '{handler.CompensateUpTo}' must occur exactly once in this saga (found {matches})", handlerPath + ".compensateUpTo", diagnostics);
                        }
                        if (handler.Then != null && handler.Compensate == null && handler.CompensateUpTo == null)
                            Invalid("SAGA002", "A continuation requires a compensation choice", handlerPath + ".then", diagnostics);
                        if (handler.Retry != null && handler.Then != "retry")
                            Invalid("SAGA003", "Only a retry continuation accepts a retry policy", handlerPath + ".retry", diagnostics);
                        if (handler.IgnoreCompensationErrors && handler.Then != "continue")
                            Invalid("SAGA004", "ignoreCompensationErrors is available only for continue", handlerPath + ".ignoreCompensationErrors", diagnostics);
                        if (handler.Retry != null) ValidateRetry(handler.Retry, handlerPath + ".retry", diagnostics);
                        await ValidateStepsAsync(child.Steps, handlerPath + ".steps", document, diagnostics, context, cancellationToken, aliases, WorkflowDocumentScope.SagaHandler);
                    }
                    break;
                }
                case OutcomeStepDocument outcome:
                {
                    ValidateExpression(outcome.Value, diagnostics, stepPath + ".value", inputRoots, document.Code);
                    foreach (var child in outcome.ChildScopes())
                    {
                        var childIndex = int.Parse(child.Key!, System.Globalization.CultureInfo.InvariantCulture);
                        if (childIndex < outcome.Branches.Count)
                        {
                            ValidateExpression(outcome.Branches[childIndex].Value, diagnostics, $"{stepPath}.branches[{child.Key}].value", [], document.Code);
                            await ValidateStepsAsync(child.Steps, $"{stepPath}.branches[{child.Key}].steps", document, diagnostics, context, cancellationToken, aliases, WorkflowDocumentScope.Saga);
                        }
                        else
                            await ValidateStepsAsync(child.Steps, stepPath + ".default", document, diagnostics, context, cancellationToken, aliases, WorkflowDocumentScope.Saga);
                    }
                    break;
                }
                case InvokeWorkflowTypeStepDocument invocation:
                {
                    RequiredType(invocation.WorkflowType, stepPath + ".workflowType", diagnostics);
                    if (invocation.DataType != null) RequiredType(invocation.DataType, stepPath + ".dataType", diagnostics);
                    ValidateInputMappings(invocation.Input, diagnostics, stepPath + ".input", false, aliases, document.Code);
                    ValidateOutputMappings(invocation.Output, diagnostics, stepPath + ".output");
                    break;
                }
                case DelayStepDocument delay:
                {
                    Duration(delay.Duration, true, stepPath + ".duration", diagnostics);
                    break;
                }
                case ResultStepDocument result:
                {
                    ValidateExpression(result.Value, diagnostics, stepPath + ".value", inputRoots, document.Code);
                    break;
                }

                case ActivityStepDocument activityStep:
                    if (!string.IsNullOrWhiteSpace(activityStep.Activity))
                    {
                        var descriptor = await context.FindActivityAsync(activityStep.Activity, cancellationToken);
                        if (descriptor is null)
                        {
                            diagnostics.Add(new WorkflowDiagnostic("STEP003", $"Unknown activity '{activityStep.Activity}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.activity"));
                        }
                        else
                        {
                            ValidateActivityDependency(activityStep.ActivityVersion, descriptor, document, diagnostics, stepPath);
                        }
                    }

                    ValidateInputMappings(activityStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false,
                        scope is WorkflowDocumentScope.Catch or WorkflowDocumentScope.SagaHandler ? aliases.Append("fault").ToArray() : aliases, document.Code);
                    await ValidateSagaActivityAsync(activityStep, stepPath, document, diagnostics, context, cancellationToken, aliases, scope);
                    ValidateOutputMappings(activityStep.Output, diagnostics, $"{stepPath}.output");
                    break;

                case ActivityReferenceStepDocument activityReferenceStep:
                    if (!string.IsNullOrWhiteSpace(activityReferenceStep.Ref) && !document.Activities.ContainsKey(activityReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP005", $"Unknown activity template '{activityReferenceStep.Ref}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    break;

                case StepReferenceStepDocument stepReferenceStep:
                    if (!string.IsNullOrWhiteSpace(stepReferenceStep.Ref) && !document.Steps.ContainsKey(stepReferenceStep.Ref) && !await context.HasStepTemplateAsync(stepReferenceStep.Ref, cancellationToken))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP007", $"Unknown step template '{stepReferenceStep.Ref}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    else if (!HasMissingMember(stepPath + ".ref", diagnostics) && !document.Steps.ContainsKey(stepReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP012", $"External step template '{stepReferenceStep.Ref}' has no executable definition", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    break;

                case WaitStepDocument suspendStep:
                    if (!string.IsNullOrWhiteSpace(suspendStep.Event) && !await context.HasEventAsync(suspendStep.Event, cancellationToken))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP009", $"Unknown event '{suspendStep.Event}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.event"));
                    }

                    ValidateInputMappings(suspendStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false, aliases, document.Code);
                    await ValidateWaitAsync(suspendStep, stepPath, document, diagnostics, context, cancellationToken, aliases);
                    ValidateOutputMappings(suspendStep.Output, diagnostics, $"{stepPath}.output");
                    ValidateExpression(suspendStep.ResumeCondition, diagnostics, $"{stepPath}.resumeCondition", (scope == WorkflowDocumentScope.Saga ? new[] { "workflow", "previousStep", "event" } : new[] { "workflow", "event" }).Concat(aliases).ToArray(), document.Code);
                    break;

                case InvokeWorkflowStepDocument invokeWorkflowStep:
                    if (!HasMissingMember(stepPath + ".workflow", diagnostics) && !string.IsNullOrWhiteSpace(invokeWorkflowStep.Workflow.Name) && !await context.HasWorkflowAsync(invokeWorkflowStep.Workflow.Name, invokeWorkflowStep.Workflow.Version, cancellationToken))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP011", $"Workflow '{invokeWorkflowStep.Workflow.Name}' needs a resolved data contract and pinned version", WorkflowDiagnosticSeverity.Error, $"{stepPath}.workflow.name"));
                    }

                    ValidateInputMappings(invokeWorkflowStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false, aliases, document.Code);
                    ValidateOutputMappings(invokeWorkflowStep.Output, diagnostics, $"{stepPath}.output");
                    break;

                case ConditionalStepDocument conditionalStep:
                    ValidateExpression(conditionalStep.Condition, diagnostics, $"{stepPath}.condition", new[] { "workflow", "previousStep", "event" }.Concat(aliases).ToArray(), document.Code);
                    foreach (var child in conditionalStep.ChildScopes())
                        await ValidateStepsAsync(child.Steps, $"{stepPath}.{child.Kind}", document, diagnostics, context, cancellationToken, aliases);
                    break;

                case ForEachStepDocument each:
                    ValidateExpression(each.Items, diagnostics, $"{stepPath}.items", inputRoots, document.Code);
                    if (string.IsNullOrWhiteSpace(each.As) || aliases.Contains(each.As) ||
                        each.As is "workflow" or "previousStep" or "event" or "ctx" or "fault" or "currentStep" or "reply" or "answered" or "previousCompensation" ||
                        !System.Text.RegularExpressions.Regex.IsMatch(each.As, @"^[_\p{L}][_\p{L}\p{Nd}]*$"))
                        diagnostics.Add(new("EACH001", "ForEach alias must be a distinct identifier", WorkflowDiagnosticSeverity.Error, $"{stepPath}.as"));
                    var itemRoots = inputRoots.Append(each.As).ToArray();
                    ValidateExpression(each.Result, diagnostics, $"{stepPath}.result", itemRoots, document.Code);
                    if (each.Result != null && each.Steps.LastOrDefault() is ResultStepDocument)
                        Invalid("EACH007", "ForEach result conflicts with the trailing result step", $"{stepPath}.result", diagnostics);
                    if (each.Into != null)
                    {
                        ValidatePath(each.Into, diagnostics, $"{stepPath}.into", ["workflow"], "EACH002", "ForEach results must be stored in workflow data");
                        if (each.Result == null && each.Steps.LastOrDefault() is not ResultStepDocument)
                            diagnostics.Add(new("EACH003", "ForEach into requires a result", WorkflowDiagnosticSeverity.Error, $"{stepPath}.result"));
                    }
                    if (each.Parallel is { } parallel)
                    {
                        if (parallel.MaxConcurrency is < 1)
                            diagnostics.Add(new("EACH004", "maxConcurrency must be positive", WorkflowDiagnosticSeverity.Error, $"{stepPath}.parallel.maxConcurrency"));
                        if (parallel.Join == "conditionally" && (string.IsNullOrWhiteSpace(parallel.ResultType) || parallel.Condition == null))
                            diagnostics.Add(new("EACH006", "Conditional join requires resultType and condition", WorkflowDiagnosticSeverity.Error, $"{stepPath}.parallel"));
                        ValidateCombiners(parallel.Combine, $"{stepPath}.parallel.combine", diagnostics);
                        ValidateExpression(parallel.Condition, diagnostics, $"{stepPath}.parallel.condition", ["workflow", "results"], document.Code);
                    }
                    foreach (var child in each.ChildScopes())
                        await ValidateStepsAsync(child.Steps, $"{stepPath}.steps", document, diagnostics, context, cancellationToken, aliases.Append(each.As).ToArray());
                    break;

                case SequenceStepDocument sequenceStep:
                    foreach (var child in sequenceStep.ChildScopes())
                        await ValidateStepsAsync(child.Steps, $"{stepPath}.steps", document, diagnostics, context, cancellationToken, aliases);
                    break;
            }
            if (step is TryStepDocument or SagaStepDocument) scope = WorkflowDocumentScope.Workflow;
        }
    }

    private async Task ValidateActivityTemplateAsync(
        string templateKey,
        ActivityTemplateDocument template,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        IWorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(template.Activity))
        {
            var descriptor = await context.FindActivityAsync(template.Activity, cancellationToken);
            if (descriptor is null)
            {
                diagnostics.Add(new WorkflowDiagnostic("ACT002", $"Unknown activity '{template.Activity}'", WorkflowDiagnosticSeverity.Error, $"activities.{templateKey}.activity"));
            }
            else
            {
                ValidateActivityDependency(template.ActivityVersion, descriptor, document, diagnostics, $"activities.{templateKey}");
            }
        }

        ValidateInputMappings(template.Input, diagnostics, $"activities.{templateKey}.input", allowEventRoot: false, aliases: document.Steps.Values.SelectMany(block => block.Parameters.Keys).Distinct().ToArray(), code: document.Code);
        ValidateOutputMappings(template.Output, diagnostics, $"activities.{templateKey}.output");
    }

    private static void ValidateActivityDependency(
        string requestedVersion,
        ActivityDocumentContract descriptor,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        string path)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion) && string.IsNullOrWhiteSpace(descriptor.Version))
            Invalid("ACT003", $"Activity '{descriptor.Key}' version '{requestedVersion}' requires a resolved activity contract from publishing", $"{path}.activityVersion", diagnostics);
        else if (!string.IsNullOrWhiteSpace(requestedVersion) && requestedVersion != descriptor.Version)
        {
            diagnostics.Add(new WorkflowDiagnostic("ACT003",
                $"Activity '{descriptor.Key}' requires version '{descriptor.Version}' but the workflow declares '{requestedVersion}'",
                WorkflowDiagnosticSeverity.Error, $"{path}.activityVersion"));
        }

        if (!string.IsNullOrWhiteSpace(descriptor.PackageName) &&
            !string.IsNullOrWhiteSpace(descriptor.PackageVersion) &&
            !document.Imports.Catalogs.Contains($"{descriptor.PackageName}@{descriptor.PackageVersion}", StringComparer.Ordinal))
        {
            diagnostics.Add(new WorkflowDiagnostic("ACT004",
                $"Activity '{descriptor.Key}' requires package '{descriptor.PackageName}@{descriptor.PackageVersion}' in imports.catalogs",
                WorkflowDiagnosticSeverity.Error, "imports.catalogs"));
        }
    }

    private static void ValidateInputMappings(
        IReadOnlyList<InputMappingDocument> mappings,
        List<WorkflowDiagnostic> diagnostics,
        string pathPrefix,
        bool allowEventRoot,
        IReadOnlyCollection<string>? aliases = null,
        IReadOnlyDictionary<string, CodeAssetDocument>? code = null)
    {
        var allowedRoots = (allowEventRoot ? new[] { "workflow", "previousStep", "event" } : new[] { "workflow", "previousStep" }).Concat(aliases ?? []).ToArray();

        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            ValidateExpression(mapping.From, diagnostics, $"{pathPrefix}[{index}].from", allowedRoots, code);
        }
    }

    private static void ValidateOutputMappings(
        IReadOnlyList<OutputMappingDocument> mappings,
        List<WorkflowDiagnostic> diagnostics,
        string pathPrefix)
    {
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            ValidatePath(mapping.To, diagnostics, $"{pathPrefix}[{index}].to", new[] { "workflow" }, "MAP003", "Output mapping target path must start with 'workflow.'");
        }
    }

    private static void ValidateExpression(
        ExpressionDocument? expression,
        List<WorkflowDiagnostic> diagnostics,
        string path,
        IReadOnlyCollection<string> allowedRoots,
        IReadOnlyDictionary<string, CodeAssetDocument>? code = null)
    {
        if (HasMissingMember(path, diagnostics)) return;
        switch (expression)
        {
            case null:
                return;

            case PathExpressionDocument pathExpression:
                ValidatePath(pathExpression, diagnostics, path, allowedRoots, "EXPR001", $"Path expression root must be one of: {string.Join(", ", allowedRoots)}");
                break;

            case BinaryExpressionDocument binaryExpression:
                ValidateExpression(binaryExpression.Left, diagnostics, $"{path}.left", allowedRoots, code);
                ValidateExpression(binaryExpression.Right, diagnostics, $"{path}.right", allowedRoots, code);
                break;

            case FunctionExpressionDocument functionExpression:
                if (!HasMissingMember(path + ".function", diagnostics))
                {
                    if (code == null || !code.TryGetValue(functionExpression.Function, out var asset) ||
                        asset is not { Language: "csharp-expression" } || string.IsNullOrWhiteSpace(asset.Source))
                        diagnostics.Add(new WorkflowDiagnostic("EXPR003", $"Unknown function '{functionExpression.Function}'", WorkflowDiagnosticSeverity.Error, $"{path}.function"));
                }
                break;

            case not null and not ConstantExpressionDocument:
                diagnostics.Add(new WorkflowDiagnostic("EXPR004", $"Unsupported expression '{expression.GetType().Name}'", WorkflowDiagnosticSeverity.Error, path));
                break;
        }
    }

    private static void ValidatePath(
        PathExpressionDocument pathExpression,
        List<WorkflowDiagnostic> diagnostics,
        string path,
        IReadOnlyCollection<string> allowedRoots,
        string code,
        string message)
    {
        if (HasMissingMember(path, diagnostics)) return;
        if (string.IsNullOrWhiteSpace(pathExpression.Path))
        {
            diagnostics.Add(new WorkflowDiagnostic(code, "Path is required", WorkflowDiagnosticSeverity.Error, path));
            return;
        }

        var root = pathExpression.Path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root) || !allowedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new WorkflowDiagnostic(code, message, WorkflowDiagnosticSeverity.Error, path));
        }
    }

    internal static bool Supports(WorkflowDocumentScope scope, WorkflowStepDocument step) => scope switch
    {
        WorkflowDocumentScope.Saga => step is ActivityStepDocument or ActivityReferenceStepDocument or WaitStepDocument or OutcomeStepDocument,
        WorkflowDocumentScope.SagaHandler => step is ActivityStepDocument and not RequestStepDocument or ActivityReferenceStepDocument or WaitForStepDocument,
        WorkflowDocumentScope.Catch => step is not (RequestStepDocument or DelayStepDocument or ResultStepDocument or OutcomeStepDocument),
        _ => step is not OutcomeStepDocument
    };

    private async Task ValidateSagaActivityAsync(ActivityStepDocument step, string path, WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics, IWorkflowValidationContext context, CancellationToken token,
        IReadOnlyCollection<string> aliases, WorkflowDocumentScope scope)
    {
        if (step is RequestStepDocument request)
        {
            ValidateExpression(request.Subject, diagnostics, path + ".subject", new[] { "workflow", "previousStep" }.Concat(aliases).ToArray(), document.Code);
        }
        if ((step.Compensation.Count > 0 || step.OnError.Count > 0) && scope != WorkflowDocumentScope.Saga)
            Invalid("SAGA006", "Compensation and step error policies require a saga activity", path, diagnostics);
        for (var index = 0; index < step.Compensation.Count; index++)
        {
            var undo = step.Compensation[index];
            var undoPath = $"{path}.compensation[{index}]";
            if (!HasMissingMember(undoPath + ".activity", diagnostics) && await context.FindActivityAsync(undo.Activity, token) == null)
                Invalid("SAGA007", $"Unknown compensation activity '{undo.Activity}'", undoPath + ".activity", diagnostics);
            if (undo.ReplyType != null && undo.PreviousActivity != null)
                Invalid("SAGA008", "replyType and previousActivity select different compensation overloads", undoPath, diagnostics);
            if (undo.ReplyType != null) RequiredType(undo.ReplyType, undoPath + ".replyType", diagnostics);
            if (undo.PreviousActivity != null) RequiredType(undo.PreviousActivity, undoPath + ".previousActivity", diagnostics);
            if (undo.PreviousActivity == null && (undo.PreviousStepInput.Count > 0 || undo.PreviousCompensationInput.Count > 0))
                Invalid("SAGA009", "Previous-value mappings require previousActivity", undoPath, diagnostics);
            var roots = new[] { "workflow" }.Concat(aliases).Concat(undo.PreviousActivity != null ? [] : undo.ReplyType == null
                ? new[] { "currentStep" } : new[] { "currentStep", "reply", "answered" }).ToArray();
            ValidateContextMappings(undo.Input, undoPath + ".input", roots);
            ValidateContextMappings(undo.PreviousStepInput, undoPath + ".previousStepInput", ["previousStep"]);
            ValidateContextMappings(undo.PreviousCompensationInput, undoPath + ".previousCompensationInput", ["previousCompensation"]);
            ValidateOutputMappings(undo.Output, diagnostics, undoPath + ".output");
        }
        for (var index = 0; index < step.OnError.Count; index++)
        {
            var policy = step.OnError[index];
            var policyPath = $"{path}.onError[{index}]";
            if (policy.Exception != null) RequiredType(policy.Exception, policyPath + ".exception", diagnostics);
            if (policy.Retry != null && policy.Then != "retry")
                Invalid("SAGA010", "Only retry accepts a retry policy", policyPath + ".retry", diagnostics);
            if (policy.Retry != null) ValidateRetry(policy.Retry, policyPath + ".retry", diagnostics);
        }

        void ValidateContextMappings(IReadOnlyList<InputMappingDocument> mappings, string prefix, IReadOnlyCollection<string> roots)
        {
            for (var index = 0; index < mappings.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(mappings[index].Target))
                    Invalid("MAP001", "Input mapping target is required", $"{prefix}[{index}].target", diagnostics);
                ValidateExpression(mappings[index].From, diagnostics, $"{prefix}[{index}].from", roots, document.Code);
            }
        }
    }

    private async Task ValidateWaitAsync(WaitStepDocument wait, string path, WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics, IWorkflowValidationContext context, CancellationToken token, IReadOnlyCollection<string> aliases)
    {
        if (wait.Timeout != null)
        {
            Duration(wait.Timeout, true, path + ".timeout", diagnostics);
            if (wait.OnTimeout is not { Count: > 0 })
                Invalid("WAIT003", "A timeout requires a nonempty onTimeout path", path + ".onTimeout", diagnostics);
        }
        if (wait.OnTimeout != null)
        {
            if (wait.Timeout == null && wait.OnTimeout.Count > 0) Invalid("WAIT002", "onTimeout requires a timeout", path + ".onTimeout", diagnostics);
            foreach (var child in wait.ChildScopes())
                await ValidateStepsAsync(child.Steps, path + ".onTimeout", document, diagnostics, context, token, aliases);
        }
    }

    private static void ValidateCombiners(IReadOnlyList<JoinCombinerDocument> choices, string path,
        List<WorkflowDiagnostic> diagnostics)
    {
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            ValidatePath(choice.Property, diagnostics, $"{path}[{index}].property", ["workflow"], "JOIN001", "A combiner selects a workflow data property");
            RequiredType(choice.PropertyType, $"{path}[{index}].propertyType", diagnostics);
            RequiredType(choice.Combiner, $"{path}[{index}].combiner", diagnostics);
        }
    }

    private static void ValidateRetry(RetryPolicyDocument policy, string path, List<WorkflowDiagnostic> diagnostics)
    {
        Duration(policy.InitialInterval, false, path + ".initialInterval", diagnostics);
        Duration(policy.MaximumInterval, false, path + ".maximumInterval", diagnostics);
    }

    private static void Fault(string? exception, string? fault, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (exception != null) RequiredType(exception, path + ".exception", diagnostics);
        if (fault != null)
        {
            RequiredType(fault, path + ".faultType", diagnostics);
            if (exception == null) Invalid("FAULT001", "faultType requires an exception type", path + ".faultType", diagnostics);
        }
    }

    private static void RequiredType(string value, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) Invalid("TYPE001", "A CLR type or resolved catalog key is required", path, diagnostics);
    }

    private static void Duration(string value, bool positive, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (!TryDuration(value, positive, out _))
            Invalid("TIME001", positive ? "Timeout must be a positive invariant TimeSpan" : "Duration must be a nonnegative invariant TimeSpan", path, diagnostics);
    }

    internal static bool TryDuration(string value, bool positive, out TimeSpan duration) =>
        TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out duration) && (positive ? duration > TimeSpan.Zero : duration >= TimeSpan.Zero);

    private static bool HasMissingMember(string path, List<WorkflowDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic => diagnostic.Code == "MEMBER001" && diagnostic.Path is { } missing &&
            (path == missing || path.StartsWith(missing + ".", StringComparison.Ordinal) || path.StartsWith(missing + "[", StringComparison.Ordinal)));

    private static void Invalid(string code, string message, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (!HasMissingMember(path, diagnostics))
            diagnostics.Add(new(code, message, WorkflowDiagnosticSeverity.Error, path));
    }

    private static void ValidateMembers(object? value, string path, bool fromSource, List<WorkflowDiagnostic> diagnostics)
    {
        if (value is DocumentNode node)
        {
            foreach (var member in DocumentModel.GetMembers(node.GetType()))
            {
                var name = DocumentModel.GetName(member);
                var memberPath = path.Length == 0 ? name : path + "." + name;
                var child = member.GetValue(node);
                if (member.IsDefined(typeof(DocumentRequiredAttribute), true) &&
                    (fromSource && !node.Members.ContainsKey(name) || child is string text && string.IsNullOrWhiteSpace(text) || child == null))
                {
                    Invalid("MEMBER001", $"Missing '{name}'", memberPath, diagnostics);
                    continue;
                }
                var values = DocumentModel.GetValues(node.GetType(), member.Name);
                if (child is string choice && values.Count > 0 && !values.Contains(choice))
                    Invalid(((DocumentValuesAttribute)member.GetCustomAttributes(typeof(DocumentValuesAttribute), true).Single()).DiagnosticCode,
                        $"Unknown {name} '{choice}'; use {string.Join(", ", values)}", memberPath, diagnostics);
                ValidateMembers(child, memberPath, fromSource, diagnostics);
            }
        }
        else if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) ValidateMembers(entry.Value, path + "." + entry.Key, fromSource, diagnostics);
        }
        else if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++) ValidateMembers(list[index], $"{path}[{index}]", fromSource, diagnostics);
        }
    }

    private static IEnumerable<string> SagaActivities(IEnumerable<WorkflowStepDocument> steps, WorkflowDocument document)
    {
        foreach (var step in steps)
        {
            if (step is ActivityStepDocument activity) yield return activity.Activity;
            if (step is ActivityReferenceStepDocument reference && document.Activities.TryGetValue(reference.Ref, out var template))
                yield return template.Activity;
            if (step is OutcomeStepDocument outcome)
                foreach (var child in outcome.ChildScopes())
                    foreach (var activityType in SagaActivities(child.Steps, document))
                        yield return activityType;
        }
    }
}
