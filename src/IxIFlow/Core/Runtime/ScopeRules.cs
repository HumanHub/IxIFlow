using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

internal static class ActivityTypes
{
    public static bool IsActivity(Type type) =>
        typeof(IAsyncActivity).IsAssignableFrom(type) || typeof(IScopeActivity).IsAssignableFrom(type);

    public static Type[] ScopeTypes(Type type) => type.GetInterfaces().Where(item => item.IsGenericType &&
        item.GetGenericTypeDefinition() == typeof(IScopeActivity<>)).Select(item => item.GenericTypeArguments[0]).ToArray();

    public static Type[] Requirements(Type type) => type.GetCustomAttributes(true).Select(item => item.GetType())
        .Where(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(RequiresScopeAttribute<>))
        .Select(item => item.GenericTypeArguments[0]).Distinct().ToArray();

    public static Type ValidateScope(Type type)
    {
        var values = ScopeTypes(type);

        if (values.Length != 1 || typeof(IAsyncActivity).IsAssignableFrom(type))
            throw Invalid(type, "requires exactly one IScopeActivity<TValue> and cannot implement IAsyncActivity");

        if (!Enum.IsDefined(Concurrency(type)) || !Enum.IsDefined(Threading(type)))
            throw Invalid(type, "has an invalid scope concurrency or threading declaration");

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (values[0].IsAssignableFrom(property.PropertyType))
                throw Invalid(type, $"property '{property.Name}' may not hold its live scope value");

            if (ActivityContract.IsInput(property, type.GetProperties().Any(ActivityContract.IsMarked)) &&
                typeof(Delegate).IsAssignableFrom(property.PropertyType))
                throw Invalid(type, $"input '{property.Name}' cannot be a delegate: lifecycle code has no workflow data");
        }

        return values[0];
    }

    public static ScopeConcurrency Concurrency(Type type) =>
        type.GetCustomAttribute<ScopeConcurrencyAttribute>()?.Mode ?? ScopeConcurrency.Serialized;

    public static ScopeThreading Threading(Type type) =>
        type.GetCustomAttribute<ScopeThreadingAttribute>()?.Mode ?? ScopeThreading.AnyThread;

    private static WorkflowEngineException Invalid(Type type, string message) =>
        new(WorkflowErrorKind.InvalidDefinition, $"Activity '{type.Name}' {message}");
}

internal static class ScopeDeclarations
{
    private static readonly ConditionalWeakTable<List<WorkflowStep>, List<Type>> Values = new();
    public static List<Type> For(List<WorkflowStep> steps) => Values.GetValue(steps, _ => []);
}

internal static class ScopeRules
{
    public static void Validate(WorkflowDefinition definition) => Walk(definition.Steps, definition.RequiredScopes, false);
    private static readonly AsyncLocal<HashSet<Type>?> CheckingChildren = new();

    private static void CheckTypedChild(WorkflowStep step)
    {
        if (step.StepType != WorkflowStepType.WorkflowInvocation ||
            step.WorkflowType is not { } type || type.GetConstructor(Type.EmptyTypes) == null)
            return;

        var checking = CheckingChildren.Value ??= [];

        if (!checking.Add(type))
            return;

        try
        {
            var builderType = typeof(WorkflowBuilder<>).MakeGenericType(step.InvokedDataType!);
            var builder = Activator.CreateInstance(builderType, step.WorkflowName!, step.WorkflowVersion!.Value)!;
            var contract = typeof(IxIFlow.Builders.Interfaces.IWorkflow<>).MakeGenericType(step.InvokedDataType!);
            object? workflow;

            // Identity discovery must also work for types whose constructors need a host.
            // Registration validates their actual definitions using the real provider.
            try
            {
                workflow = Activator.CreateInstance(type);
            }
            catch (TargetInvocationException)
            {
                return;
            }

            contract.GetMethod("Build")!.Invoke(workflow, [builder]);
            var definition = (WorkflowDefinition)builderType.GetMethod("Build")!.Invoke(builder, null)!;

            if (definition.RequiredScopes.Count != 0)
                throw WorkflowBuildError.Invalid(step, "An independent child invocation cannot supply required scopes");

            step.InvokedContract = step.InvokedContract!.WithRequiredScopes(definition.RequiredScopes);
            step.InvokedContractHash = step.InvokedContract.Hash;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
        finally
        {
            checking.Remove(type);
        }
    }

    private static void Walk(IEnumerable<WorkflowStep> steps, IReadOnlyList<Type> available, bool saga)
    {
        foreach (var step in steps)
        {
            CheckTypedChild(step);

            if (step.ActivityType is { } activity)
            {
                if (step.StepType == WorkflowStepType.Scope)
                {
                    if (saga)
                        throw WorkflowBuildError.Invalid(step, "Scope activities are not supported inside a saga body");

                    if (ActivityTypes.ValidateScope(activity) != step.ScopeValueType ||
                        step.ScopeConcurrency != ActivityTypes.Concurrency(activity) || !Enum.IsDefined(step.ScopeThreading) ||
                        step.ScopeThreading < ActivityTypes.Threading(activity))
                        throw WorkflowBuildError.Invalid(step, "Scope value, concurrency or threading disagrees with its implementation");

                    foreach (var mapping in step.InputMappings)
                        if (typeof(Delegate).IsAssignableFrom(mapping.SourceType))
                            throw WorkflowBuildError.Invalid(step, $"Scope input '{mapping.TargetProperty}' cannot be a delegate");

                    if (step.ScopeRetryPolicy is { } retry && IxIFlow.Authoring.RetryDelayRules.Validate(retry.MaximumAttempts,
                            retry.InitialInterval, retry.MaximumInterval, retry.BackoffCoefficient) is { } problem)
                        throw WorkflowBuildError.Invalid(step, problem);
                }
                else if (typeof(IScopeActivity).IsAssignableFrom(activity))
                    throw WorkflowBuildError.Invalid(step, "A scope activity requires Scope, and cannot execute as an ordinary activity");
                else if (activity.IsDefined(typeof(ScopeThreadingAttribute)) || activity.IsDefined(typeof(ScopeConcurrencyAttribute)))
                    throw WorkflowBuildError.Invalid(step, "Scope threading and concurrency declarations require a scope implementation");

                Require(step, ActivityTypes.Requirements(activity), available);
            }

            if (step.StepMetadata.GetValueOrDefault("CompensationActivities") is List<CompensationActivityInfo> compensations)
                foreach (var compensation in compensations)
                    Require(step, ActivityTypes.Requirements(compensation.ActivityType), available);

            if (step.InvokedContract is { } contract)
            {
                if (step.StepType == WorkflowStepType.WorkflowInvocation && contract.RequiredScopes.Count != 0)
                    throw WorkflowBuildError.Invalid(step,
                        "An independent child invocation cannot supply required scopes; use an inline workflow");

                Require(step, contract.RequiredScopes.Select(item => WorkflowTypeIdentity.Resolve(item.Type) ??
                    throw WorkflowBuildError.Invalid(step, $"Required scope type '{item.Type}' is unavailable")), available);
            }

            foreach (var child in step.ChildScopes())
                Walk(child.Steps, child.Kind == "scope" ? [.. available, step.ScopeValueType!] : available,
                    saga || step.StepType == WorkflowStepType.Saga);
        }
    }

    private static void Require(WorkflowStep step, IEnumerable<Type> required, IReadOnlyList<Type> available)
    {
        foreach (var type in required)
            if (!available.Any(type.IsAssignableFrom))
                throw WorkflowBuildError.Invalid(step, $"Step '{step.Name}' requires an enclosing scope providing '{type}'");
    }
}