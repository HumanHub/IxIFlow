using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using System.Text.Json;

namespace IxIFlow.Dsl.Compilation;

public interface IWorkflowDocumentCompiler
{
    Task<WorkflowDefinition> CompileAsync(WorkflowDocument document, WorkflowValidationContext context, CancellationToken cancellationToken = default);
}

public sealed class WorkflowDocumentCompiler : IWorkflowDocumentCompiler
{
    private readonly IWorkflowDocumentValidator _validator;

    public WorkflowDocumentCompiler(IWorkflowDocumentValidator validator)
    {
        _validator = validator;
    }

    public async Task<WorkflowDefinition> CompileAsync(WorkflowDocument document, WorkflowValidationContext context, CancellationToken cancellationToken = default)
    {
        var validation = await _validator.ValidateAsync(document, context, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Diagnostics.Where(d => d.Severity == WorkflowDiagnosticSeverity.Error).Select(d => d.Message)));
        }

        var dataType = await context.DataTypeRegistry.FindAsync(document.Workflow.DataType, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow data type '{document.Workflow.DataType}' was not found during compilation");

        var definition = new WorkflowDefinition
        {
            Name = document.Workflow.Name,
            Version = document.Workflow.Version,
            Description = document.Workflow.Description ?? string.Empty,
            WorkflowDataType = dataType.ClrType,
            Tags = document.Workflow.Tags,
            CreatedAt = DateTime.UtcNow
        };

        var state = new CompilationState();
        definition.Steps.AddRange(await CompileStepsAsync(document.Definitions, document, context, dataType.ClrType, state, cancellationToken));

        definition.EstimatedStepCount = definition.Steps.Count;
        return definition;
    }

    private async Task<List<WorkflowStep>> CompileStepsAsync(
        IReadOnlyList<WorkflowStepDocument> steps,
        WorkflowDocument document,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        var compiledSteps = new List<WorkflowStep>();

        foreach (var step in steps)
        {
            compiledSteps.AddRange(await CompileStepAsync(step, document, context, workflowDataType, state, cancellationToken));
        }

        return compiledSteps;
    }

    private async Task<List<WorkflowStep>> CompileStepAsync(
        WorkflowStepDocument step,
        WorkflowDocument document,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ActivityStepDocument activityStep:
                return [await CompileActivityStepAsync(activityStep, context, workflowDataType, state, cancellationToken)];

            case ActivityReferenceStepDocument activityReferenceStep:
                return [await CompileActivityReferenceStepAsync(activityReferenceStep, document, context, workflowDataType, state, cancellationToken)];

            case StepReferenceStepDocument stepReferenceStep:
                return await CompileStepReferenceAsync(stepReferenceStep, document, context, workflowDataType, state, cancellationToken);

            case SequenceStepDocument sequenceStep:
                return await CompileStepsAsync(sequenceStep.Steps, document, context, workflowDataType, state, cancellationToken);

            case ConditionalStepDocument conditionalStep:
                return [new WorkflowStep
                {
                    Id = CreateStepId(conditionalStep.Id),
                    Name = conditionalStep.Name ?? conditionalStep.Id,
                    StepType = WorkflowStepType.Conditional,
                    WorkflowDataType = workflowDataType,
                    Order = state.NextOrder(),
                    CompiledCondition = CreateCondition(conditionalStep.Condition),
                    ThenSteps = await CompileStepsAsync(conditionalStep.Then, document, context, workflowDataType, state, cancellationToken),
                    ElseSteps = await CompileStepsAsync(conditionalStep.Else, document, context, workflowDataType, state, cancellationToken)
                }];

            case SuspendStepDocument suspendStep:
                return [await CompileSuspendStepAsync(suspendStep, context, workflowDataType, state, cancellationToken)];

            case InvokeWorkflowStepDocument invokeWorkflowStep:
                return [await CompileInvokeWorkflowStepAsync(invokeWorkflowStep, context, workflowDataType, state, cancellationToken)];

            default:
                throw new NotSupportedException($"Step kind '{step.GetType().Name}' cannot be compiled");
        }
    }

    private async Task<WorkflowStep> CompileActivityStepAsync(
        ActivityStepDocument activityStep,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        var descriptor = await context.ActivityRegistry.FindAsync(activityStep.Activity, cancellationToken)
            ?? throw new InvalidOperationException($"Activity '{activityStep.Activity}' was not found during compilation");

        return new WorkflowStep
        {
            Id = CreateStepId(activityStep.Id),
            Name = activityStep.Name ?? descriptor.Key,
            StepType = WorkflowStepType.Activity,
            ActivityType = descriptor.ActivityType,
            WorkflowDataType = workflowDataType,
            Order = state.NextOrder(),
            InputMappings = CompileInputMappings(activityStep.Input),
            OutputMappings = CompileOutputMappings(activityStep.Output)
        };
    }

    private async Task<WorkflowStep> CompileActivityReferenceStepAsync(
        ActivityReferenceStepDocument activityReferenceStep,
        WorkflowDocument document,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        var template = document.Activities[activityReferenceStep.Ref];
        var descriptor = await context.ActivityRegistry.FindAsync(template.Activity, cancellationToken)
            ?? throw new InvalidOperationException($"Activity '{template.Activity}' was not found during compilation");

        return new WorkflowStep
        {
            Id = CreateStepId(activityReferenceStep.Id),
            Name = activityReferenceStep.Name ?? descriptor.Key,
            StepType = WorkflowStepType.Activity,
            ActivityType = descriptor.ActivityType,
            WorkflowDataType = workflowDataType,
            Order = state.NextOrder(),
            InputMappings = CompileInputMappings(template.Input),
            OutputMappings = CompileOutputMappings(template.Output)
        };
    }

    private async Task<List<WorkflowStep>> CompileStepReferenceAsync(
        StepReferenceStepDocument stepReferenceStep,
        WorkflowDocument document,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        if (document.Steps.TryGetValue(stepReferenceStep.Ref, out var localTemplate))
        {
            return await CompileStepsAsync(localTemplate.Steps, document, context, workflowDataType, state, cancellationToken);
        }

        throw new NotSupportedException($"External step template '{stepReferenceStep.Ref}' cannot be compiled without a definition");
    }

    private async Task<WorkflowStep> CompileSuspendStepAsync(
        SuspendStepDocument suspendStep,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        var eventDescriptor = await context.EventRegistry.FindAsync(suspendStep.Event, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{suspendStep.Event}' was not found during compilation");

        return new WorkflowStep
        {
            Id = CreateStepId(suspendStep.Id),
            Name = suspendStep.Name ?? suspendStep.Reason,
            StepType = WorkflowStepType.SuspendResume,
            WorkflowDataType = workflowDataType,
            ResumeEventType = eventDescriptor.EventType,
            Order = state.NextOrder(),
            InputMappings = CompileInputMappings(suspendStep.Input),
            OutputMappings = CompileOutputMappings(suspendStep.Output),
            CompiledCondition = suspendStep.ResumeCondition == null ? null : CreateCondition(suspendStep.ResumeCondition),
            StepMetadata = new Dictionary<string, object>
            {
                ["SuspendReason"] = suspendStep.Reason
            }
        };
    }

    private async Task<WorkflowStep> CompileInvokeWorkflowStepAsync(
        InvokeWorkflowStepDocument invokeWorkflowStep,
        WorkflowValidationContext context,
        Type workflowDataType,
        CompilationState state,
        CancellationToken cancellationToken)
    {
        var workflowDescriptor = await context.WorkflowCatalog.FindAsync(invokeWorkflowStep.Workflow.Name, invokeWorkflowStep.Workflow.Version, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow '{invokeWorkflowStep.Workflow.Name}' was not found during compilation");

        return new WorkflowStep
        {
            Id = CreateStepId(invokeWorkflowStep.Id),
            Name = invokeWorkflowStep.Name ?? invokeWorkflowStep.Workflow.Name,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowDataType = workflowDataType,
            WorkflowName = workflowDescriptor.Name,
            WorkflowVersion = workflowDescriptor.Version,
            WorkflowType = null,
            Order = state.NextOrder(),
            InputMappings = CompileInputMappings(invokeWorkflowStep.Input),
            OutputMappings = CompileOutputMappings(invokeWorkflowStep.Output)
        };
    }

    private static List<PropertyMapping> CompileInputMappings(IReadOnlyList<InputMappingDocument> mappings)
    {
        return mappings.Select(mapping => new PropertyMapping
        {
            TargetProperty = mapping.Target,
            Direction = PropertyMappingDirection.Input,
            SourceType = typeof(object),
            TargetType = typeof(object),
            SourceFunction = CreateSourceFunction(mapping.From)
        }).ToList();
    }

    private static List<PropertyMapping> CompileOutputMappings(IReadOnlyList<OutputMappingDocument> mappings)
    {
        return mappings.Select(mapping => new PropertyMapping
        {
            TargetProperty = mapping.Source,
            Direction = PropertyMappingDirection.Output,
            SourceType = typeof(object),
            TargetType = typeof(object),
            SourceFunction = _ => null,
            TargetAssignmentFunction = CreateTargetAssignment(mapping.To)
        }).ToList();
    }

    private static Func<object, object?> CreateSourceFunction(ExpressionDocument expression)
    {
        return expression switch
        {
            ConstantExpressionDocument constant => _ => GetJsonValue(constant.Value),
            PathExpressionDocument path => context => ReadPathValue(context, path.Path),
            BinaryExpressionDocument binary => context => EvaluateBinary(binary.Operator,
                CreateSourceFunction(binary.Left)(context), CreateSourceFunction(binary.Right)(context)),
            _ => throw new NotSupportedException($"Expression kind '{expression.GetType().Name}' cannot be compiled")
        };
    }

    private static Func<object, bool> CreateCondition(ExpressionDocument expression)
    {
        var evaluate = CreateSourceFunction(expression);
        return context => evaluate(context) is bool result
            ? result
            : throw new InvalidOperationException("Condition expression must return a Boolean value");
    }

    private static object EvaluateBinary(string op, object? left, object? right)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => Equals(left, right),
            "ne" => !Equals(left, right),
            "and" when left is bool l && right is bool r => l && r,
            "or" when left is bool l && right is bool r => l || r,
            _ => throw new NotSupportedException($"Binary operator '{op}' cannot be evaluated for these operands")
        };
    }

    private static Action<object, object?> CreateTargetAssignment(PathExpressionDocument path)
    {
        return (context, value) => AssignPathValue(context, path.Path, value);
    }

    private static object? ReadPathValue(object source, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = segments[0].ToLowerInvariant() switch
        {
            "workflow" => source.GetType().GetProperty("WorkflowData")?.GetValue(source),
            "previousstep" => source.GetType().GetProperty("PreviousStep")?.GetValue(source)
                ?? source.GetType().GetProperty("PreviousStepData")?.GetValue(source),
            "event" => source.GetType().GetProperty("ResumeEvent")?.GetValue(source),
            _ => throw new InvalidOperationException($"Unknown path root in '{path}'")
        };
        foreach (var segment in segments.Skip(1))
        {
            if (current == null)
            {
                return null;
            }

            var property = current.GetType().GetProperty(segment)
                ?? throw new InvalidOperationException($"Property '{segment}' was not found in path '{path}'");
            current = property.GetValue(current);
        }

        return current;
    }

    private static void AssignPathValue(object target, string path, object? value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("workflow", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Output path '{path}' must start with workflow.");
        }

        object? current = target.GetType().GetProperty("WorkflowData")?.GetValue(target);
        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (current == null)
            {
                return;
            }

            current = current.GetType().GetProperty(segments[index])?.GetValue(current);
        }

        if (current == null || segments.Length == 0)
        {
            return;
        }

        var targetProperty = current.GetType().GetProperty(segments[^1]);
        if (targetProperty == null || !targetProperty.CanWrite)
        {
            return;
        }

        var convertedValue = ConvertValue(value, targetProperty.PropertyType);
        targetProperty.SetValue(current, convertedValue);
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value == null)
        {
            return null;
        }

        if (targetType.IsAssignableFrom(value.GetType()))
        {
            return value;
        }

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Convert.ChangeType(value, underlyingType);
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone()
        };
    }

    private static string CreateStepId(string stepId)
    {
        return string.IsNullOrWhiteSpace(stepId) ? Guid.NewGuid().ToString() : stepId;
    }

    private sealed class CompilationState
    {
        private int _order;

        public int NextOrder() => _order++;
    }
}
