using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal sealed class ExecutionCheckpoint
{
    public string Runtime { get; set; } = "structured-v2";
    public int SchemaVersion { get; set; } = 2;
    public string DefinitionFingerprint { get; set; } = "";
    public List<ContinuationState> Continuations { get; set; } = [];
    public List<ParallelJoinState> Joins { get; set; } = [];
    public List<WaitState> Waits { get; set; } = [];

    public static ExecutionCheckpoint Read(string json)
    {
        var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(json);
        if (checkpoint?.Runtime != "structured-v2" || checkpoint.SchemaVersion != 2)
            throw new InvalidOperationException("Unsupported execution checkpoint version");
        return checkpoint;
    }

    public static bool IsStructured(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(Runtime), out var runtime) &&
            runtime.GetString() == "structured-v2";
    }
}

internal enum ContinuationStatus
{
    Active,
    Waiting,
    Joining,
    Completed,
    Cancelled
}

internal sealed class ContinuationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentJoinId { get; set; }
    public ContinuationStatus Status { get; set; } = ContinuationStatus.Active;
    public List<ScopePosition> Stack { get; set; } = [];
    public SerializedValue? Previous { get; set; }

    [JsonIgnore]
    public object? RuntimePrevious { get; set; }

    public object? GetPrevious(IServiceProvider services) =>
        RuntimePrevious ??= Previous?.Read(services);

    public void SetPrevious(object? value)
    {
        RuntimePrevious = value;
        Previous = null;
    }
}

internal sealed class ScopePosition
{
    public string ScopeId { get; set; } = "";
    public string ActivationId { get; set; } = Guid.NewGuid().ToString("N");
    public int NextStepIndex { get; set; }
    public int LoopIterationCount { get; set; }
    public SerializedValue? EntryPrevious { get; set; }
    public bool RestorePreviousOnExit { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }
}

internal sealed class ParallelJoinState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public string ParentContinuationId { get; set; } = "";
    public List<string> ChildContinuationIds { get; set; } = [];
    public ParallelJoinMode Mode { get; set; }
}

internal sealed class WaitState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContinuationId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Key { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class SerializedValue
{
    public string Type { get; set; } = "";
    public string Json { get; set; } = "";

    public static SerializedValue? From(object? value) => value == null
        ? null
        : new SerializedValue
        {
            Type = value.GetType().AssemblyQualifiedName!,
            Json = JsonSerializer.Serialize(value, value.GetType())
        };

    public object? Read(IServiceProvider services)
    {
        var type = System.Type.GetType(Type)
            ?? throw new InvalidOperationException($"Saved value type '{Type}' is unavailable");
        if (typeof(IAsyncActivity).IsAssignableFrom(type))
        {
            var activity = ActivatorUtilities.CreateInstance(services, type);
            using var document = JsonDocument.Parse(Json);
            foreach (var value in document.RootElement.EnumerateObject())
            {
                var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                        (candidate.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? candidate.Name) == value.Name);
                if (property?.GetSetMethod(nonPublic: true) == null)
                    throw new InvalidOperationException(
                        $"Saved activity output '{type.Name}.{value.Name}' cannot be restored");
                property.SetValue(activity, value.Value.Deserialize(property.PropertyType));
            }
            return activity;
        }
        return JsonSerializer.Deserialize(Json, type);
    }
}
