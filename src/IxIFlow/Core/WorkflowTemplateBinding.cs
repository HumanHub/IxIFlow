using System.Linq.Expressions;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;

namespace IxIFlow.Core;

public sealed record WorkflowTemplatePin(string Key, int Version, string ModelTypeId, string Entry);
public sealed record WorkflowTemplateBinding(string Step, IReadOnlyList<TemplateReference> Templates);

/// <summary>One scanner for publishing and cold reconstruction, including synthesized compensation sites.</summary>
internal static class TemplateBindings
{
    internal static IReadOnlyList<WorkflowTemplateBinding> Scan(WorkflowDefinition definition,
        Action<TemplateReference, Type?>? validate = null)
    {
        var result = new List<WorkflowTemplateBinding>();
        foreach (var (site, step) in WorkflowDefinitionValidator.ForExecution(definition).AllSteps)
        {
            var references = new HashSet<TemplateReference>();
            foreach (var mapping in step.InputMappings.Where(mapping => mapping.TargetType == typeof(TemplateReference)))
            {
                var expression = mapping.Expression;
                if (expression == null || UsesParameters(expression))
                    throw Refusal("TPL013", $"Template input at '{site}' must be a constant in a published workflow");
                TemplateReference reference;
                try
                {
                    reference = Expression.Lambda<Func<TemplateReference>>(Expression.Convert(expression.Body, typeof(TemplateReference))).Compile()()
                        ?? throw Refusal("TPL013", $"Template input at '{site}' is null");
                }
                catch (WorkflowEngineException) { throw; }
                catch (Exception error) { throw Refusal("TPL013", $"Template constant at '{site}' could not be evaluated", error); }
                references.Add(reference);
            }
            var type = step.ActivityType;
            var model = type?.IsGenericType == true && type.GetGenericTypeDefinition().FullName == "IxIFlow.Templates.RenderTemplate`1"
                ? type.GenericTypeArguments[0] : null;
            foreach (var reference in references) validate?.Invoke(reference, model);
            if (references.Count > 0)
                result.Add(new(site, Array.AsReadOnly(references.OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray())));
        }
        return result.OrderBy(item => item.Step, StringComparer.Ordinal).ToArray();
    }

    internal static bool Same(IReadOnlyList<WorkflowTemplateBinding> left, IReadOnlyList<WorkflowTemplateBinding> right) =>
        left.Count == right.Count && left.OrderBy(item => item.Step, StringComparer.Ordinal).Zip(right.OrderBy(item => item.Step, StringComparer.Ordinal))
            .All(pair => pair.First.Step == pair.Second.Step && pair.First.Templates.ToHashSet().SetEquals(pair.Second.Templates));

    private static bool UsesParameters(LambdaExpression expression)
    {
        var visitor = new ParameterUse(expression.Parameters);
        visitor.Visit(expression.Body);
        return visitor.Found;
    }
    private sealed class ParameterUse(IEnumerable<ParameterExpression> parameters) : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _parameters = parameters.ToHashSet();
        internal bool Found { get; private set; }
        protected override Expression VisitParameter(ParameterExpression node)
        { Found |= _parameters.Contains(node); return node; }
    }
    internal static WorkflowEngineException Refusal(string code, string message, Exception? inner = null) =>
        new(WorkflowErrorKind.InvalidDefinition, code + ": " + message, inner);
}
