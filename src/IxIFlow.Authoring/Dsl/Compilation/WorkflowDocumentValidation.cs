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

public sealed record WorkflowValidationContext(
    IActivityRegistry ActivityRegistry,
    IWorkflowCatalog WorkflowCatalog,
    IEventRegistry EventRegistry,
    IDataTypeRegistry DataTypeRegistry,
    IStepTemplateRegistry StepTemplateRegistry);

public interface IWorkflowDocumentValidator
{
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDocument document, WorkflowValidationContext context, CancellationToken cancellationToken = default);
}

public sealed class WorkflowDocumentValidator : IWorkflowDocumentValidator
{
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDocument document, WorkflowValidationContext context, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<WorkflowDiagnostic>();

        if (string.IsNullOrWhiteSpace(document.Workflow.Name))
        {
            diagnostics.Add(new WorkflowDiagnostic("WF001", "Workflow name is required", WorkflowDiagnosticSeverity.Error, "workflow.name"));
        }

        if (string.IsNullOrWhiteSpace(document.Workflow.DataType))
        {
            diagnostics.Add(new WorkflowDiagnostic("WF002", "Workflow data type is required", WorkflowDiagnosticSeverity.Error, "workflow.dataType"));
        }
        else if (await context.DataTypeRegistry.FindAsync(document.Workflow.DataType, cancellationToken) is null)
        {
            diagnostics.Add(new WorkflowDiagnostic("WF003", $"Unknown workflow data type '{document.Workflow.DataType}'", WorkflowDiagnosticSeverity.Error, "workflow.dataType"));
        }

        foreach (var activityTemplate in document.Activities)
        {
            await ValidateActivityTemplateAsync(activityTemplate.Key, activityTemplate.Value, document, diagnostics, context, cancellationToken);
        }

        foreach (var stepTemplate in document.Steps)
        {
            await ValidateStepsAsync(stepTemplate.Value.Steps, $"steps.{stepTemplate.Key}.steps", document, diagnostics, context, cancellationToken);
        }

        await ValidateStepsAsync(document.Definitions, "definitions", document, diagnostics, context, cancellationToken);

        return new WorkflowValidationResult(diagnostics);
    }

    private async Task ValidateStepsAsync(
        IReadOnlyList<WorkflowStepDocument> steps,
        string pathPrefix,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var stepPath = $"{pathPrefix}[{index}]";

            if (string.IsNullOrWhiteSpace(step.Id))
            {
                diagnostics.Add(new WorkflowDiagnostic("STEP001", "Step id is required", WorkflowDiagnosticSeverity.Error, $"{stepPath}.id"));
            }

            switch (step)
            {
                case ActivityStepDocument activityStep:
                    if (string.IsNullOrWhiteSpace(activityStep.Activity))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP002", "Activity step requires an activity key", WorkflowDiagnosticSeverity.Error, $"{stepPath}.activity"));
                    }
                    else
                    {
                        var descriptor = await context.ActivityRegistry.FindAsync(activityStep.Activity, cancellationToken);
                        if (descriptor is null)
                        {
                            diagnostics.Add(new WorkflowDiagnostic("STEP003", $"Unknown activity '{activityStep.Activity}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.activity"));
                        }
                        else
                        {
                            ValidateActivityDependency(activityStep.ActivityVersion, descriptor, document, diagnostics, stepPath);
                        }
                    }

                    ValidateInputMappings(activityStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false);
                    ValidateOutputMappings(activityStep.Output, diagnostics, $"{stepPath}.output");
                    break;

                case ActivityReferenceStepDocument activityReferenceStep:
                    if (string.IsNullOrWhiteSpace(activityReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP004", "Activity reference step requires a ref key", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    else if (!document.Activities.ContainsKey(activityReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP005", $"Unknown activity template '{activityReferenceStep.Ref}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    break;

                case StepReferenceStepDocument stepReferenceStep:
                    if (string.IsNullOrWhiteSpace(stepReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP006", "Step reference requires a ref key", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    else if (!document.Steps.ContainsKey(stepReferenceStep.Ref) && await context.StepTemplateRegistry.FindAsync(stepReferenceStep.Ref, cancellationToken) is null)
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP007", $"Unknown step template '{stepReferenceStep.Ref}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    else if (!document.Steps.ContainsKey(stepReferenceStep.Ref))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP012", $"External step template '{stepReferenceStep.Ref}' has no executable definition", WorkflowDiagnosticSeverity.Error, $"{stepPath}.ref"));
                    }
                    break;

                case SuspendStepDocument suspendStep:
                    if (string.IsNullOrWhiteSpace(suspendStep.Event))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP008", "Suspend step requires an event key", WorkflowDiagnosticSeverity.Error, $"{stepPath}.event"));
                    }
                    else if (await context.EventRegistry.FindAsync(suspendStep.Event, cancellationToken) is null)
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP009", $"Unknown event '{suspendStep.Event}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.event"));
                    }

                    ValidateInputMappings(suspendStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false);
                    ValidateOutputMappings(suspendStep.Output, diagnostics, $"{stepPath}.output");
                    ValidateExpression(suspendStep.ResumeCondition, diagnostics, $"{stepPath}.resumeCondition", new[] { "workflow", "event" });
                    break;

                case InvokeWorkflowStepDocument invokeWorkflowStep:
                    if (string.IsNullOrWhiteSpace(invokeWorkflowStep.Workflow.Name))
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP010", "Invoked workflow name is required", WorkflowDiagnosticSeverity.Error, $"{stepPath}.workflow.name"));
                    }
                    else if (await context.WorkflowCatalog.FindAsync(invokeWorkflowStep.Workflow.Name, invokeWorkflowStep.Workflow.Version, cancellationToken) is null)
                    {
                        diagnostics.Add(new WorkflowDiagnostic("STEP011", $"Unknown workflow '{invokeWorkflowStep.Workflow.Name}'", WorkflowDiagnosticSeverity.Error, $"{stepPath}.workflow.name"));
                    }

                    ValidateInputMappings(invokeWorkflowStep.Input, diagnostics, $"{stepPath}.input", allowEventRoot: false);
                    ValidateOutputMappings(invokeWorkflowStep.Output, diagnostics, $"{stepPath}.output");
                    break;

                case ConditionalStepDocument conditionalStep:
                    ValidateExpression(conditionalStep.Condition, diagnostics, $"{stepPath}.condition", new[] { "workflow", "previousStep", "event" });
                    await ValidateStepsAsync(conditionalStep.Then, $"{stepPath}.then", document, diagnostics, context, cancellationToken);
                    await ValidateStepsAsync(conditionalStep.Else, $"{stepPath}.else", document, diagnostics, context, cancellationToken);
                    break;

                case SequenceStepDocument sequenceStep:
                    await ValidateStepsAsync(sequenceStep.Steps, $"{stepPath}.steps", document, diagnostics, context, cancellationToken);
                    break;
            }
        }
    }

    private async Task ValidateActivityTemplateAsync(
        string templateKey,
        ActivityTemplateDocument template,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(template.Activity))
        {
            diagnostics.Add(new WorkflowDiagnostic("ACT001", "Activity template requires an activity key", WorkflowDiagnosticSeverity.Error, $"activities.{templateKey}.activity"));
        }
        else
        {
            var descriptor = await context.ActivityRegistry.FindAsync(template.Activity, cancellationToken);
            if (descriptor is null)
            {
                diagnostics.Add(new WorkflowDiagnostic("ACT002", $"Unknown activity '{template.Activity}'", WorkflowDiagnosticSeverity.Error, $"activities.{templateKey}.activity"));
            }
            else
            {
                ValidateActivityDependency(template.ActivityVersion, descriptor, document, diagnostics, $"activities.{templateKey}");
            }
        }

        ValidateInputMappings(template.Input, diagnostics, $"activities.{templateKey}.input", allowEventRoot: false);
        ValidateOutputMappings(template.Output, diagnostics, $"activities.{templateKey}.output");
    }

    private static void ValidateActivityDependency(
        string requestedVersion,
        ActivityDescriptor descriptor,
        WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics,
        string path)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.Version) && requestedVersion != descriptor.Version)
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
        bool allowEventRoot)
    {
        var allowedRoots = allowEventRoot ? new[] { "workflow", "previousStep", "event" } : new[] { "workflow", "previousStep" };

        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            if (string.IsNullOrWhiteSpace(mapping.Target))
            {
                diagnostics.Add(new WorkflowDiagnostic("MAP001", "Input mapping target is required", WorkflowDiagnosticSeverity.Error, $"{pathPrefix}[{index}].target"));
            }

            ValidateExpression(mapping.From, diagnostics, $"{pathPrefix}[{index}].from", allowedRoots);
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
            if (string.IsNullOrWhiteSpace(mapping.Source))
            {
                diagnostics.Add(new WorkflowDiagnostic("MAP002", "Output mapping source is required", WorkflowDiagnosticSeverity.Error, $"{pathPrefix}[{index}].source"));
            }

            ValidatePath(mapping.To, diagnostics, $"{pathPrefix}[{index}].to", new[] { "workflow" }, "MAP003", "Output mapping target path must start with 'workflow.'");
        }
    }

    private static void ValidateExpression(
        ExpressionDocument? expression,
        List<WorkflowDiagnostic> diagnostics,
        string path,
        IReadOnlyCollection<string> allowedRoots)
    {
        switch (expression)
        {
            case null:
                return;

            case PathExpressionDocument pathExpression:
                ValidatePath(pathExpression, diagnostics, path, allowedRoots, "EXPR001", $"Path expression root must be one of: {string.Join(", ", allowedRoots)}");
                break;

            case BinaryExpressionDocument binaryExpression:
                if (binaryExpression.Operator is not ("eq" or "ne" or "and" or "or"))
                {
                    diagnostics.Add(new WorkflowDiagnostic("EXPR002", $"Unknown binary operator '{binaryExpression.Operator}'", WorkflowDiagnosticSeverity.Error, $"{path}.operator"));
                }
                ValidateExpression(binaryExpression.Left, diagnostics, $"{path}.left", allowedRoots);
                ValidateExpression(binaryExpression.Right, diagnostics, $"{path}.right", allowedRoots);
                break;

            case FunctionExpressionDocument functionExpression:
                diagnostics.Add(new WorkflowDiagnostic("EXPR003", $"Unknown function '{functionExpression.Function}'", WorkflowDiagnosticSeverity.Error, $"{path}.function"));
                for (var index = 0; index < functionExpression.Arguments.Count; index++)
                {
                    ValidateExpression(functionExpression.Arguments[index], diagnostics, $"{path}.arguments[{index}]", allowedRoots);
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
        if (string.IsNullOrWhiteSpace(pathExpression.Path))
        {
            diagnostics.Add(new WorkflowDiagnostic(code, "Path is required", WorkflowDiagnosticSeverity.Error, path));
            return;
        }

        var root = pathExpression.Path.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root) || !allowedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new WorkflowDiagnostic(code, message, WorkflowDiagnosticSeverity.Error, path));
        }
    }
}
