using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     Builder for configuring suspend/resume operations in workflows
/// </summary>
public interface IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Maps a value into the suggested event payload once when the wait opens.
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the resume event
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property);

    /// <summary>Declares the initial suggested answer for UIs, overlaid by input mappings when the wait opens.</summary>
    IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> DefaultPayload(TResumeEvent payload);

    IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> TimeoutAfter(
        TimeSpan duration);

    IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> OnTimeout(
        Action<IFaultTimeoutWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure);
}

/// <summary>
///     Builder for configuring input properties of suspend/resume operations
/// </summary>
public interface IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    : IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies the source of the input value for suspend/resume operations
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> From(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring output properties of suspend/resume operations
/// </summary>
public interface IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    : IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies where to store the output value from suspend/resume operations
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}