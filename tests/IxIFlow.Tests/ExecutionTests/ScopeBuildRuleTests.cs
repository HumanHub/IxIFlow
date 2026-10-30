using System.ComponentModel.DataAnnotations;
using IxIFlow.Builders;
using IxIFlow.Core;
using Xunit;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeBuildRuleTests
{
    [Fact]
    public void MultipleValueInterfacesAreRefused() => Assert.Throws<WorkflowEngineException>(() =>
        new WorkflowBuilder<Data, object>([], "multiple").Scope<Multiple>(null, _ =>
        {
        }).Build());

    [Fact]
    public void DualActivityInterfacesAreRefused() => Assert.Throws<WorkflowEngineException>(() =>
        new WorkflowBuilder<Data, object>([], "dual").Scope<Dual>(null, _ =>
        {
        }).Build());

    [Fact]
    public void PublicValuePropertyIsRefused() => Assert.Throws<WorkflowEngineException>(() =>
        new WorkflowBuilder<Data, object>([], "property").Scope<Leaking>(null, _ =>
        {
        }).Build());

    [Fact]
    public void DelegateInputIsRefused() => Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "delegate")
        .Scope<DelegateScope>(null, _ =>
        {
        }).Build());

    [Fact]
    public void RequiredInputMustBeMapped() => Assert.Throws<WorkflowEngineException>(() =>
        new WorkflowBuilder<Data, object>([], "required").Scope<RequiredScope>(null, _ =>
        {
        }).Build());

    [Fact]
    public void GetterOnlyConfigurationCannotBeRestored() => Assert.Throws<WorkflowEngineException>(() =>
        WorkflowDefinitionValidator.Validate(new WorkflowBuilder<Data, object>([], "getter").Scope<GetterScope>(null, _ =>
        {
        }).Build()));

    [Fact]
    public void AffineRequirementCannotBeWeakened() => Assert.Throws<WorkflowEngineException>(() =>
        new WorkflowBuilder<Data, object>([], "weaken").Scope<AffineScope>(s => s.Threading(ScopeThreading.AnyThread), _ =>
        {
        }).Build());

    [Fact]
    public void InvalidModesAreRefused() => Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "mode")
        .Scope<InvalidScope>(null, _ =>
        {
        }).Build());

    [Fact]
    public void RequirementOutsideScopeIsRefused() =>
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("requirement").Step<RequiredActivity>().Build());

    [Fact]
    public void RootRequirementSatisfiesLexicalValidationAndEntersContract()
    {
        var definition = Workflow.Create<Data>("root-requirement").RequiresScope<Value>().Step<RequiredActivity>().Build();
        Assert.Equal(typeof(Value), Assert.Single(definition.RequiredScopes));
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Single(WorkflowContract.Parse(contract.ToJson()).RequiredScopes);
    }

    [Fact]
    public void RequirementIsCheckedInSwitchGraphAndFinallyBodies()
    {
        Assert.Throws<WorkflowEngineException>(() =>
            new WorkflowBuilder<Data, object>([], "switch-required")
                .Switch(c => c.WorkflowData.Number, cases => cases.Case(0, b => b.Step<RequiredActivity>())).Build());

        Assert.Throws<WorkflowEngineException>(() =>
            new WorkflowBuilder<Data, object>([], "graph-required").Flowchart(f => f.Start("a").Node("a", b => b.Step<RequiredActivity>()))
                .Build());

        Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "finally-required").Try(_ =>
        {
        }).Finally(b => b.Step<RequiredActivity>()).Build());
    }

    [Fact]
    public void ScopeConfigurationAndStrengtheningChangeTheStructureHash()
    {
        var first = new WorkflowBuilder<Data, object>([], "hash").Scope<PlainScope>(null, _ =>
        {
        }).Build();

        var second = new WorkflowBuilder<Data, object>([], "hash").Scope<PlainScope>(s => s.Threading(ScopeThreading.ThreadAffine), _ =>
        {
        }).Build();

        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(second));
        var clone = Assert.Single(second.Steps).Clone();
        Assert.Equal(ScopeThreading.ThreadAffine, clone.ScopeThreading);
        Assert.Equal(typeof(Value), clone.ScopeValueType);
    }

    public sealed class Data
    {
        public int Number { get; set; }
    }

    public class Value;

    public sealed class Other;

    public class PlainScope : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new Value());

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed class Multiple : PlainScope, IScopeActivity<Other>
    {
        Task<Other> IScopeActivity<Other>.OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new Other());

        public Task CloseAsync(Other value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    public sealed class Dual : PlainScope, IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class Leaking : PlainScope
    {
        public Value? Session { get; set; }
    }

    public sealed class DelegateScope : PlainScope
    {
        public Func<int>? Read { get; set; }
    }

    public sealed class RequiredScope : PlainScope
    {
        [Required] public string? Account { get; set; }
    }

    public sealed class GetterScope : PlainScope
    {
        public int Number => 1;
    }

    [ScopeThreading(ScopeThreading.ThreadAffine)]
    public sealed class AffineScope : PlainScope;

    [ScopeConcurrency((ScopeConcurrency)999)]
    public sealed class InvalidScope : PlainScope;

    [RequiresScope<Value>]
    public sealed class RequiredActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}