using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for configuring output properties of compensation activities
/// </summary>
public interface ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> 
    : ICompensationActivityBuilder<TWorkflowData, TCompensationActivity>
    where TCompensationActivity : IAsyncActivity
{
    /// <summary>
    /// Specifies where to store the output value from the compensation activity
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}
