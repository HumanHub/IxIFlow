using System.Collections;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;

namespace IxIFlow.Generators.Tests;

public sealed class DocumentChildScopeTests
{
    [Fact]
    public void EveryDocumentChildCollectionIsEnumeratedOnce()
    {
        foreach (var type in typeof(WorkflowStepDocument).Assembly.GetTypes()
                     .Where(type => !type.IsAbstract && typeof(WorkflowStepDocument).IsAssignableFrom(type)))
        {
            var bodies = new List<List<WorkflowStepDocument>>();
            var step = (WorkflowStepDocument)Activator.CreateInstance(type)!;
            Populate(step);
            var scopes = step.ChildScopes().ToArray();

            Assert.Equal(bodies.Count, scopes.Length);
            foreach (var body in bodies)
                Assert.Single(scopes, scope => ReferenceEquals(scope.Steps, body));

            void Populate(object owner)
            {
                foreach (var property in owner.GetType().GetProperties().Where(property => ContainsSteps(property.PropertyType)))
                    property.SetValue(owner, Make(property.PropertyType));
            }

            object Make(Type memberType)
            {
                var value = Activator.CreateInstance(memberType)!;
                if (value is List<WorkflowStepDocument> body)
                {
                    bodies.Add(body);
                    body.Add(new ActivityStepDocument { Activity = "nested" });
                }
                else if (value is IList list)
                {
                    var element = memberType.GetGenericArguments()[0];
                    for (var index = 0; index < 2; index++)
                    {
                        var child = Activator.CreateInstance(element)!;
                        Populate(child);
                        list.Add(child);
                    }
                }
                return value;
            }
        }
    }

    [Fact]
    public void TypeNamesKeepHandlerMetadataAndBodyTraversalOrder()
    {
        var document = new WorkflowDocument
        {
            Workflow = new() { DataType = "data" },
            Definitions = [new TryStepDocument
            {
                Steps = [new ActivityStepDocument { Activity = "try" }],
                Catches = [new() { Exception = "exception", FaultType = "fault", Steps =
                [
                    new SagaStepDocument
                    {
                        Steps = [new ActivityStepDocument { Activity = "saga" }],
                        OnError = [new() { Exception = "sagaException", FaultType = "sagaFault", CompensateUpTo = "target",
                            Steps = [new ActivityStepDocument { Activity = "handler" }] }]
                    }
                ] }],
                Finally = [new ActivityStepDocument { Activity = "finally" }]
            }]
        };

        Assert.Equal(["data", "exception", "fault", "try", "sagaException", "sagaFault", "target", "saga", "handler", "finally"],
            WorkflowSourceTranslator.TypeNames(document));
        Assert.Empty(new WaitForStepDocument().ChildScopes());
        Assert.Empty(new TryStepDocument().ChildScopes().Where(scope => scope.Kind == "finally"));
        Assert.Empty(new OutcomeStepDocument().ChildScopes());
    }

    private static bool ContainsSteps(Type type) => type == typeof(WorkflowStepDocument) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? ContainsSteps(type.GetGenericArguments()[0])
            : typeof(DocumentNode).IsAssignableFrom(type) &&
                type.GetProperties().Any(property => ContainsSteps(property.PropertyType)));
}
