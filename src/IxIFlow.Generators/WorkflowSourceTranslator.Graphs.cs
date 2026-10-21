using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators;

public static partial class WorkflowSourceTranslator
{
    private sealed partial class Translation
    {
        private static string Destination(string? key) => key == null ? "null" : Quote(key);
        private string Site(DocumentNode node, string? member = null) => $"sourcePath: {Quote(path)}, sourceLine: {Math.Max(1,
            member != null && node.Members.TryGetValue(member, out var location) ? location.Line : node.Location.Line)}";

        private void Flowchart(FlowchartStepDocument step, string builder, string next, string id, string previousType)
        {
            var graph = "graph" + ++_next;
            Line($"var {next} = {builder}.Flowchart({graph} => {{");
            Line($"{graph}.Start({Quote(step.Start)});");
            foreach (var child in step.ChildScopes())
            {
                var node = step.Nodes.First(n => n.Key == child.Key);
                var body = "node" + ++_next;
                var edges = "edges" + ++_next;
                Locate(node);
                Line($"{graph}.Node({Quote(node.Key)}, {body} => {{");
                var last = Steps(child.Steps, body, id + "/" + node.Key + "/body", previousType);
                Line($"return {last};");
                Line($"}}, {edges} => {{");
                var target = edges;
                if (node.Switch != null)
                {
                    target = "cases" + ++_next;
                    Locate(node, "switch");
                    Line($"var {target} = {edges}.Switch(switchExpression: ctx => {Expression(node.Switch)});");
                }
                foreach (var edge in node.Transitions)
                {
                    Locate(edge);
                    if (edge.When != null)
                        Line($"{edges}.When(whenExpression: ctx => {Expression(edge.When)}, to: {Destination(edge.To)}, {Site(edge)});");
                    else if (edge.Case != null || edge.Members.ContainsKey("case"))
                        Line($"{target}.Case({(edge.Case is ExpressionDocument label ? Expression(label) : Literal(edge.Case, edge))}, to: {Destination(edge.To)}, {Site(edge)});");
                    else if (edge.Default == false || edge.Default == null && node.Switch == null && node.Transitions.Count == 1)
                        Line($"{edges}.Always(to: {Destination(edge.To)}, {Site(edge)});");
                    else
                        Line($"{target}.Otherwise(to: {Destination(edge.To)}, {Site(edge)});");
                }
                Line($"}}, {Site(node)});");
                if (node.Name != null) Line($"{graph}.Named({Quote(node.Name)});");
            }
            Line("});");
        }

        private void StateMachine(StateMachineStepDocument step, string builder, string next, string id, string previousType)
        {
            var machine = "machine" + ++_next;
            Line($"var {next} = {builder}.StateMachine({machine} => {{");
            Line($"{machine}.Start({Quote(step.Start)});");
            var scopes = step.ChildScopes().ToDictionary(child => child.Key!, child => child.Steps);
            foreach (var state in step.States)
            {
                var owner = "state" + ++_next;
                Locate(state);
                Line($"{machine}.State({Quote(state.Key)}, {owner} => {{");
                if (state.Name != null) Line($"{owner}.Named({Quote(state.Name)});");
                if (state.Final) Line($"{owner}.Final();");
                StateBody("Entry", state, scopes[state.Key]);
                if (scopes.TryGetValue(state.Key + "/exit", out var exit)) StateBody("Exit", state, exit);
                for (var i = 0; i < state.Transitions.Count; i++)
                {
                    var transition = state.Transitions[i];
                    var trigger = "trigger" + ++_next;
                    Locate(transition);
                    var method = transition.On != null ? $"On<{Type(transition.Event!, transition, "event")}>({Quote(transition.On)}, " :
                        transition.Reply != null ? $"OnReply<{Type(transition.Event!, transition, "event")}>({Quote(transition.Reply)}, " :
                        transition.Activity != null ? $"OnActivity<{Type(transition.Activity, transition, "activity")}> (" :
                        transition.After is string duration ? $"After({Duration(duration)}, " :
                        transition.After is ExpressionDocument expression ? $"After(afterExpression: ctx => {Expression(expression)}, configure: " :
                        "Immediately(";
                    Line($"{owner}.{method}{trigger} => {{");
                    if (transition.Matches != null)
                    {
                        Locate(transition, "matches");
                        Line($"{trigger}.Matches(matchesExpression: (evt, ctx) => {Expression(transition.Matches, "evt")});");
                    }
                    if (transition.DefaultPayload != null)
                    {
                        Locate(transition, "defaultPayload");
                        Line($"{trigger}.DefaultPayload({PayloadLiteral(transition.DefaultPayload, transition)});");
                    }
                    Mappings(transition.Input, transition.Output, trigger);
                    var cases = transition.Cases ?? [transition];
                    for (var j = 0; j < cases.Count; j++)
                    {
                        var alternative = cases[j];
                        var action = "action" + ++_next;
                        Locate(alternative);
                        var choose = alternative.When == null ? "Otherwise(" : $"When(whenExpression: ctx => {Expression(alternative.When)}, ";
                        var body = scopes[state.Key + "/on/" + i + "/case/" + j];
                        var choosePrefix = $"{trigger}.{choose}to: {Destination(alternative.To)}, ";
                        if (body.Count == 0) Line(choosePrefix + Site(alternative) + ");");
                        else
                        {
                            Line(choosePrefix + $"action: {action} => {{");
                            var firedType = transition.Event != null ? Type(transition.Event, transition) :
                                transition.Activity != null ? Type(transition.Activity, transition) : previousType;
                            Steps(body, action, id + "/" + state.Key + "/on/" + i + "/case/" + j + "/action", firedType);
                            Line($"}}, {Site(alternative)});");
                        }
                    }
                    Line($"}}, {Site(transition)});");
                }
                Line($"}}, {Site(state)});");

                void StateBody(string method, DocumentNode node, List<WorkflowStepDocument> body)
                {
                    if (body.Count == 0 && method == "Entry") return;
                    var sequence = "body" + ++_next;
                    Locate(node, method == "Entry" ? "entry" : "exit");
                    Line($"{owner}.{method}({sequence} => {{");
                    var last = Steps(body, sequence, id + "/" + state.Key + "/" + method.ToLowerInvariant(), previousType);
                    Line($"return {last};");
                    Line(method == "Exit" ? $"}}, {Site(node, "exit")});" : "});");
                }
            }
            Line("});");
        }
    }
}
