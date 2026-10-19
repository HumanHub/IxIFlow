using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The engine and the activities it runs log through <see cref="ILogger"/> inside scopes carrying the instance and,
/// for an activity attempt, its step and invocation, so entries from concurrent branches can be told apart (G-03).
/// </summary>
[Trait("Feature", "tracing")]
[Trait("Spec", "G-03")]
public sealed class LoggingScopeTests
{
    [Fact]
    public async Task EntriesCarryTheInstanceAndTheStepTheyWereWrittenFor()
    {
        var capture = new ScopeCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddTransient<LogsItsName>();
        services.AddSingleton<BothBranches>();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<ScopeData>("LoggingScopes")
            .Step<LogsItsName>(step => step.Input(activity => activity.Name).From(_ => "first"))
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<LogsItsName>(step => step.Input(activity => activity.Name).From(_ => "left")))
                .Do(branch => branch.Step<LogsItsName>(step => step.Input(activity => activity.Name).From(_ => "right"))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var ran = new[] { "first", "left", "right" }
            .Select(name => Assert.Single(capture.Entries, entry => entry.Message == $"ran {name}"))
            .ToArray();
        foreach (var entry in ran)
        {
            Assert.Equal(result.InstanceId, entry.Scope["InstanceId"]);
            Assert.Equal("LoggingScopes", entry.Scope["WorkflowName"]);
            Assert.False(string.IsNullOrEmpty(entry.Scope["StepId"] as string));
            Assert.False(string.IsNullOrEmpty(entry.Scope["InvocationId"] as string));
        }
        Assert.Equal(3, ran.Select(entry => entry.Scope["StepId"]).Distinct().Count());
        // Each entry carries its own step: the engine logs applying each activity's name input in the same scope.
        foreach (var (name, entry) in new[] { "first", "left", "right" }.Zip(ran))
            Assert.Contains(capture.Entries, applied => applied.Message == $"Applied typed input mapping: Name = {name}" &&
                                                         Equals(applied.Scope["StepId"], entry.Scope["StepId"]));

        // The engine's own entries for an attempt, from applying its inputs to applying its outputs, carry the same
        // scope.
        var engine = capture.Entries.Where(entry => entry.Category == typeof(ActivityExecutor).FullName).ToList();
        Assert.NotEmpty(engine);
        Assert.All(engine, entry =>
        {
            Assert.Equal(result.InstanceId, entry.Scope["InstanceId"]);
            Assert.Contains(entry.Scope["StepId"], ran.Select(item => item.Scope["StepId"]));
        });
    }

    public sealed class ScopeData;

    /// <summary>Holds the two branch activities until both run, so they log at the same time.</summary>
    public sealed class BothBranches
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                _both.TrySetResult();
            return _both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public sealed class LogsItsName(ILogger<LogsItsName> logger, BothBranches branches) : IAsyncActivity
    {
        public string Name { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            // The branch activities write their entries while both are running, each on whatever thread resumes it.
            if (Name != "first")
                await branches.ArriveAsync();
            await Task.Yield();
            logger.LogInformation("ran {Name}", Name);
        }
    }

    /// <summary>Records every entry with the values of the scopes it was written in, the innermost winning.</summary>
    private sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private void Add(string category, string message)
        {
            var scope = new Dictionary<string, object?>();
            _scopes.ForEachScope((state, values) =>
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs)
                        values[key] = value;
            }, scope);
            lock (_entries)
                _entries.Add(new Entry(category, message, scope));
        }

        private sealed class Logger(ScopeCapture capture, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => capture._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => capture.Add(category, formatter(state, exception));
        }
    }

    public sealed record Entry(string Category, string Message, IReadOnlyDictionary<string, object?> Scope);
}
