using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Builds the compensation of a saga request that reads the request's answer.</summary>
public sealed class RequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply>(
    CompensationActivityInfo compensationInfo)
    : IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
    where TReply : class
{
    public IRequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData,
        TReply> Input<TProperty>(Expression<Func<TCompensationActivity, TProperty>> property) =>
        new RequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData,
            TReply>(this, compensationInfo, property);

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property) =>
        new CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty>(
            this, compensationInfo, property);
}

/// <summary>Maps one input of a request's compensation.</summary>
public sealed class RequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty,
    TCurrentStepData, TReply>(
    IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply> builder,
    CompensationActivityInfo compensationInfo,
    Expression<Func<TCompensationActivity, TProperty>> property)
    : IRequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData, TReply>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
    where TReply : class
{
    public IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData, TReply> From(
        Expression<Func<RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply>, TProperty>> source)
    {
        if (property.Body is not MemberExpression member)
            throw new ArgumentException("Expression must be a member access", nameof(property));
        var compiled = source.Compile();
        compensationInfo.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = member.Member.Name,
            SourceFunction = context => context is RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply> typed
                ? compiled(typed)
                : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"Expected {typeof(RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply>).Name}, " +
                    $"got {context.GetType().Name}"),
            Expression = source,
            ContextFactory = Core.Runtime.WorkflowStepDelegates.RequestCompensation<TWorkflowData, TCurrentStepData, TReply>,
            SourceType = typeof(RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply>),
            CompensationSource = CompensationInputSource.CurrentStepContext,
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Input
        });
        return builder;
    }

    public IRequestCompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty, TCurrentStepData,
        TReply> Input<TNewProperty>(Expression<Func<TCompensationActivity, TNewProperty>> next) => builder.Input(next);

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Output<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> next) => builder.Output(next);
}
