using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Core.Runtime;

internal static class WorkflowDefinitionIdentity
{
    public static void AssignImplicitName(WorkflowDefinition definition)
    {
        if (!string.IsNullOrEmpty(definition.Name))
            return;

        var signature = WorkflowTypeIdentity.StableName(definition.WorkflowDataType) + "|" +
            new WorkflowScopeCatalog(definition).Fingerprint;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        definition.Name = "unnamed:" + Convert.ToHexString(hash);
    }
}
