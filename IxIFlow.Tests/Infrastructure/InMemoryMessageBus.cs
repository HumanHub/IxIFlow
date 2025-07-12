using System.Collections.Concurrent;
using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// In-memory message bus implementation for testing distributed workflows
/// Mimics SQL-based message bus behavior without requiring database setup
/// </summary>
public class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentQueue<MessageEnvelope> _messages = new();
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<object>>> _subscribers = new();
    private readonly object _lock = new();
    private volatile bool _stopped = false;

    public async Task PublishAsync<T>(T message) where T : class
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (_stopped) return;

        var envelope = new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            MessageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name,
            Payload = System.Text.Json.JsonSerializer.Serialize(message),
            CreatedAt = DateTime.UtcNow,
            ProcessedAt = null,
            Priority = GetMessagePriority(message)
        };

        _messages.Enqueue(envelope);

        // Notify subscribers immediately
        var messageType = typeof(T).Name;
        if (_subscribers.TryGetValue(messageType, out var subscribers))
        {
            lock (_lock)
            {
                foreach (var subscriber in subscribers.ToList())
                {
                    try
                    {
                        subscriber.SetResult(message);
                    }
                    catch
                    {
                        // Ignore subscriber errors
                    }
                }
                subscribers.Clear();
            }
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
    {
        var messageType = typeof(T).Name;
        
        while (!_stopped)
        {
            // Check for existing messages first
            var existingMessages = GetUnprocessedMessages<T>();
            foreach (var message in existingMessages)
            {
                yield return message;
            }

            if (_stopped) yield break;

            // Wait for new messages
            var tcs = new TaskCompletionSource<object>();
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(messageType))
                {
                    _subscribers[messageType] = new List<TaskCompletionSource<object>>();
                }
                _subscribers[messageType].Add(tcs);
            }

            T? receivedMessage = null;
            try
            {
                // Wait with timeout to allow periodic checking
                var cancellationToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token;
                var result = await tcs.Task.WaitAsync(cancellationToken);
                if (result is T typedMessage)
                {
                    receivedMessage = typedMessage;
                }
            }
            catch (OperationCanceledException)
            {
                // Continue loop to check for stop condition
            }
            catch
            {
                // Handle other errors
                yield break;
            }

            if (receivedMessage != null)
            {
                yield return receivedMessage;
            }
        }
    }

    public async Task StopAsync()
    {
        _stopped = true;
        
        lock (_lock)
        {
            foreach (var subscriberList in _subscribers.Values)
            {
                foreach (var subscriber in subscriberList)
                {
                    subscriber.TrySetCanceled();
                }
            }
            _subscribers.Clear();
        }
        
        await Task.CompletedTask;
    }

    // Test helper methods
    public int GetMessageCount() => _messages.Count;

    public int GetMessageCount<T>() where T : class
    {
        var messageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name;
        return _messages.Count(m => m.MessageType == messageType);
    }

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }

    public List<T> GetAllMessages<T>() where T : class
    {
        var messageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name;
        var results = new List<T>();

        var allMessages = _messages.ToArray();
        foreach (var envelope in allMessages)
        {
            if (envelope.MessageType == messageType)
            {
                try
                {
                    var message = System.Text.Json.JsonSerializer.Deserialize<T>(envelope.Payload);
                    if (message != null)
                    {
                        results.Add(message);
                    }
                }
                catch
                {
                    // Skip invalid messages
                }
            }
        }

        return results;
    }

    private List<T> GetUnprocessedMessages<T>() where T : class
    {
        var messageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name;
        var results = new List<T>();

        var allMessages = _messages.ToArray();
        foreach (var envelope in allMessages)
        {
            if (envelope.MessageType == messageType && envelope.ProcessedAt == null)
            {
                try
                {
                    var message = System.Text.Json.JsonSerializer.Deserialize<T>(envelope.Payload);
                    if (message != null)
                    {
                        envelope.ProcessedAt = DateTime.UtcNow;
                        results.Add(message);
                    }
                }
                catch
                {
                    // Skip invalid messages
                }
            }
        }

        return results;
    }

    private static int GetMessagePriority<T>(T message)
    {
        return message switch
        {
            ExecuteWorkflowCommand cmd => cmd.Priority,
            CancelWorkflowCommand => 1000, // High priority for cancellations
            ResumeWorkflowCommand => 500,   // Medium priority for resumes
            _ => 0 // Default priority
        };
    }
}

/// <summary>
/// Message envelope for in-memory message bus
/// Mimics the structure that would be stored in SQL table
/// </summary>
public class MessageEnvelope
{
    public string Id { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Priority { get; set; }
}
