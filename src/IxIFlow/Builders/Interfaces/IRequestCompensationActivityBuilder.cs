using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures the compensation of a saga request that reads the request's answer: its inputs map from
/// a <see cref="RequestCompensationContext{TWorkflowData,TCurrentStepData,TReply}"/>, whose <c>Reply</c> is the
/// answer the request received and <c>Answered</c> whether there was one.
/// </summary>
public interface IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply>
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
    where TReply : class
{
    IRequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData, TReply>
        Input<TProperty>(Expression<Func<TCompensationActivity, TProperty>> property);

    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property);
}

/// <summary>Maps one input of a request's compensation from the request's answer, its output or the data.</summary>
public interface IRequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty,
    TCurrentStepData, TReply>
    : IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply>
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
    where TReply : class
{
    IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply> From(
        Expression<Func<RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply>, TProperty>> source);
}
