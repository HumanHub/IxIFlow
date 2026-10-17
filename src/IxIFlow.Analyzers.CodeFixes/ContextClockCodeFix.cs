using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Analyzers;

/// <summary>
/// Replaces <c>DateTime.UtcNow</c> or <c>DateTimeOffset.UtcNow</c> in workflow code with the workflow context's
/// <c>UtcNow</c>, the run's clock (D-42). Offered where the analyzer found a context parameter in scope.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ContextClockCodeFix)), Shared]
public sealed class ContextClockCodeFix : CodeFixProvider
{
    private const string Title = "Read the run's clock from the workflow context";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(Descriptors.ImpureExpression.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
            return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(Descriptors.ClockReplacement, out var replacement) ||
                replacement == null)
                continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            // A comment or directive inside the member access would be lost with it.
            if (node.DescendantTrivia().Any(trivia => trivia.Span.Length > 0 && node.Span.Contains(trivia.Span) &&
                                                      !trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
                                                      !trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
                continue;
            var document = context.Document;
            context.RegisterCodeFix(CodeAction.Create(Title, _ =>
            {
                var code = SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
                return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(node, code)));
            }, equivalenceKey: Title), diagnostic);
        }
    }
}
