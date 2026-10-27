using System.Reflection;
using System.Runtime.ExceptionServices;

namespace IxIFlow.Core.Runtime;

/// <summary>Only exceptions thrown inside authored accessors belong to workflow code.</summary>
internal static class ActivityPropertyAccess
{
    internal static object? GetValue(PropertyInfo property, object activity)
    {
        try
        {
            var value = property.GetValue(activity);
            WorkflowDefinitionScope.ThrowIfDispatchFailed();
            return value;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            WorkflowDefinitionScope.ThrowIfDispatchFailed();
            ExceptionDispatchInfo.Capture(WorkflowEngineException.FromWorkflowCode(error.InnerException)).Throw();
            throw;
        }
    }

    internal static void SetValue(PropertyInfo property, object activity, object? value)
    {
        try
        {
            property.SetValue(activity, value);
            WorkflowDefinitionScope.ThrowIfDispatchFailed();
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            WorkflowDefinitionScope.ThrowIfDispatchFailed();
            ExceptionDispatchInfo.Capture(WorkflowEngineException.FromWorkflowCode(error.InnerException)).Throw();
            throw;
        }
    }
}
