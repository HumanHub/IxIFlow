using IxIFlow.Core;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Generators.Tests;

public sealed partial class YamlParityTests
{
    [Fact]
    [Trait("Feature", "authoring")]
    public void SagaRequestOwnErrorPolicyMatchesTheFluentContract()
    {
        var generated = Build(
            """
            - kind: saga
              id: saga
              steps:
                - kind: request
                  id: request
                  key: approval
                  activity: ParityRequest
                  onError: [{exception: ParityFailure, then: ignore}]
                - kind: waitFor
                  id: reply
                  key: approval
                  event: Answer
            """);

        var fluent = Start().Saga(saga => saga
            .Request<ParityRequest>("approval", step => step.OnError<ParityFailure>(handler => handler.ThenIgnore())).WithId("request")
            .WaitFor<Answer>("approval").WithId("reply")).WithId("saga").Build();

        Assert.Equal(Shape(fluent), Shape(generated));
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
        {
            var request = Assert.Single(ParallelRules.Flatten(definition.Steps), step => step.ActivityType == typeof(ParityRequest));
            var policy = Assert.Single((IEnumerable<StepErrorHandlerInfo>)request.StepMetadata["StepErrorHandlers"]);
            Assert.Equal(typeof(ParityFailure), policy.ExceptionType);
            Assert.Equal("Ignore", policy.HandlerAction.ToString());
        }
    }
}