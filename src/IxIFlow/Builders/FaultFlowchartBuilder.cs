using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds a graph of keyed nodes sharing the graph's incoming previous value.</summary>
public sealed class FaultFlowchartBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    private readonly WorkflowStep _graph;

    private readonly Func<List<WorkflowStep>, ICatchWorkflowBuilder<TData, TFault, TPrevious>> _body;

    internal FaultFlowchartBuilder(WorkflowStep graph, Func<List<WorkflowStep>, ICatchWorkflowBuilder<TData, TFault, TPrevious>> body) =>
        (_graph, _body) = (graph, body);

    /// <summary>Sets the required starting node key.</summary>
    public FaultFlowchartBuilder<TData, TFault, TPrevious> Start(string key)
    {
        _graph.GraphStart = key;

        return this;
    }

    /// <summary>Adds a node whose edges read the last value returned by its body.</summary>
    public FaultFlowchartBuilder<TData, TFault, TPrevious> Node<TLast>(string key,
        Func<ICatchWorkflowBuilder<TData, TFault, TPrevious>, ICatchWorkflowBuilder<TData, TFault, TLast>> body,
        Action<FaultFlowchartEdges<TData, TFault, TLast>>? edges = null,
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
    public FaultFlowchartBuilder<TData, TFault, TPrevious> Node(string key,
        Action<FaultFlowchartEdges<TData, TFault, TPrevious>>? edges = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        Node(key, body => body, edges, sourcePath, sourceLine);

    /// <summary>Names the preceding node for inspection.</summary>
    public FaultFlowchartBuilder<TData, TFault, TPrevious> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _graph.GraphNodes[^1].Name = name;

        return this;
    }

    private static WorkflowStep Pick<T>(WorkflowStepType kind, string path, int line) where T : class => new()
    {
        StepType = kind, Name = kind.ToString(), SourceLocation = path + ":" + line,
        WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(T),
        Runtime = WorkflowStepDelegates.Create<TData, T>().ForFault<TFault>()
    };
}

/// <summary>Declares first-match edges reading a node's last body value.</summary>
public sealed class FaultFlowchartEdges<TData, TFault, TPrevious> where TPrevious : class
{
    private readonly FlowchartNode _node;
    private readonly WorkflowStep _decision;
    internal FaultFlowchartEdges(FlowchartNode node, WorkflowStep decision) => (_node, _decision) = (node, decision);

    /// <summary>Adds a pure guard; a null destination ends the chart.</summary>
    public FaultFlowchartEdges<TData, TFault, TPrevious> When(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, bool>> whenExpression, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = whenExpression.Compile();
        var edge = Add(to, sourcePath, sourceLine);
        edge.ConditionExpression = whenExpression;
        edge.CompiledCondition = context => evaluate((FaultContext<TData, TFault, TPrevious>)context);

        return this;
    }

    /// <summary>Selects case labels using one pure evaluation per decision.</summary>
    public FaultFlowchartCases<TData, TFault, TPrevious, TValue> Switch<TValue>(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> switchExpression,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _decision.SourceLocation = sourcePath + ":" + sourceLine;
        var evaluate = switchExpression.Compile();
        _decision.SwitchValueType = typeof(TValue);
        _decision.SwitchEquals = (left, right) => EqualityComparer<TValue>.Default.Equals((TValue)left!, (TValue)right!);
        _decision.OutcomeExpression = switchExpression;
        _decision.OutcomeSelector = context => evaluate((FaultContext<TData, TFault, TPrevious>)context);

        return new(this);
    }

    /// <summary>Adds the last, fallback edge.</summary>
    public FaultFlowchartEdges<TData, TFault, TPrevious> Otherwise(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Add(to, sourcePath, sourceLine).IsDefault = true;

        return this;
    }

    /// <summary>Adds an unconditional edge.</summary>
    public FaultFlowchartEdges<TData, TFault, TPrevious> Always(string? to,
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
            Runtime = WorkflowStepDelegates.Create<TData, TPrevious>().ForFault<TFault>(), SourceLocation = path + ":" + line
        };

        _node.Edges.Add([edge]);

        return edge;
    }
}

/// <summary>Declares ordered labels and a fallback for a switch decision.</summary>
public sealed class FaultFlowchartCases<TData, TFault, TPrevious, TValue> where TPrevious : class
{
    private readonly FaultFlowchartEdges<TData, TFault, TPrevious> _edges;
    internal FaultFlowchartCases(FaultFlowchartEdges<TData, TFault, TPrevious> edges) => _edges = edges;

    /// <summary>Adds an edge matched with the selector type's default equality comparer.</summary>
    public FaultFlowchartCases<TData, TFault, TPrevious, TValue> Case(TValue value, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _edges.Add(to, sourcePath, sourceLine).EdgeLabel = value;

        return this;
    }

    /// <summary>Adds the last, fallback edge.</summary>
    public FaultFlowchartCases<TData, TFault, TPrevious, TValue> Otherwise(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _edges.Otherwise(to, sourcePath, sourceLine);

        return this;
    }
}