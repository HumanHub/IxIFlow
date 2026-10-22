using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Saves constructor values only when the exception's type, core values, and public subclass
/// properties survive a round trip. Stack frames, Source, and public fields are not preserved.
/// </summary>
internal sealed class SerializedException
{
    public WorkflowFaultLocation? OriginalFaultLocation { get; set; }
    public bool HasDurableProperties { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    private Exception? LiveError { get; set; }

    internal static SerializedException Capture(Exception error, WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint, ContinuationState? continuation, bool newOccurrence)
    {
        var exceptionType = CatchType(error);
        var properties = scopes.FaultProperties(exceptionType).Concat(Hierarchy(exceptionType)
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(property => checkpoint.InvocationFaultProperties.Contains(FaultProjection.PropertyKey(property))))
            .DistinctBy(FaultProjection.PropertyKey).ToArray();
        if (!newOccurrence)
        {
            var known = From(error, checkpoint);
            // Invocation contracts were supplied at child start. Propagation never repairs a saved envelope.
            foreach (var property in properties)
                if (!known.Properties.ContainsKey(FaultProjection.PropertyKey(property)))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"Saved exception property '{property.Name}' is unavailable");
            return known;
        }
        var saved = Serialize(error, properties);
        saved.HasDurableProperties = true;
        saved.LiveError = error;
        saved.OriginalFaultLocation = Location(scopes, checkpoint, continuation);
        checkpoint.CapturedFaults[error] = saved;
        return saved;
    }

    private static IEnumerable<Type> Hierarchy(Type type)
    {
        for (Type? current = type; current != null; current = current.BaseType) yield return current;
    }

    internal static WorkflowFaultLocation? Location(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState? continuation)
    {
        var frame = continuation?.Stack.LastOrDefault();
        if (frame == null) return null;
        var step = scopes.Steps(frame.ScopeId).ElementAtOrDefault(frame.NextStepIndex);
        var attempt = continuation!.PendingActivity?.Attempts.LastOrDefault()?.Id ??
            InvocationRecords.Active(checkpoint, continuation)?.Attempts.LastOrDefault()?.Id;
        return new(step == null ? frame.ScopeId : scopes.Id(step), continuation!.Id, frame.ActivationId, attempt);
    }

    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRestorable { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPolicyTermination { get; set; }
    public string? RestorationFailureReason { get; set; }
    public List<ExceptionArgument> Arguments { get; set; } = [];
    public SerializedValue? Fault { get; set; }
    public string? FaultCatchType { get; set; }
    public Dictionary<string, SavedExceptionProperty> Properties { get; set; } = [];

    public static SerializedException FromForCatch(Exception error, Type catchType, Type faultType,
        ExecutionCheckpoint checkpoint)
    {
        var projection = FaultProjection.For(catchType, faultType);
        var saved = From(error, checkpoint);
        return saved.WithFault(projection.Capture(saved), catchType);
    }

    public static SerializedException From(Exception error, ExecutionCheckpoint checkpoint)
    {
        if (error is DurableWorkflowException carried)
            return carried.Saved;
        return checkpoint.CapturedFaults.TryGetValue(error, out var captured) ? captured
            : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "The failure has no captured occurrence");
    }

    private static SerializedException Serialize(Exception error, IReadOnlyCollection<PropertyInfo> faultProperties)
    {
        var properties = CaptureFaultProperties(error, faultProperties);
        var constructors = error.GetType().GetConstructors()
            .OrderByDescending(constructor => constructor.GetParameters().Length);
        var failureReason = "no public constructor can rebuild the exception";
        string? roundTripFailureReason = null;
        foreach (var constructor in constructors)
        {
            if (!TryCaptureArguments(error, constructor, out var arguments, out failureReason))
                continue;
            if (arguments.Any(argument => argument.InnerException?.IsRestorable == false))
            {
                failureReason = "an inner exception cannot be restored";
                roundTripFailureReason ??= failureReason;
                continue;
            }
            var saved = new SerializedException
            {
                IsPolicyTermination = error is SagaTerminatedException { IsPolicyTermination: true },
                Type = WorkflowTypeIdentity.SavedName(error.GetType()),
                Message = error.Message,
                Arguments = arguments,
                HasDurableProperties = faultProperties != null,
                Properties = properties
            };
            try
            {
                if (HasSameCatchState(error, saved.Restore(), out failureReason))
                    return saved;
                roundTripFailureReason ??= failureReason;
            }
            catch (Exception restoreFailure)
            {
                failureReason = $"constructor round trip failed: {restoreFailure.GetBaseException().Message}";
                roundTripFailureReason ??= failureReason;
            }
        }
        return new SerializedException
        {
            IsPolicyTermination = error is SagaTerminatedException { IsPolicyTermination: true },
            Type = WorkflowTypeIdentity.SavedName(error.GetType()),
            Message = error.Message,
            IsRestorable = false,
            HasDurableProperties = faultProperties != null,
            RestorationFailureReason = roundTripFailureReason ?? failureReason,
            Properties = properties
        };
    }

    // Exception-valued activity inputs are copied as values, rather than raised as failures.
    internal static SerializedException ForInputValue(Exception value) => Serialize(value, []);

    public Exception ForPropagation(ExecutionCheckpoint checkpoint)
    {
        var error = LiveError ?? (IsRestorable ? Restore() : new DurableWorkflowException(this));
        checkpoint.CapturedFaults[error] = this;
        return error;
    }

    public void Propagate(ExecutionCheckpoint checkpoint)
    {
        var error = ForPropagation(checkpoint);
        checkpoint.FailureBoundary?.Propagating(error);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static Type CatchType(Exception error) => error is DurableWorkflowException carried
        ? WorkflowTypeIdentity.Resolve(carried.Saved.Type) ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
            $"Saved exception type '{carried.Saved.Type}' is unavailable")
        : error.GetType();

    private SerializedException WithFault(object fault, Type catchType) => new()
    {
        OriginalFaultLocation = OriginalFaultLocation,
        HasDurableProperties = HasDurableProperties,
        LiveError = LiveError,
        IsPolicyTermination = IsPolicyTermination,
        Type = Type,
        Message = Message,
        IsRestorable = IsRestorable,
        RestorationFailureReason = RestorationFailureReason,
        Arguments = Arguments,
        Properties = Properties,
        Fault = SerializedValue.From(fault),
        FaultCatchType = WorkflowTypeIdentity.SavedName(catchType)
    };

    private static Dictionary<string, SavedExceptionProperty> CaptureFaultProperties(
        Exception error, IReadOnlyCollection<PropertyInfo>? properties)
    {
        var values = new Dictionary<string, SavedExceptionProperty>(StringComparer.Ordinal);
        if (properties == null)
            return values;
        foreach (var property in properties)
        {
            var key = FaultProjection.PropertyKey(property);
            if (values.ContainsKey(key))
                continue;
            try
            {
                if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                    throw new InvalidOperationException($"Exception property '{property.Name}' is unavailable");
                values[key] = new SavedExceptionProperty
                {
                    Json = JsonSerializer.Serialize(property.GetValue(error), property.PropertyType)
                };
            }
            catch (Exception captureError)
            {
                var failure = captureError;
                while (failure is TargetInvocationException { InnerException: { } inner }) failure = inner;
                if (WorkflowEngineException.IsEngineFailure(failure) || failure is ResourceLimitExceededException)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                values[key] = new SavedExceptionProperty
                {
                    Error = captureError.GetBaseException().Message
                };
            }
        }
        return values;
    }

    public Exception Restore()
    {
        if (!IsRestorable)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Saved exception '{Type}' cannot be restored: {RestorationFailureReason ?? "constructor values were unavailable"}");
        var type = WorkflowTypeIdentity.Resolve(Type);
        if (type == null || !typeof(Exception).IsAssignableFrom(type))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved exception type '{Type}' is unavailable");

        var parameterTypes = Arguments.Select(argument => WorkflowTypeIdentity.Resolve(argument.Type)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved constructor type '{argument.Type}' is unavailable"))
            .ToArray();
        var constructor = type.GetConstructor(parameterTypes)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved constructor for '{Type}' is unavailable");
        var values = Arguments.Select((argument, index) => argument.InnerException != null
            ? argument.InnerException.Restore()
            : JsonSerializer.Deserialize(argument.Json, parameterTypes[index])).ToArray();
        // A saved failure was raised by workflow code, so it stays that step's failure when restored.
        return WorkflowEngineException.FromWorkflowCode((Exception)constructor.Invoke(values));
    }

    private static bool TryCaptureArguments(Exception error, ConstructorInfo constructor,
        out List<ExceptionArgument> arguments, out string failureReason)
    {
        arguments = [];
        failureReason = "";
        foreach (var parameter in constructor.GetParameters())
        {
            try
            {
                var property = error.GetType().GetProperty(parameter.Name!,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property?.GetMethod == null || !parameter.ParameterType.IsAssignableFrom(property.PropertyType))
                {
                    failureReason = $"constructor parameter '{parameter.Name}' has no matching public property";
                    return false;
                }
                var value = property.GetValue(error);
                arguments.Add(new ExceptionArgument
                {
                    Type = WorkflowTypeIdentity.SavedName(parameter.ParameterType),
                    Json = value is Exception ? "null" : JsonSerializer.Serialize(value, parameter.ParameterType),
                    InnerException = value is Exception nested ? Serialize(nested, []) : null
                });
            }
            catch (Exception captureFailure)
            {
                arguments.Clear();
                failureReason = $"constructor parameter '{parameter.Name}' cannot be saved: {captureFailure.GetBaseException().Message}";
                return false;
            }
        }
        return true;
    }

    private static bool HasSameCatchState(Exception original, Exception restored, out string failureReason)
    {
        failureReason = "";
        if (original.GetType() != restored.GetType())
            failureReason = "the restored exception type changed";
        else if (original.Message != restored.Message)
            failureReason = "the exception message changed";
        else if (original.HResult != restored.HResult)
            failureReason = "the exception error code changed";
        else if (original.HelpLink != restored.HelpLink)
            failureReason = "the exception help link changed";
        else if (original.Data.Count != 0 || restored.Data.Count != 0)
            failureReason = "exception Data is outside the constructor checkpoint contract";
        else if ((original.InnerException == null) != (restored.InnerException == null))
            failureReason = "the inner exception changed";
        if (failureReason.Length != 0)
            return false;
        if (original.InnerException != null &&
            !HasSameCatchState(original.InnerException, restored.InnerException!, out failureReason))
            return false;

        // Check public state introduced by every subclass, including inherited custom exception classes.
        // Diagnostic stack frames and Source are intentionally outside this checkpoint contract.
        for (var type = original.GetType(); type != null && type != typeof(Exception); type = type.BaseType)
        {
            foreach (var property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (property.GetMethod?.IsPublic != true)
                    continue;
                if (property.GetIndexParameters().Length != 0)
                {
                    failureReason = $"public exception property '{property.Name}' is an indexer";
                    return false;
                }
                try
                {
                    var originalValue = property.GetValue(original);
                    var restoredValue = property.GetValue(restored);
                    if (originalValue?.GetType() != restoredValue?.GetType() ||
                        JsonSerializer.Serialize(originalValue, originalValue?.GetType() ?? property.PropertyType) !=
                        JsonSerializer.Serialize(restoredValue, restoredValue?.GetType() ?? property.PropertyType))
                    {
                        failureReason = $"public exception property '{property.Name}' changed after restoration";
                        return false;
                    }
                }
                catch (Exception propertyFailure)
                {
                    failureReason = $"public exception property '{property.Name}' cannot be verified: " +
                                    propertyFailure.GetBaseException().Message;
                    return false;
                }
            }
        }
        return true;
    }
}

internal sealed class SavedExceptionProperty
{
    public string? Json { get; set; }
    public string? Error { get; set; }
}

internal sealed class DurableWorkflowException(SerializedException saved) : Exception(saved.Message)
{
    public SerializedException Saved { get; } = saved;
}

internal sealed class ExceptionArgument
{
    public string Type { get; set; } = "";
    public string Json { get; set; } = "null";
    public SerializedException? InnerException { get; set; }
}
