using System.Collections;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class ChildScopeTests
{
    [Fact]
    public void EveryChildCollectionIsEnumeratedOnceIncludingEmptyBodies()
    {
        var bodies = new List<List<WorkflowStep>>();
        var step = PopulatedStep(bodies);
        var scopes = step.ChildScopes().ToArray();

        Assert.Equal(bodies.Count, scopes.Length);
        foreach (var body in bodies)
            Assert.Single(scopes, scope => ReferenceEquals(scope.Steps, body));
        Assert.Equal(["sequence", "then", "else", "loop", "foreach", "finally", "timeout", "catch"],
            scopes.Take(8).Select(scope => scope.Kind));
        Assert.All(scopes.Take(8), scope => Assert.Null(scope.Key));
        Assert.Equal([("branch", "0"), ("branch", "1"), ("outcome", "0"), ("outcome", "1")],
            scopes.Skip(8).Select(scope => (scope.Kind, scope.Key)));
        Assert.All(new WorkflowStep().ChildScopes(), scope => Assert.Empty(scope.Steps));
    }

    [Fact]
    public void CloneCopiesAllChildBodiesAndRetainsScopeSegments()
    {
        var step = PopulatedStep([]);
        var original = step.ChildScopes().ToArray();
        var copied = step.Clone().ChildScopes().ToArray();

        Assert.Equal(original.Length, copied.Length);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Kind, copied[index].Kind);
            Assert.Equal(original[index].Key, copied[index].Key);
            Assert.NotSame(original[index].Steps, copied[index].Steps);
            Assert.Equal(original[index].Steps.Select(child => child.Id), copied[index].Steps.Select(child => child.Id));
            foreach (var (source, target) in original[index].Steps.Zip(copied[index].Steps))
                Assert.NotSame(source, target);
        }
    }

    [Fact]
    public void FlattenPreservesItsDiagnosticOrder()
    {
        var step = PopulatedStep([]);
        foreach (var scope in step.ChildScopes())
            foreach (var child in scope.Steps)
                child.Id = scope.Kind + scope.Key;

        Assert.Equal([step.Id, "sequence", "then", "else", "loop", "finally", "catch", "timeout", "foreach", "branch0", "outcome0", "outcome1"],
            ParallelRules.Flatten([step]).Select(child => child.Id));
    }

    [Fact]
    public void RequestDiagnosticsStillVisitTimeoutsBeforeCatchesAndItemBodies()
    {
        var step = PopulatedStep([]);
        foreach (var scope in step.ChildScopes().Where(scope => scope.Kind is "timeout" or "catch" or "foreach"))
            foreach (var child in scope.Steps)
            {
                child.RequestName = scope.Kind;
                child.ReplyType = typeof(object);
            }

        var error = Assert.Throws<WorkflowEngineException>(() => RequestPairing.Pair([step]));
        Assert.Contains("Request 'timeout' needs exactly one WaitFor", error.Message);
    }

    private static WorkflowStep PopulatedStep(List<List<WorkflowStep>> bodies)
    {
        var step = new WorkflowStep();
        Populate(step);
        return step;

        void Populate(object owner)
        {
            foreach (var property in owner.GetType().GetProperties().Where(property => ContainsSteps(property.PropertyType)))
                property.SetValue(owner, Make(property.PropertyType));
        }

        object Make(Type type)
        {
            if (type == typeof(WorkflowStep)) return new WorkflowStep();
            var value = Activator.CreateInstance(type)!;
            if (value is List<WorkflowStep> body)
            {
                bodies.Add(body);
                body.Add(new WorkflowStep());
            }
            else if (value is IList list)
            {
                var element = type.GetGenericArguments()[0];
                list.Add(Make(element));
                var empty = Activator.CreateInstance(element)!;
                list.Add(empty);
                if (empty is List<WorkflowStep> emptyBody) bodies.Add(emptyBody);
                else Populate(empty);
            }
            else Populate(value);
            return value;
        }
    }

    private static bool ContainsSteps(Type type) => type == typeof(WorkflowStep) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? ContainsSteps(type.GetGenericArguments()[0])
            : type.IsClass && type.Assembly == typeof(WorkflowStep).Assembly &&
                type.GetProperties().Any(property => ContainsSteps(property.PropertyType)));
}
