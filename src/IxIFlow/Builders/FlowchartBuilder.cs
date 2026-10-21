using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds a graph of keyed nodes sharing the graph's incoming previous value.</summary>
public sealed class FlowchartBuilder<TData, TPrevious> where TPrevious : class
{
    private readonly WorkflowStep _graph;

    private readonly Func<List<WorkflowStep>, ISequenceBuilder<TData, TPrevious>> _body;
    internal FlowchartBuilder(WorkflowStep graph, Func<List<WorkflowStep>, ISequenceBuilder<TData, TPrevious>> body) =>
        (_graph, _body) = (graph, body);

    /// <summary>Sets the required starting node key.</summary>
    public FlowchartBuilder<TData, TPrevious> Start(string key)
    {
        _graph.GraphStart = key;
        return this;
    }

    /// <summary>Adds a node whose edges read the last value returned by its body.</summary>
    public FlowchartBuilder<TData, TPrevious> Node<TLast>(string key,
        Func<ISequenceBuilder<TData, TPrevious>, IWorkflowBuilder<TData, TLast>> body,
        Action<FlowchartEdges<TData, TLast>>? edges = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TLast : class
    {
        var node = new FlowchartNode { Key = key, Name = key };
        node.Steps.Add(Pick<TPrevious>(WorkflowStepType.Enter, sourcePath, sourceLine));
        body(_body(node.Steps));
        var decision = Pick<TLast>(WorkflowStepType.Decision, sourcePath, sourceLine);
        node.Steps.Add(decision);
        edges?.Invoke(new(node, decision));
        _graph.GraphNodes.Add(node);
        return this;
    }

    /// <summary>Adds an empty node whose edges read the incoming previous value.</summary>
    public FlowchartBuilder<TData, TPrevious> Node(string key,
        Action<FlowchartEdges<TData, TPrevious>>? edges = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        Node(key, body => body, edges, sourcePath, sourceLine);

    /// <summary>Names the preceding node for inspection.</summary>
    public FlowchartBuilder<TData, TPrevious> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _graph.GraphNodes[^1].Name = name;
        return this;
    }

    private static WorkflowStep Pick<T>(WorkflowStepType kind, string path, int line) where T : class => new()
    {
        StepType = kind, Name = kind.ToString(), SourceLocation = path + ":" + line,
        WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(T),
        Runtime = WorkflowStepDelegates.Create<TData, T>()
    };
}

/// <summary>Declares first-match edges reading a node's last body value.</summary>
public sealed class FlowchartEdges<TData, TPrevious> where TPrevious : class
{
    private readonly FlowchartNode _node;
    private readonly WorkflowStep _decision;
    internal FlowchartEdges(FlowchartNode node, WorkflowStep decision) => (_node, _decision) = (node, decision);

    /// <summary>Adds a pure guard; a null destination ends the chart.</summary>
    public FlowchartEdges<TData, TPrevious> When(
        Expression<Func<WorkflowContext<TData, TPrevious>, bool>> whenExpression, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = whenExpression.Compile();
        var edge = Add(to, sourcePath, sourceLine);
        edge.ConditionExpression = whenExpression;
        edge.CompiledCondition = context => evaluate((WorkflowContext<TData, TPrevious>)context);
        return this;
    }

    /// <summary>Selects case labels using one pure evaluation per decision.</summary>
    public FlowchartCases<TData, TPrevious, TValue> Switch<TValue>(
        Expression<Func<WorkflowContext<TData, TPrevious>, TValue>> switchExpression)
    {
        var evaluate = switchExpression.Compile();
        _decision.OutcomeExpression = switchExpression;
        _decision.OutcomeSelector = context => evaluate((WorkflowContext<TData, TPrevious>)context);
        return new(this);
    }

    /// <summary>Adds the last, fallback edge.</summary>
    public FlowchartEdges<TData, TPrevious> Otherwise(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Add(to, sourcePath, sourceLine).IsDefault = true;
        return this;
    }

    /// <summary>Adds an unconditional edge.</summary>
    public FlowchartEdges<TData, TPrevious> Always(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Add(to, sourcePath, sourceLine);
        return this;
    }

    internal WorkflowStep Add(string? to, string path, int line)
    {
        var edge = new WorkflowStep
        {
            StepType = WorkflowStepType.Edge, Name = "Edge", Target = to == "end" ? null : to,
            WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(TPrevious),
            Runtime = WorkflowStepDelegates.Create<TData, TPrevious>(), SourceLocation = path + ":" + line
        };
        _node.Edges.Add([edge]);
        return edge;
    }
}

/// <summary>Declares ordered labels and a fallback for a switch decision.</summary>
public sealed class FlowchartCases<TData, TPrevious, TValue> where TPrevious : class
{
    private readonly FlowchartEdges<TData, TPrevious> _edges;
    internal FlowchartCases(FlowchartEdges<TData, TPrevious> edges) => _edges = edges;

    /// <summary>Adds an edge matched with <see cref="object.Equals(object?, object?)"/>.</summary>
    public FlowchartCases<TData, TPrevious, TValue> Case(TValue value, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _edges.Add(to, sourcePath, sourceLine).EdgeLabel = value;
        return this;
    }

    /// <summary>Adds the last, fallback edge.</summary>
    public FlowchartCases<TData, TPrevious, TValue> Otherwise(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _edges.Otherwise(to, sourcePath, sourceLine);
        return this;
    }
}
