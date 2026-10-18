using System.Globalization;
using System.Text;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.Core;

namespace IxIFlow.Generators;

/// <summary>Translates the shared workflow document into fluent C#.</summary>
public static class WorkflowSourceTranslator
{
    public static string Translate(string yaml, string sourcePath, WorkflowSourceOptions? options = null)
    {
        if (yaml is null) throw new ArgumentNullException(nameof(yaml));
        if (sourcePath is null) throw new ArgumentNullException(nameof(sourcePath));
        try { return Translate(WorkflowYamlSerializer.Read(yaml), sourcePath, options); }
        catch (YamlException error)
        {
            throw new WorkflowSourceException(error.Message, sourcePath, (int)error.Start.Line, (int)error.Start.Column);
        }
        catch (InvalidOperationException error)
        {
            throw new WorkflowSourceException(error.Message, sourcePath, 1, 1);
        }
    }

    public static string Translate(WorkflowDocument document, string sourcePath, WorkflowSourceOptions? options = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (sourcePath is null) throw new ArgumentNullException(nameof(sourcePath));
        if (sourcePath.IndexOf('"') >= 0 || sourcePath.IndexOf('\r') >= 0 || sourcePath.IndexOf('\n') >= 0)
            throw new WorkflowSourceException("A source path cannot contain quotes or newlines", sourcePath, 1, 1);
        return new Translation(document, sourcePath, options ?? new WorkflowSourceOptions()).Run();
    }

    public static string ClassName(string sourcePath) =>
        "Workflow_" + string.Concat(Path.GetFileNameWithoutExtension(sourcePath)
            .Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    internal static IEnumerable<string> TypeNames(WorkflowDocument document)
    {
        yield return document.Workflow.DataType;
        foreach (var activity in document.Activities.Values) yield return activity.Activity;
        foreach (var step in Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(template => template.Steps))))
            switch (step)
            {
                case ActivityStepDocument activity: yield return activity.Activity; break;
                case SuspendStepDocument wait: yield return wait.Event; break;
                case ForEachStepDocument { Parallel.ResultType: { } result }: yield return result; break;
            }
    }

    private static IEnumerable<WorkflowStepDocument> Flatten(IEnumerable<WorkflowStepDocument> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            var body = step switch
            {
                SequenceStepDocument sequence => sequence.Steps,
                ConditionalStepDocument condition => condition.Then.Concat(condition.Else),
                ForEachStepDocument each => each.Steps,
                _ => Enumerable.Empty<WorkflowStepDocument>()
            };
            foreach (var nested in Flatten(body)) yield return nested;
        }
    }

    private sealed class Translation(WorkflowDocument document, string path, WorkflowSourceOptions options)
    {
        private readonly StringBuilder _source = new();
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _templates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _arguments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
        private string? _templatePrefix;
        private string _workflowRoot = "ctx.WorkflowData";
        private int _next;

        public string Run()
        {
            if (document.SchemaVersion != "1.0") throw Error(document, $"Unsupported schema version '{document.SchemaVersion}'");
            foreach (var import in document.Imports.Catalogs.Concat(document.Imports.Workflows))
                if (!options.ResolvedImports.Contains(import))
                    throw Error(document.Imports, $"Import '{import}' requires publishing to resolve and pin its package or workflow");
            foreach (var activity in document.Activities.Values)
                ActivityVersion(activity.Activity, activity.ActivityVersion, activity);
            foreach (var activity in Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(template => template.Steps))).OfType<ActivityStepDocument>())
                ActivityVersion(activity.Activity, activity.ActivityVersion, activity);
            foreach (var parameter in document.Steps.Values.SelectMany(template => template.Parameters.Values))
            {
                ParameterType(parameter);
                if (parameter.Default != null) ParameterValue(parameter, Literal(parameter.Default, parameter), parameter.Default);
            }
            var workflow = document.Workflow;
            var name = Required(workflow.Name, workflow, "name");
            if (workflow.Version < 1) throw Error(workflow, "Workflow version must be a positive integer", "version");
            var data = Type(Required(workflow.DataType, workflow, "dataType"), workflow, "dataType");
            var className = options.ClassName ?? ClassName(path);
            if (!SyntaxFacts.IsValidIdentifier(className) || SyntaxFactory.ParseName(options.Namespace).ContainsDiagnostics)
                throw Error(workflow, "Invalid generated class or namespace");
            Line("// <auto-generated/>");
            Line("#nullable enable");
            Line("using System;");
            Line("using System.Linq;");
            Line("using IxIFlow.Builders;");
            Line("using IxIFlow.Core;");
            Line($"namespace {options.Namespace};");
            Line($"public static class {className}");
            Line("{");
            Line("    public static WorkflowDefinition Build()");
            Line("    {");
            var initial = document.Definitions.FirstOrDefault();
            Locate(workflow, "dataType");
            Line(initial is ActivityStepDocument or ActivityReferenceStepDocument
                ? $"var builder0 = Workflow.Create<{data}>({Quote(name)}, {workflow.Version});"
                : $"var builder0 = new WorkflowBuilder<{data}, object>(new(), {Quote(name)}, {workflow.Version});");
            var last = Steps(document.Definitions, "builder0", "root");
            Line("#line default");
            Line($"var definition = {last}.Build();");
            Line($"definition.Description = {Quote(workflow.Description ?? "")};");
            Line($"definition.Tags = new string[] {{ {string.Join(", ", workflow.Tags.Select(Quote))} }};");
            if (workflow.DisplayName != null) Line($"definition.Metadata[\"DisplayName\"] = {Quote(workflow.DisplayName)};");
            Line("return definition;");
            Line("    }");
            Line("}");
            return _source.ToString();
        }

        private void ActivityVersion(string key, string requested, DocumentNode node)
        {
            if (string.IsNullOrWhiteSpace(requested)) return;
            if (!options.ActivityVersions.TryGetValue(key, out var declared))
                throw Error(node, $"Activity '{key}' version '{requested}' requires a resolved activity contract from publishing", "activityVersion");
            if (requested != declared)
                throw Error(node, $"Activity '{key}' requires version '{declared}' but the workflow declares '{requested}'", "activityVersion");
        }

        private string ParameterType(TemplateParameterDocument parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter.Type)) return "";
            if (parameter.Type is not ("string" or "bool" or "int" or "long" or "double" or "decimal"))
                throw Error(parameter, $"Template parameter type '{parameter.Type}' requires publishing; local templates support string, bool, int, long, double and decimal", "type");
            return parameter.Type;
        }

        private string ParameterValue(TemplateParameterDocument parameter, string expression, object? constant)
        {
            var type = ParameterType(parameter);
            var valid = constant == null || type.Length == 0 || type switch
            {
                "string" => constant is string,
                "bool" => constant is bool,
                "int" => constant is int || constant is long number && number is >= int.MinValue and <= int.MaxValue,
                "long" => constant is int or long,
                "double" => constant is int or long or double,
                "decimal" => constant is int or long or decimal,
                _ => false
            };
            if (!valid) throw Error(parameter, $"Template argument or default does not match type '{type}'", "type");
            return type.Length == 0 ? expression : $"({type})({expression})";
        }

        private string Steps(IEnumerable<WorkflowStepDocument> steps, string builder, string scope)
        {
            var index = 0;
            foreach (var step in steps)
            {
                var id = string.IsNullOrEmpty(step.Id) ? scope + "/" + index :
                    _templatePrefix == null ? step.Id : _templatePrefix + "/" + step.Id;
                index++;
                if (string.IsNullOrWhiteSpace(id) || !_ids.Add(id)) throw Error(step, $"Invalid or duplicate step ID '{id}'");
                var next = "builder" + ++_next;
                Locate(step);
                switch (step)
                {
                    case ActivityStepDocument activity:
                        Activity(activity.Activity, activity.Defaults, activity.Input, activity.Output, activity, builder, next);
                        break;
                    case ActivityReferenceStepDocument reference:
                        if (!document.Activities.TryGetValue(reference.Ref, out var template)) throw Error(step, "Unknown activity template");
                        Activity(template.Activity, template.Defaults, template.Input, template.Output, template, builder, next);
                        break;
                    case SequenceStepDocument sequence:
                        Container(builder, next, id, sequence.Steps);
                        break;
                    case StepReferenceStepDocument stepReference:
                        if (!document.Steps.TryGetValue(stepReference.Ref, out var block))
                            throw Error(step, $"Unknown step template '{stepReference.Ref}'; external templates require publishing");
                        if (!_templates.Add(stepReference.Ref)) throw Error(step, $"Recursive step template '{stepReference.Ref}'");
                        var saved = new Dictionary<string, string>(_arguments);
                        var supplied = stepReference.Arguments.ToDictionary(pair => pair.Key, pair => Expression(pair.Value));
                        _arguments.Clear();
                        foreach (var parameter in block.Parameters)
                            if (supplied.TryGetValue(parameter.Key, out var value))
                                _arguments[parameter.Key] = ParameterValue(parameter.Value, value,
                                    (stepReference.Arguments[parameter.Key] as ConstantExpressionDocument)?.Value);
                            else if (parameter.Value.Default is { } fallback) _arguments[parameter.Key] = ParameterValue(parameter.Value, Literal(fallback, parameter.Value), fallback);
                            else if (parameter.Value.Required) throw Error(step, $"Missing template argument '{parameter.Key}'");
                        foreach (var pair in supplied)
                            if (!block.Parameters.ContainsKey(pair.Key)) throw Error(step, $"Unknown template argument '{pair.Key}'");
                        var savedPrefix = _templatePrefix;
                        _templatePrefix = id;
                        Container(builder, next, id, block.Steps);
                        _templatePrefix = savedPrefix;
                        _arguments.Clear();
                        foreach (var pair in saved) _arguments[pair.Key] = pair.Value;
                        _templates.Remove(stepReference.Ref);
                        break;
                    case ConditionalStepDocument condition:
                        Line($"var {next} = {builder}.If(ctx => {Expression(condition.Condition)}, then => {{");
                        Steps(condition.Then, "then", id + "/then");
                        Line("}, @else => {");
                        Steps(condition.Else, "@else", id + "/else");
                        Line("});");
                        break;
                    case ForEachStepDocument each:
                        ForEach(each, builder, next, id);
                        break;
                    case SuspendStepDocument wait:
                        var eventType = Type(Required(wait.Event, wait, "event"), wait, "event");
                        Locate(wait, "event");
                        Line($"var {next} = {builder}.Suspend<{eventType}>({Quote(string.IsNullOrEmpty(wait.Reason) ? id : wait.Reason)}, " +
                            (wait.ResumeCondition == null ? "null" : "(evt, ctx) => " + Expression(wait.ResumeCondition, "evt")) + ", setup => {");
                        Mappings(wait.Input, wait.Output);
                        Line("});");
                        break;
                    case InvokeWorkflowStepDocument invocation:
                        var childReference = invocation.Workflow;
                        var childName = Required(childReference.Name, childReference, "name");
                        var lookup = childName + (childReference.Version == null ? "" : "@" + childReference.Version);
                        if (!options.Workflows.TryGetValue(lookup, out var child))
                            throw Error(childReference, $"Workflow '{lookup}' needs a resolved data contract and pinned version");
                        if (childReference.Version != null && childReference.Version != child.Version)
                            throw Error(childReference, $"Workflow '{lookup}' resolves to a different version");
                        Line($"var {next} = {builder}.Invoke<{Type(child.DataType, childReference)}>({Quote(childName)}, {child.Version}, setup => {{");
                        Mappings(invocation.Input, invocation.Output);
                        Line("});");
                        break;
                    default: throw Error(step, $"Unsupported step kind '{step.Kind}'");
                }
                Line($"{next}.WithId({Quote(id)}, {Quote(path)}, {Math.Max(1, step.Location.Line)});");
                if (step.Name != null) Line($"{next}.Named({Quote(step.Name)});");
                builder = next;
            }
            return builder;
        }

        private void Container(string builder, string next, string id, IEnumerable<WorkflowStepDocument> children)
        {
            var body = "sequence" + _next;
            Line($"var {next} = {builder}.Sequence({body} => {{");
            Steps(children, body, id);
            Line("});");
        }

        private void ForEach(ForEachStepDocument step, string builder, string next, string id)
        {
            var selector = Expression(step.Items);
            var alias = step.As;
            if (_items.ContainsKey(alias) || alias is "workflow" or "previousStep" or "event" or "ctx")
                throw Error(step, $"Item alias '{alias}' is already in scope or reserved");
            Member(alias, step);
            var handle = "item" + _next;
            var body = "body" + _next;
            Line($"var {next} = {builder}.ForEach(itemsExpression: ctx => {selector}, ({body}, {handle}) => {{");
            _items.Add(alias, handle);
            var last = Steps(step.Steps, body, id + "/body");
            if (step.Result is { } result)
            {
                Locate(result);
                Line($"{last}.Result(valueExpression: ctx => {Expression(result)}).WithId({Quote(id + "/result")}, {Quote(path)}, {Math.Max(1, result.Location.Line)});");
            }
            _items.Remove(alias);
            Line("}, setup => {");
            Line($"setup.As({Quote(alias)});");
            if (step.Parallel is { } parallel)
            {
                if (parallel.MaxConcurrency is < 1) throw Error(parallel, "maxConcurrency must be a positive integer", "maxConcurrency");
                Line($"setup.Parallel({parallel.MaxConcurrency?.ToString(CultureInfo.InvariantCulture)});");
                switch (parallel.Join)
                {
                    case "all": break;
                    case "any": Line("setup.WaitAny();"); break;
                    case "conditionally":
                        var resultType = Type(Required(parallel.ResultType, parallel, "resultType"), parallel, "resultType");
                        _workflowRoot = "data";
                        var condition = Expression(parallel.Condition ?? throw Error(parallel, "Missing 'condition'"));
                        _workflowRoot = "ctx.WorkflowData";
                        Line($"setup.WaitConditionally<{resultType}>(completeWhenExpression: (data, results) => {condition});");
                        break;
                    default: throw Error(parallel, "Unknown parallel join; use all, any or conditionally", "join");
                }
            }
            if (step.Into is { } into)
            {
                Locate(into);
                Line($"setup.Into(ctx => {Expression(into)});");
            }
            Line("});");
        }

        private void Activity(string type, Dictionary<string, object?> defaults, List<InputMappingDocument> inputs,
            List<OutputMappingDocument> outputs, DocumentNode node, string builder, string next)
        {
            Locate(node, "activity");
            Line($"var {next} = {builder}.Step<{Type(Required(type, node, "activity"), node, "activity")}>(setup => {{");
            foreach (var pair in defaults)
                if (!inputs.Any(input => input.Target == pair.Key))
                    Line($"setup.Input(act => act.{Member(pair.Key, node)}).From(ctx => {Literal(pair.Value, node)});");
            Mappings(inputs, outputs);
            Line("});");
        }

        private void Mappings(IEnumerable<InputMappingDocument> inputs, IEnumerable<OutputMappingDocument> outputs)
        {
            foreach (var input in inputs)
            {
                Locate(input);
                Line($"setup.Input(act => act.{Member(Required(input.Target, input, "target"), input)}).From(ctx => {Expression(input.From)});");
            }
            foreach (var output in outputs)
            {
                Locate(output);
                if (output.SourceContext is not ("activity" or "event" or "workflow"))
                    throw Error(output, $"Unknown output source context '{output.SourceContext}'");
                Line($"setup.Output(act => act.{Member(Required(output.Source, output, "source"), output)}).To(ctx => {Expression(output.To)});");
            }
        }

        private string Expression(ExpressionDocument node, string? eventName = null)
        {
            switch (node)
            {
                case ConstantExpressionDocument constant: return Literal(constant.Value, node);
                case PathExpressionDocument expression: return Rewrite(expression.Path, node, eventName);
                case BinaryExpressionDocument binary:
                    var symbol = binary.Operator switch
                    {
                        "eq" => "==", "ne" => "!=", "and" => "&&", "or" => "||",
                        "gt" => ">", "ge" => ">=", "lt" => "<", "le" => "<=",
                        "add" => "+", "subtract" => "-", "multiply" => "*", "divide" => "/",
                        _ => throw Error(node, $"Unknown binary operator '{binary.Operator}'")
                    };
                    return "(" + Expression(binary.Left, eventName) + " " + symbol + " " + Expression(binary.Right, eventName) + ")";
                case FunctionExpressionDocument function:
                    var arguments = function.Arguments.Select(argument => Expression(argument, eventName)).ToArray();
                    if (!document.Code.TryGetValue(function.Function, out var asset))
                        return Rewrite(function.Function + "(" + string.Join(", ", arguments) + ")", node, eventName);
                    if (asset.Language != "csharp-expression")
                        throw Error(asset, "Workflow expressions must use csharp-expression; code activities require publishing");
                    if (arguments.Length != 0) throw Error(node, "A C# expression asset reads its context directly and takes no arguments");
                    return Rewrite(asset.Source, asset, eventName);
                default: throw Error(node, "Unsupported expression kind");
            }
        }

        private string Rewrite(string code, DocumentNode node, string? eventName)
        {
            // Tokenization keeps strings, comments and already escaped identifiers intact.
            var tokens = SyntaxFactory.ParseTokens(code).Select(token => token.IsKind(SyntaxKind.EventKeyword)
                ? SyntaxFactory.Identifier(token.LeadingTrivia, "@event", token.TrailingTrivia) : token);
            var expression = SyntaxFactory.ParseExpression(string.Concat(tokens.Select(token => token.ToFullString())));
            if (expression.ContainsDiagnostics) throw Error(node, "Invalid C# expression: " + code);
            return new ContextNames(_arguments, _items, eventName, _workflowRoot, document.Workflow.DataType, options.Members).Visit(expression)!.NormalizeWhitespace().ToFullString();
        }

        private string Literal(object? value, DocumentNode node) => value switch
        {
            null => "null",
            string text => Quote(text),
            bool flag => flag ? "true" : "false",
            long integer => integer.ToString(CultureInfo.InvariantCulture) + (integer is > int.MaxValue or < int.MinValue ? "L" : ""),
            int integer => integer.ToString(CultureInfo.InvariantCulture),
            double number when !double.IsNaN(number) && !double.IsInfinity(number) => number.ToString("R", CultureInfo.InvariantCulture) + "d",
            decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
            _ => throw Error(node, "Constants must be scalar C# values")
        };

        private string Type(string key, DocumentNode node, string? member = null)
        {
            var name = options.Types.TryGetValue(key, out var mapped) ? mapped : key;
            if (SyntaxFactory.ParseTypeName(name).ContainsDiagnostics)
                throw Error(node, $"Invalid C# type '{name}'; resolve catalog keys before translation", member);
            return name;
        }

        private string Member(string name, DocumentNode node) => SyntaxFacts.IsValidIdentifier(name)
            ? name : throw Error(node, $"Invalid C# property '{name}'");

        private void Locate(DocumentNode node, string? member = null)
        {
            var location = member != null && node.Members.TryGetValue(member, out var found) ? found : node.Location;
            Line($"// {path}:{Math.Max(1, location.Line)}");
            Line($"#line {Math.Max(1, location.Line)} \"{path}\"");
        }
        private void Line(string text) => _source.Append(text).Append('\n');
        private WorkflowSourceException Error(DocumentNode node, string message, string? member = null)
        {
            var location = member != null && node.Members.TryGetValue(member, out var found) ? found : node.Location;
            return new WorkflowSourceException(message, path, Math.Max(1, location.Line), Math.Max(1, location.Column));
        }
        private string Required(string? value, DocumentNode node, string key) =>
            string.IsNullOrWhiteSpace(value) ? throw Error(node, $"Missing '{key}'", key) : value!;
    }

    private sealed class ContextNames(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> items, string? eventName, string workflowRoot, string dataType, IDictionary<string, string> members) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
            return node.Expression is IdentifierNameSyntax root && root.Identifier.ValueText == "workflow" &&
                members.TryGetValue(dataType + "." + node.Name.Identifier.ValueText, out var member)
                ? visited.WithName(SyntaxFactory.IdentifierName(member).WithTriviaFrom(visited.Name)) : visited;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node) return node;
            var name = node.Identifier.ValueText;
            var replacement = name switch
            {
                "workflow" => workflowRoot, "previousStep" => "ctx.PreviousStep",
                "event" => eventName ?? "ctx.ResumeEvent",
                _ => items.TryGetValue(name, out var handle) ? "ctx.Item(" + handle + ")"
                    : arguments.TryGetValue(name, out var argument) ? "(" + argument + ")" : null
            };
            return replacement == null ? node : SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
        }
    }

    private static string Quote(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
