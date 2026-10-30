using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class WorkflowDataTypeRegistrationTests
{
    [Theory]
    [InlineData("code")]
    [InlineData("catalog")]
    [InlineData("startup")]
    public async Task UntypedDefinitionIsRefusedBeforeAnyStoreCall(string entry)
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        var definition = new WorkflowDefinition { Name = "MissingDataType", Version = 3, Steps = [] };

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
        {
            switch (entry)
            {
                case "code":
                    using (var catalog = new WorkflowCatalog(store.Object))
                        await catalog.RegisterWorkflowAsync(definition);

                    break;
                case "catalog":
                    using (var catalog = new WorkflowCatalog(store.Object, [definition]))
                    {
                    }

                    break;
                case "startup":
                    using (var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services
                               .AddSingleton<IWorkflowStateRepository>(store.Object).AddSingleton(definition)
                               .BuildServiceProvider())
                        _ = provider.GetRequiredService<IWorkflowCatalog>();

                    break;
            }
        });

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Equal("Workflow data type is required for 'MissingDataType' v3", error.Message);
        store.VerifyNoOtherCalls();
    }
}