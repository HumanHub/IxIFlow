using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class Contracts
{
    /// <summary>Tools and applications can validate a definition before starting it.</summary>
    public static WorkflowDefinition ValidateQuote()
    {
        var definition = Quote.Definition();
        WorkflowDefinitionValidator.Validate(definition);
        return definition;
    }

    /// <summary>The catalog exposes the same input, output and designer metadata as the activity contract.</summary>
    public static ActivityPackageRegistry Catalog()
    {
        var catalog = new ActivityPackageRegistry();
        catalog.AddAssembly(typeof(AddFee).Assembly, "IxIFlow.Samples", "1.0.0");
        return catalog;
    }
}
