using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Saves the public constructor values needed to rebuild a caught exception after a wait.
/// </summary>
internal sealed class SerializedException
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRestorable { get; set; } = true;
    public List<ExceptionArgument> Arguments { get; set; } = [];

    public static SerializedException From(Exception error)
    {
        var constructors = error.GetType().GetConstructors()
            .OrderByDescending(constructor => constructor.GetParameters().Length);
        foreach (var constructor in constructors)
        {
            if (!TryCaptureArguments(error, constructor, out var arguments))
                continue;
            return new SerializedException
            {
                Type = error.GetType().AssemblyQualifiedName!,
                Message = error.Message,
                IsRestorable = arguments.All(argument => argument.InnerException?.IsRestorable != false),
                Arguments = arguments
            };
        }
        return new SerializedException
        {
            Type = error.GetType().AssemblyQualifiedName!,
            Message = error.Message,
            IsRestorable = false
        };
    }

    public Exception Restore()
    {
        if (!IsRestorable)
            throw new InvalidOperationException(
                $"Saved exception '{Type}' cannot be restored because its constructor values were unavailable");
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
        out List<ExceptionArgument> arguments)
    {
        arguments = [];
        foreach (var parameter in constructor.GetParameters())
        {
            var property = error.GetType().GetProperty(parameter.Name!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property?.GetMethod == null || !parameter.ParameterType.IsAssignableFrom(property.PropertyType))
                return false;

            try
            {
                var value = property.GetValue(error);
                arguments.Add(new ExceptionArgument
                {
                    Type = parameter.ParameterType.AssemblyQualifiedName!,
                    Json = value is Exception ? "null" : JsonSerializer.Serialize(value, parameter.ParameterType),
                    InnerException = value is Exception nested ? From(nested) : null
                });
            }
            catch
            {
                arguments.Clear();
                return false;
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
