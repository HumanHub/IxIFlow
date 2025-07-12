using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     Builder for configuring suspend/resume operations in workflows
/// </summary>
public interface ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Configures an input property for the resume event
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the resume event
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property);
}

/// <summary>
///     Builder for configuring input properties of suspend/resume operations
/// </summary>
public interface ISuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>
    : ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies the source of the input value for suspend/resume operations
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring output properties of suspend/resume operations
/// </summary>
public interface ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>
    : ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies where to store the output value from suspend/resume operations
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}