using Microsoft.CodeAnalysis;
using System.Collections;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IxIFlow.Dsl.Compilation;

public sealed partial class WorkflowDocumentValidator
{
    private static ExpressionSyntax ParseDocumentExpression(string source)
    {
        var tokens = SyntaxFactory.ParseTokens(source).Select(token => token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EventKeyword)
            ? SyntaxFactory.Identifier(token.LeadingTrivia, "@event", token.TrailingTrivia) : token);
        return SyntaxFactory.ParseExpression(string.Concat(tokens.Select(token => token.ToFullString())));
    }

    // Item aliases have their own lexical scope; template parameters are never iteration handles.
    private static void ValidateAssignmentIntrinsics(WorkflowDocument document, List<WorkflowDiagnostic> diagnostics)
    {
        foreach (var asset in document.Code.Keys.Where(name => name == "index"))
            Invalid("EXPR005", "index is reserved for iteration positions", "code." + asset, diagnostics);
        foreach (var template in document.Steps)
            if (template.Key == "index" || template.Value.Parameters.ContainsKey("index"))
                Invalid("EXPR005", "index is reserved for iteration positions", "steps." + template.Key, diagnostics);
        if (document.Activities.ContainsKey("index"))
            Invalid("EXPR005", "index is reserved for iteration positions", "activities.index", diagnostics);
        Walk(document.Definitions, "definitions", [], new HashSet<string>(StringComparer.Ordinal));

        void Walk(IReadOnlyList<WorkflowStepDocument>? steps, string path, IReadOnlyCollection<string> items, ISet<string> expanding)
        {
            if (steps == null) return;
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var at = path + "[" + index + "]";
                Own(step, at, items);
                if (step is StepReferenceStepDocument reference && document.Steps.TryGetValue(reference.Ref, out var template) && expanding.Add(reference.Ref))
                {
                    Walk(template.Steps, "steps." + reference.Ref + ".steps", items, expanding);
                    expanding.Remove(reference.Ref);
                }
                if (step is FlowchartStepDocument or StateMachineStepDocument)
                {
                    foreach (var child in step.GraphChildScopes()) Walk(child.Steps, at + child.Path, items, expanding);
                    continue;
                }
                foreach (var scope in step.ChildScopes())
                {
                    var childItems = step is ForEachStepDocument each && scope.Kind == "foreach" ? items.Append(each.As).ToArray() : items;
                    Walk(scope.Steps, ChildPath(step, at, scope.Kind, scope.Key), childItems, expanding);
                }
            }
        }

        void Own(object? node, string path, IReadOnlyCollection<string> items)
        {
            if (node is PathExpressionDocument expression) Check(expression.Path, path, items);
            if (node is FunctionExpressionDocument function && document.Code.TryGetValue(function.Function, out var asset))
                Check(asset.Source, path, items);
            if (node is DocumentNode documentNode)
                foreach (var member in DocumentModel.GetMembers(documentNode.GetType()))
                {
                    var value = member.GetValue(documentNode);
                    if (value is WorkflowStepDocument) continue;
                    if (value is IEnumerable sequence and not string)
                    {
                        var index = 0;
                        foreach (var child in sequence)
                        {
                            if (child is not WorkflowStepDocument) Own(child, path + "." + DocumentModel.GetName(member) + "[" + index + "]", items);
                            index++;
                        }
                    }
                    else Own(value, path + "." + DocumentModel.GetName(member), items);
                }
        }

        void Check(string source, string path, IReadOnlyCollection<string> items)
        {
            var syntax = ParseDocumentExpression(source);
            foreach (var call in syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                if (call.Expression is IdentifierNameSyntax { Identifier.ValueText: "index" } &&
                    (call.ArgumentList.Arguments.Count != 1 || call.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax alias ||
                     !items.Contains(alias.Identifier.ValueText)))
                    Invalid("EXPR005", "index(alias) requires one in-scope ForEach item alias", path, diagnostics);
        }
    }

}
