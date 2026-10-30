using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

internal static class WorkflowStatuses
{
    internal static T Read<T>(T value) where T : struct, Enum => Require(value, WorkflowErrorKind.InvalidState);
    internal static T Write<T>(T value) where T : struct, Enum => Require(value, WorkflowErrorKind.InvalidRequest);

    private static T Require<T>(T value, WorkflowErrorKind kind) where T : struct, Enum => Enum.IsDefined(value)
        ? value
        : throw new WorkflowEngineException(kind, $"Undefined {typeof(T).Name} value '{value}'");

    internal static WorkflowStatus Read(string value) => Enum.TryParse<WorkflowStatus>(value, out var status)
        ? Read(status)
        : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"Undefined workflow status '{value}'");

    internal static WorkflowStatus? Read(WorkflowStatus? value) => value is { } status ? Read(status) : null;
}

// Keep the numeric JSON representation and reject the unused numbers in every saved or wire projection.
internal sealed class WorkflowStatusJsonConverter : StatusJsonConverter<WorkflowStatus>;

internal sealed class WorkflowExecutionStatusJsonConverter : StatusJsonConverter<WorkflowExecutionStatus>;

internal abstract class StatusJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var number))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"Invalid stored {typeof(T).Name}");

        return WorkflowStatuses.Read((T)Enum.ToObject(typeof(T), number));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Convert.ToInt32(WorkflowStatuses.Write(value)));
}