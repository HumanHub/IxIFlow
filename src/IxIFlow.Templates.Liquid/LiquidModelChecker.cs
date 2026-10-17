using Fluid;
using Fluid.Ast;
using Fluid.Values;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// Checks a parsed Liquid template against its model type when the template is published (H-03): every variable is
/// a model member, a loop variable or a variable the template assigns, every member read exists on its type, every
/// filter exists, and the template includes no other template. Where a value's type cannot be known, such as a
/// filter's result, reads from it are left to the render, which refuses any member outside the model.
/// </summary>
internal sealed class LiquidModelChecker
{
    // The members Fluid gives a for loop's forloop variable.
    private static readonly HashSet<string> ForLoopMembers =
        ["length", "index", "index0", "rindex", "rindex0", "first", "last"];

    private readonly TemplateOptions _options;
    private readonly List<TemplateDiagnostic> _diagnostics = [];
    private readonly List<Dictionary<string, Shape>> _scopes = [];

    private readonly Type _model;

    private LiquidModelChecker(TemplateOptions options, Type model)
    {
        _options = options;
        _model = model;
    }

    public static IReadOnlyList<TemplateDiagnostic> Check(IStatementList template, Type model, TemplateOptions options)
    {
        if (!LiquidModel.IsObject(model))
        {
            return [new TemplateDiagnostic(
                $"A Liquid template reads the members of its model, and '{TemplateVersion.ModelTypeName(model)}' " +
                "has none; wrap it in a model type")];
        }
        var checker = new LiquidModelChecker(options, model);
        checker._scopes.Add(LiquidModel.Members(model)
            .Where(member => member.Readable)
            .GroupBy(member => member.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Shape.Of(group.First().Type), StringComparer.Ordinal));
        checker.Statements(template.Statements);
        return checker._diagnostics;
    }

    private void Statements(IReadOnlyList<Statement>? statements)
    {
        if (statements == null)
            return;
        foreach (var statement in statements)
            Statement(statement);
    }

    private void Statement(Statement statement)
    {
        switch (statement)
        {
            case OutputStatement output:
                Expression(output.Expression);
                break;
            case AssignStatement assign:
                Assign(assign.Identifier, Expression(assign.Value));
                break;
            case CaptureStatement capture:
                Statements(capture.Statements);
                Assign(capture.Identifier, Shape.Of(typeof(string)));
                break;
            case ForStatement loop:
                var element = Elements(Expression(loop.Source));
                Expression(loop.Limit);
                // offset: continue resumes where the last loop over the same source stopped; it names no variable.
                if (!loop.OffsetIsContinue)
                    Expression(loop.Offset);
                Scoped(loop.Statements, (loop.Identifier, element), ("forloop", Shape.Named(ForLoopMembers)));
                Statements(loop.Else?.Statements);
                break;
            case TableRowStatement table:
                var row = Elements(Expression(table.Source));
                Expression(table.Limit);
                Expression(table.Offset);
                Expression(table.Cols);
                Scoped(table.Statements, (table.Identifier, row), ("tablerowloop", Shape.Unknown));
                break;
            case IfStatement branch:
                Expression(branch.Condition);
                Statements(branch.Statements);
                foreach (var other in branch.ElseIfs ?? [])
                {
                    Expression(other.Condition);
                    Statements(other.Statements);
                }
                Statements(branch.Else?.Statements);
                break;
            case UnlessStatement unless:
                Expression(unless.Condition);
                Statements(unless.Statements);
                Statements(unless.Else?.Statements);
                break;
            case CaseStatement choice:
                Expression(choice.Expression);
                foreach (var when in choice.Whens ?? [])
                {
                    foreach (var option in when.Options ?? [])
                        Expression(option);
                    Statements(when.Statements);
                }
                Statements(choice.Else?.Statements);
                break;
            case CycleStatement cycle:
                Expression(cycle.Group);
                foreach (var value in cycle.Values ?? [])
                    Expression(value);
                break;
            case IncludeStatement or RenderStatement:
                _diagnostics.Add(new TemplateDiagnostic(
                    "A template cannot include another template: each template version is self-contained"));
                break;
            case TagStatement tag:
                Statements(tag.Statements);
                break;
        }
    }

    // Fluid assigns into the template's outermost scope, so the variable outlives any loop around it. A variable
    // assigned twice, in branches the check cannot tell apart, has a known type only if both assignments agree.
    private void Assign(string name, Shape shape) =>
        _scopes[0][name] = _scopes[0].TryGetValue(name, out var earlier) && earlier != shape ? Shape.Unknown : shape;

    private void Scoped(IReadOnlyList<Statement> statements, params (string Name, Shape Shape)[] variables)
    {
        _scopes.Add(variables.ToDictionary(variable => variable.Name, variable => variable.Shape,
            StringComparer.Ordinal));
        Statements(statements);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private Shape Expression(Expression? expression)
    {
        switch (expression)
        {
            case MemberExpression member:
                return Member(member);
            case FilterExpression filter:
                var input = Expression(filter.Input);
                if (!_options.Filters.TryGetValue(filter.Name, out _))
                    _diagnostics.Add(new TemplateDiagnostic($"There is no filter named '{filter.Name}'"));
                else if (LiquidTemplateEngine.DateFilters.Contains(filter.Name) && input.Text is { } text &&
                         !LiquidTemplateEngine.TryParseDate(text, out _))
                    _diagnostics.Add(new TemplateDiagnostic(LiquidTemplateEngine.NotADate(text)));
                foreach (var argument in filter.Parameters ?? [])
                    Expression(argument.Expression);
                return Shape.Unknown;
            case BinaryExpression binary:
                Expression(binary.Left);
                Expression(binary.Right);
                return Shape.Unknown;
            case LiteralExpression { Value: StringValue text }:
                return Shape.Of(typeof(string)) with { Text = text.ToStringValue() };
            case RangeExpression range:
                Expression(range.From);
                Expression(range.To);
                return Shape.Unknown;
            default:
                return Shape.Unknown;
        }
    }

    private Shape Member(MemberExpression expression)
    {
        var segments = expression.Segments;
        if (segments.Count == 0 || segments[0] is not IdentifierSegment root)
        {
            foreach (var segment in segments)
                Segment(Shape.Unknown, segment, "");
            return Shape.Unknown;
        }
        var shape = Lookup(root.Identifier);
        var path = root.Identifier;
        if (shape == null)
        {
            _diagnostics.Add(LiquidModel.Member(_model, root.Identifier) is { Readable: false } member
                ? Unreadable("the model", member)
                : new TemplateDiagnostic(
                    $"'{root.Identifier}' is not a member of the model or a variable of the template"));
            shape = Shape.Unknown;
        }
        for (var index = 1; index < segments.Count; index++)
        {
            shape = Segment(shape, segments[index], path);
            path += segments[index] is IdentifierSegment identifier ? $".{identifier.Identifier}" : "[]";
        }
        return shape;
    }

    private Shape? Lookup(string name)
    {
        for (var index = _scopes.Count - 1; index >= 0; index--)
        {
            if (_scopes[index].TryGetValue(name, out var shape))
                return shape;
        }
        return null;
    }

    private Shape Segment(Shape shape, MemberSegment segment, string path)
    {
        switch (segment)
        {
            case IdentifierSegment identifier:
                return Read(shape, identifier.Identifier, path);
            case IndexerSegment indexer:
                Expression(indexer.Expression);
                if (shape.Type is { } type)
                {
                    if (LiquidModel.DictionaryValue(type) is { } value)
                        return Shape.Of(value);
                    if (LiquidModel.Element(type) is { } element)
                        return Shape.Of(element);
                    if (indexer.Expression is LiteralExpression { Value: StringValue name })
                        return Read(shape, name.ToStringValue(), path);
                }
                return Shape.Unknown;
            default:
                return Shape.Unknown;
        }
    }

    private Shape Read(Shape shape, string name, string path)
    {
        if (shape.Members is { } members)
        {
            if (members.Contains(name))
                return Shape.Unknown;
            _diagnostics.Add(new TemplateDiagnostic($"'{path}' has no member '{name}'"));
            return Shape.Unknown;
        }
        if (shape.Type is not { } type)
            return Shape.Unknown;
        if (type == typeof(string))
        {
            if (name is "size" or "first" or "last")
                return Shape.Unknown;
        }
        else if (LiquidModel.DictionaryValue(type) is { } value)
        {
            return name == "size" ? Shape.Unknown : Shape.Of(value);
        }
        else if (LiquidModel.Element(type) is { } element)
        {
            if (name == "size")
                return Shape.Unknown;
            if (name is "first" or "last")
                return Shape.Of(element);
        }
        else if (LiquidModel.IsObject(type) && LiquidModel.Member(type, name) is { } member)
        {
            if (member.Readable)
                return Shape.Of(member.Type);
            _diagnostics.Add(Unreadable(path, member));
            return Shape.Unknown;
        }
        _diagnostics.Add(new TemplateDiagnostic(
            $"'{path}' has no member '{name}': it is {TypeName(type)}"));
        return Shape.Unknown;
    }

    private static TemplateDiagnostic Unreadable(string owner, ModelMember member) =>
        new($"'{member.Name}' of {owner} cannot be read: it is typed " +
            $"'{TemplateVersion.ModelTypeName(member.Type)}', which could hold anything; give it a model type");

    private static Shape Elements(Shape source) =>
        source.Type is { } type && LiquidModel.Element(type) is { } element ? Shape.Of(element) : Shape.Unknown;

    private static string TypeName(Type type) =>
        type == typeof(object) ? "an object of unknown type" : $"a {TemplateVersion.ModelTypeName(type)}";

    /// <summary>
    /// What is known about a value: its .NET type, or the names of its members for a value Fluid makes itself, or
    /// nothing.
    /// </summary>
    private sealed record Shape(Type? Type, IReadOnlySet<string>? Members)
    {
        /// <summary>The text of a string written in the template, which a variable assigned it keeps.</summary>
        public string? Text { get; init; }

        public static readonly Shape Unknown = new(null, null);

        public static Shape Of(Type type) => new(LiquidModel.Unwrap(type), null);

        public static Shape Named(IReadOnlySet<string> members) => new(null, members);
    }
}
