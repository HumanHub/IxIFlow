namespace IxIFlow.Core.Runtime;

// An execution lifetime may retain work without depending on the host that owns it.
internal static class ExecutionWork
{
    internal static readonly AsyncLocal<Action<Task>?> Retainer = new();
    internal static void Retain(Task task) => Retainer.Value?.Invoke(task);
}