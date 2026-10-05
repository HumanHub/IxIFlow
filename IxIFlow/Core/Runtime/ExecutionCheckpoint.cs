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
    public SerializedException? UnhandledError { get; set; }
    public bool CancellationRequested { get; set; }

    [JsonIgnore]
    public Exception? RuntimeUnhandledError { get; set; }

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
    Cancelling,
    Completed,
    Cancelled,
    WaitingResolution
}

internal sealed class ContinuationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentJoinId { get; set; }
    public ContinuationStatus Status { get; set; } = ContinuationStatus.Active;
    public bool CancellationUnwind { get; set; }
    public List<ScopePosition> Stack { get; set; } = [];
    public SerializedValue? Previous { get; set; }
    public ActivityInvocationState? PendingActivity { get; set; }
    public AcceptedWaitState? AcceptedWait { get; set; }

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

/// <summary>
/// A committed Start for one logical activity invocation. The continuation cannot advance
/// until this invocation has a committed End or an explicit recovery decision.
/// </summary>
internal sealed class ActivityInvocationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public ActivityInputSnapshot Inputs { get; set; } = new();
    public SerializedValue? RecoveryState { get; set; }
    public List<ActivityAttemptState> Attempts { get; set; } = [];
    public string? ResolutionReason { get; set; }
}

internal sealed class ActivityAttemptState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public string Kind { get; set; } = "Execute";
    public string? Observation { get; set; }
}

internal sealed class ActivityInputSnapshot
{
    public Dictionary<string, ActivityInputValue> Properties { get; set; } = [];

    public IReadOnlyCollection<string> CodeInputs => Properties
        .Where(item => item.Value.RebindFromDefinition)
        .Select(item => item.Key).ToArray();

    public static ActivityInputSnapshot Capture(IAsyncActivity activity, WorkflowStep step)
    {
        var snapshot = new ActivityInputSnapshot();
        foreach (var mapping in step.InputMappings.Where(item =>
                     item.Direction == PropertyMappingDirection.Input))
        {
            var property = activity.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new InvalidOperationException(
                    $"Mapped activity input '{mapping.TargetProperty}' was not found");
            var value = property.GetValue(activity);
            snapshot.Properties[property.Name] = value switch
            {
                Delegate => new ActivityInputValue { RebindFromDefinition = true },
                Exception error => new ActivityInputValue
                {
                    Exception = SerializedException.From(error)
                },
                _ => new ActivityInputValue { Value = SerializedValue.From(value) }
            };
        }
        return snapshot;
    }

    public void Restore(IAsyncActivity activity, IServiceProvider services)
    {
        foreach (var (name, saved) in Properties)
        {
            if (saved.RebindFromDefinition)
                continue;
            var property = activity.GetType().GetProperty(name)
                ?? throw new InvalidOperationException(
                    $"Saved activity input '{activity.GetType().Name}.{name}' is unavailable");
            if (property.GetSetMethod(nonPublic: true) == null)
                throw new InvalidOperationException(
                    $"Saved activity input '{activity.GetType().Name}.{name}' cannot be restored");
            property.SetValue(activity, saved.Exception?.Restore() ?? saved.Value?.Read(services));
        }
    }
}

internal sealed class ActivityInputValue
{
    public SerializedValue? Value { get; set; }
    public SerializedException? Exception { get; set; }
    public bool RebindFromDefinition { get; set; }
}

internal sealed class ScopePosition
{
    public string ScopeId { get; set; } = "";
    public string ActivationId { get; set; } = Guid.NewGuid().ToString("N");
    public int NextStepIndex { get; set; }
    public int LoopIterationCount { get; set; }
    public Dictionary<int, int> StepRetryCounts { get; set; } = [];
    public TryScopeState? TryState { get; set; }
    public SagaScopeState? SagaState { get; set; }
    public SerializedValue? EntryPrevious { get; set; }
    public bool RestorePreviousOnExit { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }
}

internal enum TryPhase
{
    Try,
    Catch,
    Finally
}

internal sealed class TryScopeState
{
    public TryPhase Phase { get; set; } = TryPhase.Try;
    public int CatchIndex { get; set; } = -1;
    public bool IsCancellationCleanup { get; set; }
    public SerializedValue? EntryPrevious { get; set; }
    public SerializedException? Error { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }

    [JsonIgnore]
    public Exception? RuntimeError { get; set; }

    public Exception? GetError() => RuntimeError ??= Error?.Restore();

    public void SetError(Exception error)
    {
        RuntimeError = error;
        Error = SerializedException.From(error);
    }
}

internal enum SagaPhase
{
    Forward,
    Compensating
}

internal sealed class SagaScopeState
{
    public SagaPhase Phase { get; set; }
    public List<SagaCompletedStep> CompletedSteps { get; set; } = [];
    public int CompensationCursor { get; set; } = -1;
    public int CompensationFloor { get; set; }
    public int ErrorHandlerIndex { get; set; } = -1;
    public SagaContinuationAction? ErrorAction { get; set; }
    public int RetryCount { get; set; }
    public int MaximumRetries { get; set; }
    public List<SagaAcceptedWait> AcceptedWaits { get; set; } = [];
    public int AcceptedWaitCursor { get; set; }
    public SerializedException? Error { get; set; }
    public List<string> CompensationErrors { get; set; } = [];
    public bool IsCancellationCleanup { get; set; }
    public SerializedValue? PreviousCompensation { get; set; }

    [JsonIgnore]
    public Exception? RuntimeError { get; set; }

    public void SetError(Exception error)
    {
        RuntimeError = error;
        Error = SerializedException.From(error);
    }

    public Exception? GetError() => RuntimeError ??= Error?.Restore();
}

internal sealed class SagaAcceptedWait
{
    public string StepId { get; set; } = "";
    public SerializedValue Event { get; set; } = null!;
}

internal sealed class SagaCompletedStep
{
    public string StepId { get; set; } = "";
    public SerializedValue? Output { get; set; }
    public SerializedValue? Previous { get; set; }
}

internal sealed class ParallelJoinState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public string ParentContinuationId { get; set; } = "";
    public List<string> ChildContinuationIds { get; set; } = [];
    public ParallelJoinMode Mode { get; set; }
    public bool IsCompleting { get; set; }
    public bool ResumeParentWithoutAdvance { get; set; }
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

internal sealed class AcceptedWaitState
{
    public string StepId { get; set; } = "";
    public SerializedValue Event { get; set; } = new();
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
        var type = WorkflowTypeIdentity.Resolve(Type)
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
                var options = new JsonSerializerOptions();
                var converterAttribute = property.GetCustomAttribute<JsonConverterAttribute>();
                if (converterAttribute != null)
                {
                    var converter = converterAttribute.ConverterType is { } converterType
                        ? Activator.CreateInstance(converterType) as JsonConverter
                        : converterAttribute.CreateConverter(property.PropertyType);
                    if (converter == null)
                        throw new InvalidOperationException(
                            $"Converter for saved activity output '{type.Name}.{property.Name}' is unavailable");
                    options.Converters.Add(converter);
                }
                var numberHandling = property.GetCustomAttribute<JsonNumberHandlingAttribute>();
                if (numberHandling != null)
                    options.NumberHandling = numberHandling.Handling;
                property.SetValue(activity, value.Value.Deserialize(property.PropertyType, options));
            }
            return activity;
        }
        return JsonSerializer.Deserialize(Json, type);
    }
}
