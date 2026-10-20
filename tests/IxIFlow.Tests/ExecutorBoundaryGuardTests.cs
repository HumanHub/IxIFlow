using System.Reflection;
using System.Reflection.Emit;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests;

/// <summary>
/// The executor contains only transition logic. The store, leases, the engine and other
/// instances are reached only through the host (spec A-01).
/// </summary>
[Trait("Feature", "guard")]
[Trait("Spec", "A-01")]
public sealed class ExecutorBoundaryGuardTests
{
    private static readonly Type[] Executor =
    [
        typeof(WorkflowExecutor), typeof(ActivityAttempts), typeof(ContinuationTransitions),
        typeof(ExecutionCheckpointWriter), typeof(IExecutionHost), typeof(ExecutionSession),
        typeof(IWorkflowDefinitionSource), typeof(WorkflowInstances), typeof(EventIntake)
    ];

    private static readonly Type[] HostOnly =
    [
        typeof(IWorkflowStateRepository), typeof(IWorkflowOutbox), typeof(GuardedStateRepository),
        typeof(WorkflowExecutionLease), typeof(InProcessInstanceGate), typeof(IWorkflowClient),
        typeof(IWorkflowCatalog), typeof(WorkflowHost.InstanceOperations), typeof(WorkflowHost.SlotClient),
        typeof(WorkflowHost), typeof(HostedWorkflowClient)
    ];

    [Fact]
    public void ExecutorReachesTheStoreLeaseEngineAndChildrenOnlyThroughTheHost()
    {
        var violations = Executor
            .SelectMany(type => ReferencedTypes(type).Where(IsHostOnly)
                .Select(referenced => $"{type.Name} uses {referenced.Name}"))
            .Distinct()
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ScanFindsHostDependencies()
    {
        // Guards the scan itself: the host does use the store, the lease and the registry.
        var referenced = ReferencedTypes(typeof(WorkflowHost.InstanceOperations)).ToHashSet();

        Assert.Contains(typeof(IWorkflowStateRepository), referenced);
        Assert.Contains(typeof(WorkflowExecutionLease), referenced);
        Assert.Contains(typeof(IWorkflowCatalog), referenced);
    }

    private static bool IsHostOnly(Type type) =>
        HostOnly.Any(hostType => hostType == type ||
            hostType.IsGenericTypeDefinition && type.IsGenericType && type.GetGenericTypeDefinition() == hostType);

    // Every type a class and its compiler-generated nested types mention: in fields, signatures
    // and method bodies.
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var referenced in ReferencedTypes(nested))
            yield return referenced;
        foreach (var field in type.GetFields(all))
        foreach (var referenced in Expand(field.FieldType))
            yield return referenced;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var signature = method.GetParameters().Select(parameter => parameter.ParameterType)
                .Concat(method is MethodInfo info ? [info.ReturnType] : []);
            foreach (var referenced in signature.SelectMany(Expand))
                yield return referenced;
            foreach (var referenced in BodyReferences(method).SelectMany(Expand))
                yield return referenced;
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var element in Expand(type.GetElementType()!))
                yield return element;
            yield break;
        }
        yield return type;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
                yield return argument;
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static IEnumerable<Type> BodyReferences(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            yield break;
        var typeArguments = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE)
                value = (short)(0xFE00 | il[position++]);
            var code = OpCodesByValue[value];
            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, position);
                    var member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                    if (member is Type referencedType)
                        yield return referencedType;
                    else if (member != null)
                    {
                        yield return member.DeclaringType!;
                        if (member is FieldInfo field)
                            yield return field.FieldType;
                        if (member is MethodInfo called)
                        {
                            yield return called.ReturnType;
                            foreach (var parameter in called.GetParameters())
                                yield return parameter.ParameterType;
                        }
                    }
                    position += 4;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + 4 * BitConverter.ToInt32(il, position);
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                default:
                    position += 4;
                    break;
            }
        }
    }
}
