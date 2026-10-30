using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Authoring;

namespace IxIFlow.Core.Runtime;

/// <summary>Compiles a location once, retaining typed receivers and value-type write-back links.</summary>
internal static class AssignmentRules
{
    internal static WorkflowStep Step<TData, TPrevious>(LambdaExpression target, LambdaExpression value,
        Type valueType, string path, int line, int order) where TPrevious : class => new()
    {
        Name = "Assign", StepType = WorkflowStepType.Assign, Order = order,
        SourceLocation = path + ":" + line, WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(TPrevious),
        Runtime = WorkflowStepDelegates.Create<TData, TPrevious>(), Assignment = new(target, value, valueType)
    };

    internal static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in ParallelRules.Flatten(steps).Where(step => step.StepType == WorkflowStepType.Assign))
            WorkflowBuildError.Check(step, () =>
            {
                Compile(step);

                return true;
            });
    }

    private static void Compile(WorkflowStep step)
    {
        var assignment = step.Assignment ?? throw WorkflowBuildError.Invalid(step, "ASSIGN001: missing assignment");
        var target = assignment.Target ?? throw WorkflowBuildError.Invalid(step, "ASSIGN001: missing target");
        var value = assignment.Value ?? throw WorkflowBuildError.Invalid(step, "ASSIGN003: missing value");

        if (target.Parameters.Count != 1 || value.Parameters.Count != 1 || target.Parameters[0].Type != value.Parameters[0].Type)
            throw WorkflowBuildError.Invalid(step, "ASSIGN003: target and value require the same typed context");

        if (target.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked })
            throw WorkflowBuildError.Invalid(step, "ASSIGN003: a target cannot be converted to the value type; convert the value instead");

        var links = new List<Expression>();
        Expression? cursor = target.Body;

        while (cursor != null)
        {
            if (cursor is MemberExpression { Member.Name: "WorkflowData", Expression: ParameterExpression parameter } &&
                parameter == target.Parameters[0] && cursor.Type == step.WorkflowDataType)
                break;

            links.Add(cursor);

            cursor = cursor switch
            {
                MemberExpression member => member.Expression,
                IndexExpression index => index.Object,
                BinaryExpression { NodeType: ExpressionType.ArrayIndex } array => array.Left,
                MethodCallExpression { Method.Name: "get_Item", Object: not null } index => index.Object,
                _ => null
            };
        }

        if (cursor == null || links.Count == 0 || cursor.Type.IsValueType)
            throw WorkflowBuildError.Invalid(step, "ASSIGN001: to must be a property or indexed location rooted at WorkflowData");

        var contextType = step.LexicalFaultType is { } fault
            ? typeof(FaultContext<,,>).MakeGenericType(cursor.Type, fault, step.PreviousStepDataType ?? typeof(object))
            : typeof(WorkflowContext<,>).MakeGenericType(cursor.Type, step.PreviousStepDataType ?? typeof(object));

        if (!target.Parameters[0].Type.IsAssignableFrom(contextType))
            throw WorkflowBuildError.Invalid(step, "ASSIGN001: the location context does not match its lexical workflow scope");

        if (target.ReturnType != assignment.ValueType || value.ReturnType != assignment.ValueType || target.Body.Type != value.Body.Type)
            throw WorkflowBuildError.Invalid(step, "ASSIGN003: value is incompatible with the target type");

        links.Reverse();
        var written = AssignmentLocationChain.WrittenLinks(links, link => link.Type.IsValueType).ToArray();
        var context = Expression.Parameter(typeof(object), "context");
        var typedContext = Expression.Convert(context, target.Parameters[0].Type);
        var replace = new Replace(target.Parameters[0], typedContext);
        var variables = new List<ParameterExpression>();
        var actions = new List<Expression>();
        var locations = new List<Expression>();
        var receivers = new List<ParameterExpression>();
        Expression receiver = Save(replace.Visit(cursor)!);

        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var write = written.Contains(index);
            Expression location;

            if (link is MemberExpression member)
            {
                if (member.Member is not PropertyInfo property || property.GetMethod is not { IsPublic: true, IsStatic: false } ||
                    write && property.SetMethod is not { IsPublic: true, IsStatic: false } ||
                    write && property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                        .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
                    throw WorkflowBuildError.Invalid(step, $"ASSIGN002: '{member.Member.Name}' requires public writable property storage");

                BranchData.ValidateAssignmentProperty(receiver.Type, property, write, index == links.Count - 1);
                location = Expression.Property(receiver, property);
            }
            else
            {
                if (index == links.Count - 1)
                    BranchData.ValidateAssignmentValue(link.Type);

                var arguments = link switch
                {
                    IndexExpression indexed => indexed.Arguments.ToArray(),
                    MethodCallExpression call => call.Arguments.ToArray(),
                    BinaryExpression array => new[] { array.Right },
                    _ => throw WorkflowBuildError.Invalid(step, "ASSIGN002: unsupported location segment")
                };

                var indexes = arguments.Select(argument => Save(replace.Visit(argument)!)).Cast<Expression>().ToArray();

                if (receiver.Type.IsArray)
                    location = Expression.ArrayAccess(receiver, indexes);
                else
                {
                    var property = link is IndexExpression indexed
                        ? indexed.Indexer
                        : receiver.Type.GetProperties()
                            .FirstOrDefault(property => property.GetMethod == ((MethodCallExpression)link).Method);

                    var supported = receiver.Type.GetInterfaces().Append(receiver.Type).Any(type => type.IsGenericType &&
                        (type.GetGenericTypeDefinition() == typeof(IList<>) || type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                            !write && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                                type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))));

                    if (!supported || property?.GetMethod is not { IsPublic: true } ||
                        write && property.SetMethod is not { IsPublic: true })
                        throw WorkflowBuildError.Invalid(step, "ASSIGN002: [] requires a writable array, list or dictionary indexer");

                    location = Expression.MakeIndex(receiver, property, indexes);
                }
            }

            locations.Add(location);

            if (index < links.Count - 1)
            {
                var saved = Save(location);
                receivers.Add(saved);
                receiver = saved;
            }
        }

        var rhs = Save(new Replace(value.Parameters[0], typedContext).Visit(value.Body)!);
        actions.Add(Expression.Assign(locations[^1], rhs));

        foreach (var index in written.Skip(1))
            actions.Add(Expression.Assign(locations[index], receivers[index]));

        actions.Add(Expression.Empty());
        assignment.Apply = Expression.Lambda<Action<object>>(Expression.Block(variables, actions), context).Compile();
        assignment.TargetBehavior = target.Compile();
        assignment.ValueBehavior = value.Compile();

        ParameterExpression Save(Expression expression)
        {
            var saved = Expression.Variable(expression.Type, "location" + variables.Count);
            variables.Add(saved);
            actions.Add(Expression.Assign(saved, expression));

            return saved;
        }
    }

    private sealed class Replace(ParameterExpression source, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? replacement : node;
    }
}