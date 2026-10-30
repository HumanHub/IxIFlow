using System.Runtime.CompilerServices;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Core.Runtime;

internal static class InlineConstruction
{
    private static readonly AsyncLocal<IReadOnlyList<(string Name, int Version)>?> Building = new();
    private static readonly AsyncLocal<Type?> Executable = new();
    private static readonly ConditionalWeakTable<WorkflowStep, object> Owned = new();
    private static readonly ConditionalWeakTable<WorkflowDefinition, object> UsedDefinitions = new();

    internal static void Cataloged(WorkflowDefinition definition)
    {
        lock (Gate)
            UsedDefinitions.GetValue(definition, _ => new object());
    }

    private static readonly object Gate = new();

    internal static void Cataloged(WorkflowStep step)
    {
        lock (Gate)
            Owned.GetValue(step, _ => new object());
    }

    public static WorkflowDefinition Typed<TWorkflow, TData>()
        where TWorkflow : IWorkflow<TData>, new() where TData : class
    {
        var identity = WorkflowAttribute.For(typeof(TWorkflow));

        return Build(identity.Name, identity.Version, () =>
        {
            var previous = Executable.Value;
            Executable.Value = typeof(TWorkflow);

            try
            {
                TWorkflow workflow;

                try
                {
                    workflow = new TWorkflow();
                }
                catch (System.Reflection.TargetInvocationException error) when (error.InnerException is WorkflowEngineException inner)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();

                    throw;
                }

                var builder = Workflow.Create<TData>(identity.Name, identity.Version);

                builder.DisplayName(identity.DisplayName).Description(identity.Description ?? "").Tags(identity.Tags).Icon(identity.Icon)
                    .Category(identity.Category);

                workflow.Build(builder);

                return builder.Build();
            }
            finally
            {
                Executable.Value = previous;
            }
        });
    }

    internal static WorkflowInlinePin FactoryPin(Func<WorkflowDefinition> factory, WorkflowInlinePin pin)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.BundleDigest != null || pin.EntryType != null || pin.AssemblyName != null)
            return pin;

        // A factory is rebuilt by its containing executable; it need not declare a callee entry.
        var owner = Executable.Value ?? factory.Method.DeclaringType
            ?? throw Invalid("The inline factory has no executable provenance");

        return pin with { EntryType = owner.FullName, AssemblyName = owner.Assembly.GetName().Name };
    }

    public static WorkflowDefinition Factory(Func<WorkflowDefinition> factory, WorkflowInlinePin pin)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pin);
        var definition = Build(pin.Name, pin.Version, factory);

        if (WorkflowDefinitionValidator.Inspect(definition) != pin.StructureHash ||
            WorkflowDefinitionValidator.GetContract(definition).Hash != pin.ContractHash)
            throw Invalid("Inline workflow pin differs from the built definition");

        return definition;
    }

    private static WorkflowDefinition Build(string name, int version, Func<WorkflowDefinition> factory)
    {
        var previous = Building.Value ?? [];

        if (previous.Contains((name, version)))
            throw Invalid($"Inline cycle at '{name}' v{version}");

        if (previous.Count >= 16)
            throw Invalid("Inline workflow depth exceeds 16");

        Building.Value = [.. previous, (name, version)];

        try
        {
            var definition = factory() ?? throw Invalid("The callee factory returned no definition");

            if (definition.Name != name || definition.Version != version)
                throw Invalid("Inline workflow identity differs from the built definition");

            // Inner grafts have their own ownership. Claim this definition's namespace, including
            // the call sites, before its first catalog is materialized.
            var steps = ParallelRules.Flatten(definition.Steps, crossInline: false).ToArray();
            var inherited = new HashSet<WorkflowStep>(ReferenceEqualityComparer.Instance);

            foreach (var site in steps.Where(step => step.StepType == WorkflowStepType.InlineWorkflow))
            {
                var body = ParallelRules.Flatten(site.InlineSteps).ToArray();

                if (site.InlineBodyIdentity != null && body.All(site.InlineBodyIdentity.Contains))
                    inherited.UnionWith(body);
            }

            lock (Gate)
            {
                if (UsedDefinitions.TryGetValue(definition, out _) || steps.Any(step => Owned.TryGetValue(step, out _)) ||
                    ParallelRules.Flatten(definition.Steps).Any(step => Owned.TryGetValue(step, out _) && !inherited.Contains(step)))
                    throw Invalid("The callee factory must return a fresh definition for every call");

                UsedDefinitions.Add(definition, new object());

                foreach (var step in steps)
                    Owned.GetValue(step, _ => new object());
            }

            if (definition.Demands.Any(demand => demand.Evaluate != null || demand.Expression != null))
                throw Invalid("DemandFrom is not supported in an inline callee; declare it on the caller");

            BranchData.Validate(WorkflowDefinitionValidator.RequireDataType(definition), branches: true);
            WorkflowDefinitionValidator.Validate(definition);

            return definition;
        }
        finally
        {
            Building.Value = previous;
        }
    }

    internal static List<WorkflowDemand> MergeDemands(IEnumerable<WorkflowDemand> demands)
    {
        var merged = new Dictionary<string, WorkflowDemand>(StringComparer.Ordinal);

        foreach (var demand in demands)
        {
            if (demand.Evaluate != null)
            {
                continue;
            }

            if (merged.TryGetValue(demand.Key, out var prior))
            {
                if (prior.Value != null && demand.Value != null && prior.Value != demand.Value)
                    throw Invalid($"Conflicting inline demands for '{demand.Key}': '{prior.Value}' and '{demand.Value}'", "PUB011");

                static DateTime? Later(DateTime? a, DateTime? b) => a == null || b == null ? null : a > b ? a : b;
                var expiry = Later(prior.ExpiresAtUtc, demand.ExpiresAtUtc);

                var valueExpiry = prior.Value == null ? demand.ValueExpiresAtUtc ?? demand.ExpiresAtUtc
                    : demand.Value == null ? prior.ValueExpiresAtUtc ?? prior.ExpiresAtUtc
                    : Later(prior.ValueExpiresAtUtc ?? prior.ExpiresAtUtc, demand.ValueExpiresAtUtc ?? demand.ExpiresAtUtc);

                merged[demand.Key] = prior with
                {
                    Value = prior.Value ?? demand.Value, ExpiresAtUtc = expiry,
                    ValueExpiresAtUtc = valueExpiry == expiry ? null : valueExpiry, Inherit = prior.Inherit || demand.Inherit
                };
            }
            else
                merged.Add(demand.Key, demand);
        }

        return merged.Values.OrderBy(demand => demand.Key, StringComparer.Ordinal)
            .Concat(demands.Where(demand => demand.Evaluate != null)).ToList();
    }

    internal static string? PublishCode(Exception error) => error.Data[typeof(InlineConstruction)] as string;

    internal static WorkflowEngineException Invalid(string message, string code = "PUB013")
    {
        var error = new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, message);
        error.Data[typeof(InlineConstruction)] = code;

        return error;
    }
}