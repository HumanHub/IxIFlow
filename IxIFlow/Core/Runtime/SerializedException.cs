using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Saves constructor values only when the exception's type, core values, and public subclass
/// properties survive a round trip. Stack frames, Source, and public fields are not preserved.
/// </summary>
internal sealed class SerializedException
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRestorable { get; set; } = true;
    public string? RestorationFailureReason { get; set; }
    public List<ExceptionArgument> Arguments { get; set; } = [];
    public SerializedValue? Fault { get; set; }

    public static SerializedException FromForCatch(Exception error, Type catchType, Type faultType)
    {
        var fault = FaultProjection.For(catchType, faultType).Capture(error);
        return new SerializedException
        {
            Type = error.GetType().AssemblyQualifiedName!,
            Message = error.Message,
            IsRestorable = false,
            Fault = SerializedValue.From(fault)
        };
    }

    public static SerializedException From(Exception error)
    {
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
                Type = error.GetType().AssemblyQualifiedName!,
                Message = error.Message,
                Arguments = arguments
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
            Type = error.GetType().AssemblyQualifiedName!,
            Message = error.Message,
            IsRestorable = false,
            RestorationFailureReason = roundTripFailureReason ?? failureReason
        };
    }

    public Exception Restore()
    {
        if (!IsRestorable)
            throw new InvalidOperationException(
                $"Saved exception '{Type}' cannot be restored: {RestorationFailureReason ?? "constructor values were unavailable"}");
        var type = WorkflowTypeIdentity.Resolve(Type);
        if (type == null || !typeof(Exception).IsAssignableFrom(type))
            throw new InvalidOperationException($"Saved exception type '{Type}' is unavailable");

        var parameterTypes = Arguments.Select(argument => WorkflowTypeIdentity.Resolve(argument.Type)
            ?? throw new InvalidOperationException($"Saved constructor type '{argument.Type}' is unavailable"))
            .ToArray();
        var constructor = type.GetConstructor(parameterTypes)
            ?? throw new InvalidOperationException($"Saved constructor for '{Type}' is unavailable");
        var values = Arguments.Select((argument, index) => argument.InnerException != null
            ? argument.InnerException.Restore()
            : JsonSerializer.Deserialize(argument.Json, parameterTypes[index])).ToArray();
        return (Exception)constructor.Invoke(values);
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
                    Type = parameter.ParameterType.AssemblyQualifiedName!,
                    Json = value is Exception ? "null" : JsonSerializer.Serialize(value, parameter.ParameterType),
                    InnerException = value is Exception nested ? From(nested) : null
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

internal sealed class ExceptionArgument
{
    public string Type { get; set; } = "";
    public string Json { get; set; } = "null";
    public SerializedException? InnerException { get; set; }
}
