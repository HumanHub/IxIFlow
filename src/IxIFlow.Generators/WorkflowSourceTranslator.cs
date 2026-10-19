using System.Globalization;
using System.Text;
using IxIFlow.Dsl.Documents;
using IxIFlow.Dsl.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.Core;

namespace IxIFlow.Generators;

/// <summary>
/// Lowers the parsed document model into the same executable structure as fluent C#.
/// The runtime hashes that structure, rather than YAML text or generated method bodies,
/// so comments, key order, source positions and presentation metadata cannot affect the hash.
/// </summary>
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
        options ??= new WorkflowSourceOptions();
        var validation = new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options)).GetAwaiter().GetResult();
        var diagnostic = validation.Diagnostics.FirstOrDefault(item => item.Severity == WorkflowDiagnosticSeverity.Error);
        if (diagnostic != null)
        {
            var location = DocumentModel.GetLocation(document, diagnostic.Path);
            throw new WorkflowSourceException(diagnostic.Message, sourcePath, location.Line, location.Column);
        }
        return new Translation(document, sourcePath, options).Run();
    }

    public static string ClassName(string sourcePath) =>
        "Workflow_" + string.Concat(Path.GetFileNameWithoutExtension(sourcePath)
            .Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    internal static IEnumerable<string> TypeNames(WorkflowDocument document)
    {
        yield return document.Workflow.DataType;
        foreach (var activity in document.Activities.Values) yield return activity.Activity;
        foreach (var step in Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(template => template.Steps))))
            foreach (var type in StepTypeNames(step)) yield return type;
    }

    private static IEnumerable<WorkflowStepDocument> Flatten(IEnumerable<WorkflowStepDocument> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in Flatten(Children(step))) yield return nested;
        }
    }

    private static IEnumerable<WorkflowStepDocument> Children(WorkflowStepDocument step) => step switch
    {
        SequenceStepDocument sequence => sequence.Steps,
        ConditionalStepDocument condition => condition.Then.Concat(condition.Else),
        ForEachStepDocument each => each.Steps,
        WaitStepDocument wait => wait.OnTimeout ?? Enumerable.Empty<WorkflowStepDocument>(),
        ParallelStepDocument parallel => parallel.Branches.SelectMany(branch => branch.Steps),
        LoopStepDocument loop => loop.Steps,
        TryStepDocument attempt => attempt.Steps.Concat(attempt.Catches.SelectMany(handler => handler.Steps))
            .Concat(attempt.Finally ?? Enumerable.Empty<WorkflowStepDocument>()),
        SagaStepDocument saga => saga.Steps.Concat(saga.OnError.SelectMany(handler => handler.Steps)),
        OutcomeStepDocument outcome => outcome.Branches.SelectMany(branch => branch.Steps)
            .Concat(outcome.Default ?? Enumerable.Empty<WorkflowStepDocument>()),
        _ => Enumerable.Empty<WorkflowStepDocument>()
    };

    private static IEnumerable<string> StepTypeNames(WorkflowStepDocument step)
    {
        switch (step)
        {
            case ActivityStepDocument activity:
                yield return activity.Activity;
                foreach (var undo in activity.Compensation)
                {
                    yield return undo.Activity;
                    if (undo.ReplyType != null) yield return undo.ReplyType;
                    if (undo.PreviousActivity != null) yield return undo.PreviousActivity;
                }
                foreach (var policy in activity.OnError)
                    if (policy.Exception != null) yield return policy.Exception;
                break;
            case WaitStepDocument wait: yield return wait.Event; break;
            case InvokeWorkflowTypeStepDocument invocation:
                yield return invocation.WorkflowType;
                if (invocation.DataType != null) yield return invocation.DataType;
                break;
            case TryStepDocument attempt:
                foreach (var handler in attempt.Catches)
                {
                    if (handler.Exception != null) yield return handler.Exception;
                    if (handler.FaultType != null) yield return handler.FaultType;
                }
                break;
            case SagaStepDocument saga:
                foreach (var handler in saga.OnError)
                {
                    if (handler.Exception != null) yield return handler.Exception;
                    if (handler.FaultType != null) yield return handler.FaultType;
                    if (handler.CompensateUpTo != null) yield return handler.CompensateUpTo;
                }
                break;
        }
        var choices = step switch
        {
            ParallelStepDocument parallel => parallel.Combine,
            ForEachStepDocument { Parallel: { } parallel } => parallel.Combine,
            _ => Enumerable.Empty<JoinCombinerDocument>()
        };
        foreach (var choice in choices)
        {
            yield return choice.PropertyType;
            yield return choice.Combiner;
        }
        if (step is ForEachStepDocument { Parallel.ResultType: { } result }) yield return result;
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
        private string _previousRoot = "ctx.PreviousStep";

        public string Run()
        {
            foreach (var import in document.Imports.Catalogs.Concat(document.Imports.Workflows))
                if (!options.ResolvedImports.Contains(import))
                    throw Error(document.Imports, $"Import '{import}' requires publishing to resolve and pin its package or workflow");
            foreach (var parameter in document.Steps.Values.SelectMany(template => template.Parameters.Values))
            {
                ParameterType(parameter);
                if (parameter.Default != null) ParameterValue(parameter, Literal(parameter.Default, parameter), parameter.Default);
            }
            var workflow = document.Workflow;
            var name = workflow.Name!;
            var data = Type(workflow.DataType!, workflow, "dataType");
            var className = options.ClassName ?? ClassName(path);
            if (!SyntaxFacts.IsValidIdentifier(className) || SyntaxFactory.ParseName(options.Namespace).ContainsDiagnostics)
                throw Error(workflow, "Invalid generated class or namespace");
            Line("// <auto-generated/>");
            Line("#nullable enable");
            Line("using System;");
            Line("using System.Linq;");
            Line("using IxIFlow.Builders;");
            Line("using IxIFlow.Core;");
            Line("using IxIFlow.Builders.Interfaces;");
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

        private string Steps(IEnumerable<WorkflowStepDocument> steps, string builder, string scope,
            string previousType = "object", WorkflowDocumentScope builderScope = WorkflowDocumentScope.Workflow)
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
                        Activity(activity.Activity, activity.Defaults, activity.Input, activity.Output, activity, builder, next, previousType, builderScope);
                        if (HasSetup(activity) || builderScope != WorkflowDocumentScope.SagaHandler)
                            previousType = Type(activity.Activity, activity, "activity");
                        break;
                    case ActivityReferenceStepDocument reference:
                        var template = document.Activities[reference.Ref];
                        Activity(template.Activity, template.Defaults, template.Input, template.Output, template, builder, next, previousType, builderScope);
                        if (HasSetup(template) || builderScope != WorkflowDocumentScope.SagaHandler)
                            previousType = Type(template.Activity, template, "activity");
                        break;
                    case SequenceStepDocument sequence:
                        Container(builder, next, id, sequence.Steps, previousType);
                        break;
                    case StepReferenceStepDocument stepReference:
                        var block = document.Steps[stepReference.Ref];
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
                        Container(builder, next, id, block.Steps, previousType);
                        _templatePrefix = savedPrefix;
                        _arguments.Clear();
                        foreach (var pair in saved) _arguments[pair.Key] = pair.Value;
                        _templates.Remove(stepReference.Ref);
                        break;
                    case ConditionalStepDocument condition:
                        Line($"var {next} = {builder}.If(ctx => {Expression(condition.Condition)}, then => {{");
                        Steps(condition.Then, "then", id + "/then", previousType);
                        Line("}, @else => {");
                        Steps(condition.Else, "@else", id + "/else", previousType);
                        Line("});");
                        break;
                    case ForEachStepDocument each:
                        ForEach(each, builder, next, id, previousType);
                        break;
                    case WaitStepDocument wait:
                        Wait(wait, builder, next, id, previousType, builderScope);
                        previousType = Type(wait.Event, wait, "event");
                        break;
                    case ParallelStepDocument parallel:
                        Parallel(parallel, builder, next, id, previousType);
                        break;
                    case LoopStepDocument loop:
                        Loop(loop, builder, next, id, previousType);
                        break;
                    case TryStepDocument attempt:
                        Try(attempt, builder, next, id, previousType);
                        builderScope = WorkflowDocumentScope.Workflow;
                        break;
                    case SagaStepDocument saga:
                        Saga(saga, builder, next, id, previousType);
                        builderScope = WorkflowDocumentScope.Workflow;
                        break;
                    case OutcomeStepDocument outcome:
                        Outcome(outcome, builder, next, id, previousType);
                        break;
                    case DelayStepDocument delay:
                        Locate(delay, "duration");
                        Line($"var {next} = {builder}.Delay({Duration(delay.Duration)});");
                        break;
                    case ResultStepDocument result:
                        Locate(result, "value");
                        Line($"var {next} = {builder}.Result(valueExpression: ctx => {Expression(result.Value)});");
                        break;
                    case InvokeWorkflowTypeStepDocument typedInvocation:
                        if (options.RequireResolvedTypes &&
                            (!options.WorkflowIdentities.TryGetValue(typedInvocation.WorkflowType, out var identity) ||
                             string.IsNullOrWhiteSpace(identity.Name) || identity.Version < 1))
                            throw Error(typedInvocation, "Workflow class requires [Workflow(\"Name\", Version = n)] with a nonblank name and positive version", "workflowType");
                        var workflowType = Type(typedInvocation.WorkflowType!, typedInvocation, "workflowType");
                        var childData = Type(options.WorkflowDataTypes.TryGetValue(typedInvocation.WorkflowType, out var inferred) ? inferred :
                            typedInvocation.DataType ?? throw Error(typedInvocation, "Cannot infer IWorkflow<T>; supply dataType or a resolved workflow type contract", "workflowType"), typedInvocation, "dataType");
                        Locate(typedInvocation, "workflowType");
                        Line($"var {next} = {builder}.Invoke<{workflowType}, {childData}>(setup => {{");
                        Mappings(typedInvocation.Input, typedInvocation.Output);
                        if (!typedInvocation.CancelWithParent)
                        {
                            Locate(typedInvocation, "cancelWithParent");
                            Line("setup.CancelWithParent(false);");
                        }
                        Line("});");
                        previousType = childData;
                        break;
                    case InvokeWorkflowStepDocument invocation:
                        var childReference = invocation.Workflow;
                        var childName = childReference.Name!;
                        var lookup = childName + (childReference.Version == null ? "" : "@" + childReference.Version);
                        var child = options.Workflows[lookup];
                        Line($"var {next} = {builder}.Invoke<{Type(child.DataType, childReference)}>({Quote(childName)}, {child.Version}, setup => {{");
                        Mappings(invocation.Input, invocation.Output);
                        if (!invocation.CancelWithParent)
                        {
                            Locate(invocation, "cancelWithParent");
                            Line("setup.CancelWithParent(false);");
                        }
                        Line("});");
                        previousType = Type(child.DataType, childReference);
                        break;
                    default: throw Error(step, $"Unsupported step kind '{step.Kind}'");
                }
                Line($"{next}.WithId({Quote(id)}, {Quote(path)}, {Math.Max(1, step.Location.Line)});");
                if (step.Name != null) Line($"{next}.Named({Quote(step.Name)});");
                builder = next;
            }
            return builder;
        }

        private void Container(string builder, string next, string id, IEnumerable<WorkflowStepDocument> children, string previousType)
        {
            var body = "sequence" + _next;
            Line($"var {next} = {builder}.Sequence({body} => {{");
            Steps(children, body, id, previousType);
            Line("});");
        }

        private void ForEach(ForEachStepDocument step, string builder, string next, string id, string previousType)
        {
            var selector = Expression(step.Items);
            var alias = step.As;

            var handle = "item" + _next;
            var body = "body" + _next;
            Line($"var {next} = {builder}.ForEach(itemsExpression: ctx => {selector}, ({body}, {handle}) => {{");
            _items.Add(alias, handle);
            var last = Steps(step.Steps, body, id + "/body", previousType);
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
                Line($"setup.Parallel({parallel.MaxConcurrency?.ToString(CultureInfo.InvariantCulture)});");
                switch (parallel.Join)
                {
                    case "all": break;
                    case "any": Line("setup.WaitAny();"); break;
                    case "conditionally":
                        var resultType = Type(parallel.ResultType!, parallel, "resultType");
                        _workflowRoot = "data";
                        var condition = Expression(parallel.Condition!);
                        _workflowRoot = "ctx.WorkflowData";
                        Line($"setup.WaitConditionally<{resultType}>(completeWhenExpression: (data, results) => {condition});");
                        break;
                    default: throw new InvalidOperationException($"Declared parallel join '{parallel.Join}' has no translation");
                }
            }
            if (step.Parallel is { } join) Combiners(join.Combine, "setup");
            if (step.Into is { } into)
            {
                Locate(into);
                Line($"setup.Into(ctx => {Expression(into)});");
            }
            Line("});");
        }

        private void Activity(string type, Dictionary<string, object?> defaults, List<InputMappingDocument> inputs,
            List<OutputMappingDocument> outputs, DocumentNode node, string builder, string next, string previousType, WorkflowDocumentScope scope)
        {
            Locate(node, "activity");
            var activityType = Type(type!, node, "activity");
            var request = node as RequestStepDocument;
            if (scope == WorkflowDocumentScope.SagaHandler && !HasSetup(node))
            {
                Line($"var {next} = {builder}.Step<{activityType}>();");
                return;
            }
            Line($"var {next} = {builder}.{(request == null ? "Step" : "Request")}<{activityType}>(" +
                (request == null ? "" : Quote(request.Key!) + ", ") + "setup => {");
            foreach (var pair in defaults)
                if (!inputs.Any(input => input.Target == pair.Key))
                    Line($"setup.Input(act => act.{Member(pair.Key, node)}).From(ctx => {Literal(pair.Value, node)});");
            Mappings(inputs, outputs);
            if (node is ActivityStepDocument activity) SagaSetup(activity, activityType, previousType);
            if (request?.Subject != null)
            {
                Locate(request, "subject");
                Line($"}}, subjectExpression: ctx => {Expression(request.Subject)});");
            }
            else Line("});");
        }

        private void Mappings(IEnumerable<InputMappingDocument> inputs, IEnumerable<OutputMappingDocument> outputs,
            string setup = "setup", string? inputContext = null)
        {
            foreach (var input in inputs)
            {
                Locate(input);
                Line($"{setup}.Input(act => act.{Member(input.Target!, input)}).From({(inputContext == null ? "ctx" : "(" + inputContext + " ctx)")} => {Expression(input.From)});");
            }
            foreach (var output in outputs)
            {
                Locate(output);
                Line($"{setup}.Output(act => act.{Member(output.Source!, output)}).To(ctx => {Expression(output.To)});");
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
                        _ => throw new InvalidOperationException($"Declared binary operator '{binary.Operator}' has no translation")
                    };
                    return "(" + Expression(binary.Left, eventName) + " " + symbol + " " + Expression(binary.Right, eventName) + ")";
                case FunctionExpressionDocument function:
                    var asset = document.Code[function.Function];
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
            return new ContextNames(_arguments, _items, eventName, _workflowRoot, _previousRoot, document.Workflow.DataType, options.Members).Visit(expression)!.NormalizeWhitespace().ToFullString();
        }

        private string PayloadLiteral(object? value, DocumentNode node) => value switch
        {
            IDictionary<string, object?> properties => "new() { " + string.Join(", ", properties.Select(
                pair => Member(pair.Key, node) + " = " + PayloadLiteral(pair.Value, node))) + " }",
            IEnumerable<object?> items => "[" + string.Join(", ", items.Select(item => PayloadLiteral(item, node))) + "]",
            _ => Literal(value, node)
        };

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
        private static bool HasSetup(DocumentNode node) => node switch
        {
            ActivityStepDocument activity => activity is RequestStepDocument || activity.Defaults.Count > 0 ||
                activity.Input.Count > 0 || activity.Output.Count > 0 || activity.Compensation.Count > 0 || activity.OnError.Count > 0,
            ActivityTemplateDocument template => template.Defaults.Count > 0 || template.Input.Count > 0 || template.Output.Count > 0,
            _ => false
        };

        private void Parallel(ParallelStepDocument step, string builder, string next, string id, string previousType)
        {
            var parallel = "parallel" + _next;
            Line($"var {next} = {builder}.Parallel({parallel} => {{");
            for (var index = 0; index < step.Branches.Count; index++)
            {
                var branch = step.Branches[index];
                var body = "branch" + ++_next;
                Locate(branch);
                Line($"{parallel}.Do({body} => {{");
                Steps(branch.Steps, body, id + "/branches/" + index, previousType);
                Line("});");
            }
            Locate(step, "join");
            switch (step.Join)
            {
                case "all": break;
                case "any": Line($"{parallel}.WaitAny();"); break;
                case "conditionally":
                    var condition = step.Condition!;
                    Locate(condition);
                    _workflowRoot = "data";
                    Line($"{parallel}.WaitConditionally(completeWhenExpression: data => {Expression(condition)});");
                    _workflowRoot = "ctx.WorkflowData";
                    break;
                default: throw new InvalidOperationException($"Declared parallel join '{step.Join}' has no translation");
            }
            Combiners(step.Combine, parallel);
            Line("});");
        }

        private void Combiners(IEnumerable<JoinCombinerDocument> choices, string setup)
        {
            foreach (var choice in choices)
            {
                Locate(choice);
                var propertyType = Type(choice.PropertyType!, choice, "propertyType");
                var combiner = Type(choice.Combiner!, choice, "combiner");
                _workflowRoot = "data";
                Line($"{setup}.Combine<{propertyType}, {combiner}>(data => {Expression(choice.Property)});");
                _workflowRoot = "ctx.WorkflowData";
            }
        }

        private void Loop(LoopStepDocument step, string builder, string next, string id, string previousType)
        {
            var body = "loop" + _next;
            Locate(step, "condition");
            if (step is WhileDoStepDocument)
                Line($"var {next} = {builder}.WhileDo(conditionExpression: ctx => {Expression(step.Condition)}, configure: {body} => {{");
            else Line($"var {next} = {builder}.DoWhile({body} => {{");
            Steps(step.Steps, body, id + "/body", previousType);
            Locate(step, "condition");
            Line(step is WhileDoStepDocument ? "});" : $"}}, conditionExpression: ctx => {Expression(step.Condition)});");
        }

        private void Try(TryStepDocument step, string builder, string next, string id, string previousType)
        {
            var body = "tryBody" + _next;
            Line($"var {next} = {builder}.Try({body} => {{");
            Steps(step.Steps, body, id + "/try", previousType);
            Line("})");
            for (var index = 0; index < step.Catches.Count; index++)
            {
                var handler = step.Catches[index];
                if (handler.Id is { } handlerId && (string.IsNullOrWhiteSpace(handlerId) || !_ids.Add(handlerId)))
                    throw Error(handler, $"Invalid or duplicate step ID '{handlerId}'");
                var caught = "caught" + ++_next;
                Locate(handler);
                Line($".Catch{FaultTypes(handler.Exception, handler.FaultType, handler)}({caught} => {{");
                Steps(handler.Steps, caught, id + "/catches/" + index, previousType, WorkflowDocumentScope.Catch);
                Line($"}}, id: {(handler.Id == null ? "null" : Quote(handler.Id))})");
            }
            if (step.Finally != null)
            {
                var cleanup = "cleanup" + ++_next;
                Locate(step, "finally");
                Line($".Finally({cleanup} => {{");
                Steps(step.Finally, cleanup, id + "/finally", previousType);
                Line("})");
            }
            Line(";");
        }

        private string FaultTypes(string? exception, string? fault, DocumentNode node)
        {
            if (exception == null)
            {
                return "";
            }
            return "<" + Type(exception!, node, "exception") +
                (fault == null ? "" : ", " + Type(fault!, node, "faultType")) + ">";
        }

        private void Saga(SagaStepDocument step, string builder, string next, string id, string previousType)
        {
            var body = "saga" + _next;
            Line($"var {next} = {builder}.Saga({body} => {{");
            Steps(step.Steps, body, id + "/steps", previousType, WorkflowDocumentScope.Saga);
            Line("})");
            for (var index = 0; index < step.OnError.Count; index++)
            {
                var handler = step.OnError[index];
                if (handler.Id is { } handlerId && (string.IsNullOrWhiteSpace(handlerId) || !_ids.Add(handlerId)))
                    throw Error(handler, $"Invalid or duplicate step ID '{handlerId}'");
                var error = "error" + ++_next;
                Locate(handler);
                Line($".OnError{FaultTypes(handler.Exception, handler.FaultType, handler)}({error} => {{");
                var last = Steps(handler.Steps, error, id + "/onError/" + index, previousType, WorkflowDocumentScope.SagaHandler);
                if (handler.Compensate != null || handler.CompensateUpTo != null)
                {
                    Locate(handler, handler.CompensateUpTo != null ? "compensateUpTo" : "compensate");
                    var compensation = handler.CompensateUpTo != null
                        ? "CompensateUpTo<" + Type(handler.CompensateUpTo, handler, "compensateUpTo") + ">()"
                        : handler.Compensate == "all" ? "Compensate()" : "CompensateNone()";
                    var continuation = "continuation" + ++_next;
                    Line($"var {continuation} = {last}.{compensation};");
                    Locate(handler, "then");
                    switch (handler.Then)
                    {
                        case null: break;
                        case "continue":
                            Line($"{continuation}.ThenContinue({(handler.IgnoreCompensationErrors ? "true" : "false")});"); break;
                        case "terminate": Line($"{continuation}.ThenTerminate();"); break;
                        case "retry":
                            Line($"{continuation}.ThenRetry({(handler.Retry == null ? "3" : Retry(handler.Retry))});"); break;
                        default: throw new InvalidOperationException($"Declared saga continuation '{handler.Then}' has no translation");
                    }
                }
                Line($"}}, id: {(handler.Id == null ? "null" : Quote(handler.Id))})");
            }
            Line(";");
        }

        private void Outcome(OutcomeStepDocument step, string builder, string next, string id, string previousType)
        {
            var outcomes = "outcomes" + _next;
            Locate(step, "value");
            Line($"var {next} = {builder}.OutcomeOn(ctx => {Expression(step.Value)}, {outcomes} => {{");
            for (var index = 0; index < step.Branches.Count; index++)
            {
                var branch = step.Branches[index];
                var body = "outcome" + ++_next;
                Locate(branch, "value");
                Line($"{outcomes}.Outcome({Expression(branch.Value)}, {body} => {{");
                Steps(branch.Steps, body, id + "/branches/" + index, previousType, WorkflowDocumentScope.Saga);
                Line("});");
            }
            if (step.Default != null)
            {
                var body = "defaultOutcome" + ++_next;
                Locate(step, "default");
                Line($"{outcomes}.DefaultOutcome({body} => {{");
                Steps(step.Default, body, id + "/default", previousType, WorkflowDocumentScope.Saga);
                Line("});");
            }
            Line("});");
        }

        private void Wait(WaitStepDocument step, string builder, string next, string id, string previousType, WorkflowDocumentScope scope)
        {
            var eventType = Type(step.Event!, step, "event");
            var named = step as WaitForStepDocument;
            var key = named == null ? ((SuspendStepDocument)step).Reason : named.Key!;
            if (key.Length == 0) key = id;
            Locate(step, "event");
            Line($"var {next} = {builder}.{(named == null ? "Suspend" : "WaitFor")}<{eventType}>({Quote(key)}, " +
                (step.ResumeCondition == null ? "null" : (named == null && scope != WorkflowDocumentScope.Catch ? "resumeExpression: " : "matchesExpression: ") +
                    "(evt, ctx) => " + Expression(step.ResumeCondition, "evt")) + ", configure: setup => {");
            if (step.DefaultPayload != null)
            {
                Locate(step, "defaultPayload");
                Line($"setup.DefaultPayload({PayloadLiteral(step.DefaultPayload, step)});");
            }
            Mappings(step.Input, step.Output);
            if (step.Timeout != null)
            {
                Locate(step, "timeout");
                Line($"setup.TimeoutAfter({Duration(step.Timeout)});");
            }
            if (step.OnTimeout != null)
            {
                var body = "timeout" + ++_next;
                Locate(step, "onTimeout");
                Line($"setup.OnTimeout({body} => {{");
                Steps(step.OnTimeout, body, id + "/timeout", previousType);
                Line("});");
            }
            Line("});");
        }

        private void SagaSetup(ActivityStepDocument step, string activityType, string previousType)
        {
            foreach (var undo in step.Compensation)
            {
                Locate(undo, "activity");
                var activity = Type(undo.Activity!, undo, "activity");
                var data = Type(document.Workflow.DataType, document.Workflow, "dataType");
                if (undo.ReplyType != null)
                {
                    var reply = Type(undo.ReplyType!, undo, "replyType");
                    Line($"setup.CompensateWith<{activity}, {reply}>((IRequestCompensationActivityBuilder<{data}, {activity}, {activityType}, {reply}> compensation) => {{");
                }
                else if (undo.PreviousActivity != null)
                {
                    var previous = Type(undo.PreviousActivity!, undo, "previousActivity");
                    Line($"setup.CompensateWith<{activity}, {previous}>((ICompensationActivityBuilder<{data}, {activity}, {previousType}, {previous}> compensation) => {{");
                }
                else Line($"setup.CompensateWith<{activity}>(compensation => {{");
                Mappings(undo.Input, undo.Output, "compensation", undo.PreviousActivity == null ? null : $"WorkflowContext<{data}>");
                if (undo.PreviousActivity != null)
                {
                    _previousRoot = "ctx";
                    Mappings(undo.PreviousStepInput, [], "compensation", previousType);
                    _previousRoot = "ctx.PreviousStep";
                    Mappings(undo.PreviousCompensationInput, [], "compensation", Type(undo.PreviousActivity, undo, "previousActivity"));
                }
                Line("});");
            }
            foreach (var policy in step.OnError)
            {
                Locate(policy, "then");
                var exception = policy.Exception == null ? "" : "<" + Type(policy.Exception!, policy, "exception") + ">";
                var action = policy.Then switch
                {
                    "retry" => "ThenRetry(" + (policy.Retry == null ? "3" : Retry(policy.Retry)) + ")",
                    "terminate" => "ThenTerminate()",
                    "ignore" => "ThenIgnore()",
                    _ => throw new InvalidOperationException($"Declared step error action '{policy.Then}' has no translation")
                };
                Line($"setup.OnError{exception}(error => error.{action});");
            }
        }

        private string Retry(RetryPolicyDocument policy)
        {
            var initial = Duration(policy.InitialInterval);
            var maximum = Duration(policy.MaximumInterval);
            return $"new RetryPolicy {{ MaximumAttempts = {policy.MaximumAttempts}, InitialInterval = {initial}, MaximumInterval = {maximum}, BackoffCoefficient = {Literal(policy.BackoffCoefficient, policy)} }}";
        }

        private static string Duration(string value)
        {
            var duration = TimeSpan.ParseExact(value, "c", CultureInfo.InvariantCulture);
            return "TimeSpan.FromTicks(" + duration.Ticks.ToString(CultureInfo.InvariantCulture) + "L)";
        }

    }

    private sealed class ContextNames(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> items, string? eventName, string workflowRoot, string previousRoot, string dataType, IDictionary<string, string> members) : CSharpSyntaxRewriter
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
                "workflow" => workflowRoot, "previousStep" => previousRoot,
                "fault" => "ctx.Fault", "currentStep" => "ctx.CurrentStep",
                "reply" => "ctx.Reply", "answered" => "ctx.Answered", "previousCompensation" => "ctx",
                "event" => eventName ?? "ctx.ResumeEvent",
                _ => items.TryGetValue(name, out var handle) ? "ctx.Item(" + handle + ")"
                    : arguments.TryGetValue(name, out var argument) ? "(" + argument + ")" : null
            };
            return replacement == null ? node : SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
        }
    }

    private static string Quote(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
