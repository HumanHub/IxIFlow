using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeAnalyzerTests
{
    private const string Scope = """
        public class Container : IScopeActivity<Stream> {
            public Task<Stream> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream());
            public Task CloseAsync(Stream value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
        }
        """;

    [Fact]
    public Task PublicValuePropertyWarns() => Analyzer<ScopeAnalyzer>(Scope + """
        public sealed class Resource : Container { public Stream {|#0:Session|} { get; set; } }
        """, Diagnostic(ScopeAnalyzer.ValueProperty, 0, "Resource.Session"));

    [Fact]
    public Task RequirementOnNonActivityIsRefused() => Analyzer<ScopeAnalyzer>("""
        [RequiresScope<Stream>] public sealed class {|#0:Data|} { }
        """, Diagnostic(ScopeAnalyzer.RequirementOwner, 0, "Data"));

    [Fact]
    public Task DualActivityInterfacesAreRefused() => Analyzer<ScopeAnalyzer>(Scope + """
        public sealed class {|#0:Dual|} : Container, IAsyncActivity {
            public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
        }
        """, Diagnostic(ScopeAnalyzer.DualActivity, 0, "Dual"));

    [Fact]
    public Task ScopeDelegateInputIsRefused() => Analyzer<ScopeAnalyzer>(Scope + """
        public sealed class Resource : Container { public Func<int> {|#0:Callback|} { get; set; } }
        """, Diagnostic(ScopeAnalyzer.DelegateInput, 0, "Resource.Callback"));

    [Fact]
    public Task InvalidThreadingIsRefused() => Analyzer<ScopeAnalyzer>(Scope + """
        [ScopeThreading((ScopeThreading)99)] public sealed class {|#0:Resource|} : Container { }
        """, Diagnostic(ScopeAnalyzer.InvalidThreading, 0, "Resource"));

    [Fact]
    public Task ValidIndependentModesAndRequirementAreAccepted() => Analyzer<ScopeAnalyzer>(Scope + """
        [ScopeThreading(ScopeThreading.ThreadAffine), ScopeConcurrency(ScopeConcurrency.Shared)] public sealed class Resource : Container { }
        [RequiresScope<Stream>] public sealed class Consumer : IAsyncActivity {
            public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
        }
        """);
}
