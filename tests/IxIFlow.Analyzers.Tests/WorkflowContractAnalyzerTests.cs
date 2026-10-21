using static IxIFlow.Analyzers.Tests.Verify;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
public sealed class WorkflowContractAnalyzerTests
{
    [Fact]
    public async Task MarkedChildDataRequiresDeclaredMappingsAndRequiredInputs()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Required { get; set; }
                [WorkflowOutput] public int Result { get; set; }
                public int Other { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .{|#0:Invoke<Child>|}("Child", 1, setup => setup
                        .Input(data => data.{|#1:Other|}).From(_ => 1)
                        .Output(data => data.{|#2:Other|}).To(ctx => ctx.WorkflowData.Value)).Build();
            }
            """, Diagnostic(Descriptors.WorkflowRequiredInputNotMapped, 0, "Child.Required"),
            Diagnostic(Descriptors.WorkflowNotAnInput, 1, "Child.Other"), Diagnostic(Descriptors.WorkflowNotAnOutput, 2, "Child.Other"));
    }

    [Fact]
    public async Task ExplicitContractsAllowMixedMarkersAndListsAndSuppressRequiredDiagnostics()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Required { get; set; }
                public int Listed { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Build();
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), setup => setup
                            .Input(data => data.Listed).From(_ => 1)
                            .Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value)).Build();
                }
            }
            """);
    }

    [Fact]
    public async Task ExplicitContractsNeverSuppressAccessorErrors()
    {
        await Analyzer<MappingAnalyzer>("""
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child { public int ReadOnly => 1; public int WriteOnly { internal get; set; } }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .Invoke<Child>("Child", 1, new WorkflowContract([], [], true), setup => setup
                        .Input(data => data.{|#0:ReadOnly|}).From(_ => 1)
                        .Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value)).Build();
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Fact]
    public async Task UnmarkedDataIsConservativeEvenWhenItImplementsActivity()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child : IAsyncActivity
            {
                [ActivityInput, Required] public int ActivityOnly { get; set; }
                public int Other { get; set; }
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Child>(step => step.Input(data => data.ActivityOnly).From(_ => 1))
                    .Invoke<Child>("Child", 1, setup => setup.Input(data => data.Other).From(_ => 1)).Build();
            }
            """);
    }

    [Fact]
    public async Task UnmarkedDataDoesNotGuessTheEffectiveDeclarations()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { }
            public sealed class Child
            {
                [Required] public int {|#0:ReadOnly|} => 1;
                [DefaultValue("bad")] public int {|#1:Count|} { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>().Invoke<Child>("Child", 1).Build();
            }
            """, Diagnostic(Descriptors.WorkflowRequiredOnOutput, 0, "Child.ReadOnly"),
            Diagnostic(Descriptors.WorkflowInvalidDefault, 1, "Child.Count", "Int32"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractedExplicitContractSetupsOnlyReportAccessorErrors(bool localDelegate)
    {
        var setup = """
            setup.Input(data => data.Listed).From(_ => 1);
            setup.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);
            setup.Input(data => data.{|#0:ReadOnly|}).From(_ => 1);
            setup.Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value);
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Required { get; set; }
                public int Listed { get; set; }
                public int ReadOnly => 1;
                public int WriteOnly { internal get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, new WorkflowContract([], [], true), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DelegateInvokeInsideExtractedExplicitContractSetupsLeavesTheContractUnknown(bool localDelegate, bool explicitInvoke)
    {
        var setup = $$"""
            Action<Action> run = action => action();
            {{(explicitInvoke ? "run.Invoke" : "run")}}(() => setup.Input(data => data.Listed).From(_ => 1));
            run(() => setup.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value));
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                public int Listed { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Output(data => data.Listed).Build();
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
            }
            """);
    }

    [Fact]
    public async Task MappingsInAnInvokeReceiverDoNotUseThatCallsContract()
    {
        await Analyzer<MappingAnalyzer>("""
            using IxIFlow.Builders.Interfaces;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                public int Listed { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Build();
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) =>
                    Builder(() => setup.Input(data => data.Listed).From(_ => 1)).Invoke<Child>("Child", 1);
                private static IWorkflowBuilder<Data, Noop> Builder(Action configure) => Workflow.Create<Data>("Other").Step<Noop>();
            }
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedSetupInsideAnotherInvocationsSetupOnlyReportsAccessorErrors(bool localDelegate)
    {
        var setup = """
            var other = Workflow.Create<Data>("Other").Step<Noop>();
            other.Invoke<NoMembers>("Empty", 1, ignored => setup.Input(data => data.Listed).From(_ => 1));
            other.Invoke<NoMembers>("Empty", 1, ignored =>
            {
                setup.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);
                setup.Input(data => data.{|#0:ReadOnly|}).From(_ => 1);
                setup.Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value);
            });
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            public sealed class Data { public int Value { get; set; } }
            public sealed class NoMembers { }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                public int Listed { get; set; }
                public int ReadOnly => 1;
                public int WriteOnly { internal get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Output(data => data.Listed).Build();
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Theory]
    [InlineData("assignment", false, false)]
    [InlineData("assignment", false, true)]
    [InlineData("assignment", true, false)]
    [InlineData("assignment", true, true)]
    [InlineData("ref", false, false)]
    [InlineData("ref", false, true)]
    [InlineData("ref", true, false)]
    [InlineData("ref", true, true)]
    [InlineData("out", false, false)]
    [InlineData("out", false, true)]
    [InlineData("out", true, false)]
    [InlineData("out", true, true)]
    [InlineData("tuple", false, false)]
    [InlineData("tuple", false, true)]
    [InlineData("tuple", true, false)]
    [InlineData("tuple", true, true)]
    public async Task WrittenSetupParametersLeaveTheContractUnknown(string write, bool writeAfter, bool localDelegate)
    {
        var assignment = write switch
        {
            "ref" => "ReplaceRef(ref ignored, setup);",
            "out" => "ReplaceOut(out ignored, setup);",
            "tuple" => "(ignored, _) = (setup, 0);",
            _ => "ignored = setup;"
        };
        var setup = $$"""
            var other = Workflow.Create<Data>("Other").Step<Noop>();
            other.Invoke<Child>("Other", 1, ignored =>
            {
                {{(writeAfter ? "" : assignment)}}
                ignored.Input(data => data.Listed).From(_ => 1);
                var continuation = ignored.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);
                continuation.Input(data => data.{|#0:ReadOnly|}).From(_ => 1);
                continuation.Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value);
                {{(writeAfter ? assignment : "")}}
            });
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Marked { get; set; }
                public int Listed { get; set; }
                public int ReadOnly => 1;
                public int WriteOnly { internal get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Output(data => data.Listed).Build();
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
                private static void ReplaceRef(ref IWorkflowInvocationSetupBuilder<Data, Child, Noop> target,
                    IWorkflowInvocationSetupBuilder<Data, Child, Noop> value) => target = value;
                private static void ReplaceOut(out IWorkflowInvocationSetupBuilder<Data, Child, Noop> target,
                    IWorkflowInvocationSetupBuilder<Data, Child, Noop> value) => target = value;
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Theory]
    [InlineData("ref-local", false, false)]
    [InlineData("ref-local", false, true)]
    [InlineData("ref-local", true, false)]
    [InlineData("ref-local", true, true)]
    [InlineData("helper", false, false)]
    [InlineData("helper", false, true)]
    [InlineData("helper", true, false)]
    [InlineData("helper", true, true)]
    [InlineData("in", false, false)]
    [InlineData("alias", false, false)]
    [InlineData("lambda", false, false)]
    [InlineData("local-function", false, false)]
    [InlineData("other-member", false, false)]
    public async Task NonReceiverSetupParameterUsesLeaveTheWholeContractUnknown(string use, bool useAfter, bool localDelegate)
    {
        var escape = use switch
        {
            "ref-local" => "ref var alias = ref ignored; alias = setup;",
            "helper" => "Configure(ignored);",
            "in" => "Inspect(in ignored);",
            "alias" => "var alias = ignored;",
            "lambda" => "Action later = () => ignored.Input(data => data.Listed).From(_ => 1);",
            "local-function" => "void Later() => ignored.Input(data => data.Listed).From(_ => 1);",
            _ => "ignored.ToString();"
        };
        var setup = $$"""
            var other = Workflow.Create<Data>("Other").Step<Noop>();
            other.Invoke<Child>("Other", 1, ignored =>
            {
                {{(useAfter ? "" : escape)}}
                ignored.Input(data => data.Listed).From(_ => 1);
                var continuation = ignored.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);
                continuation.Input(data => data.{|#0:ReadOnly|}).From(_ => 1);
                continuation.Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value);
                {{(useAfter ? escape : "")}}
            });
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Marked { get; set; }
                public int Listed { get; set; }
                public int ReadOnly => 1;
                public int WriteOnly { internal get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Output(data => data.Listed).Build();
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
                private static void Configure(IWorkflowInvocationSetupBuilder<Data, Child, Noop> builder) =>
                    builder.Input(data => data.Marked).From(_ => 1);
                private static void Inspect(in IWorkflowInvocationSetupBuilder<Data, Child, Noop> builder) { }
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Fact]
    public async Task NameofSetupParameterUsesKeepTheCallerViewKnown()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Marked { get; set; }
                public int Other { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .{|#0:Invoke<Child>|}("Child", 1, setup =>
                    {
                        _ = nameof(setup);
                        Func<string> label = () => nameof(setup);
                        string Label() => nameof(setup);
                        setup.Input(data => data.{|#1:Other|}).From(_ => 1)
                            .Output(data => data.{|#2:Other|}).To(ctx => ctx.WorkflowData.Value);
                    }).Build();
            }
            """, Diagnostic(Descriptors.WorkflowRequiredInputNotMapped, 0, "Child.Marked"),
            Diagnostic(Descriptors.WorkflowNotAnInput, 1, "Child.Other"),
            Diagnostic(Descriptors.WorkflowNotAnOutput, 2, "Child.Other"));
    }

    [Theory]
    [InlineData("ref-local", false, false)]
    [InlineData("ref-local", false, true)]
    [InlineData("ref-local", true, false)]
    [InlineData("ref-local", true, true)]
    [InlineData("ref-argument", false, false)]
    [InlineData("ref-argument", false, true)]
    [InlineData("ref-argument", true, false)]
    [InlineData("ref-argument", true, true)]
    [InlineData("out", false, false)]
    [InlineData("in", false, false)]
    [InlineData("argument", false, false)]
    [InlineData("assignment-target", false, false)]
    [InlineData("assignment-source", false, false)]
    [InlineData("lambda", false, false)]
    [InlineData("local-function", false, false)]
    [InlineData("other-member", false, false)]
    [InlineData("earlier-link", true, false)]
    [InlineData("deferred-initializer", false, false)]
    public async Task NonReceiverContinuationUsesLeaveTheWholeContractUnknown(string use, bool useAfter, bool localDelegate)
    {
        var escape = use switch
        {
            "ref-local" => "ref var alias = ref continuation; alias = setup;",
            "ref-argument" => "ReplaceRef(ref continuation, setup);",
            "out" => "ReplaceOut(out continuation, setup);",
            "in" => "Inspect(in continuation);",
            "argument" => "Configure(continuation);",
            "assignment-target" => "continuation = setup;",
            "assignment-source" => "var alias = continuation;",
            "lambda" => "Action later = () => continuation.Input(data => data.Listed).From(_ => 1);",
            "local-function" => "void Later() => continuation.Input(data => data.Listed).From(_ => 1);",
            "earlier-link" => "ref var alias = ref first; alias = setup;",
            "deferred-initializer" => "",
            _ => "continuation.ToString();"
        };
        var setup = $$"""
            var other = Workflow.Create<Data>("Other").Step<Noop>();
            other.Invoke<Child>("Other", 1, ignored =>
            {
                ignored.Input(data => data.Listed).From(_ => 1);
                {{(use == "earlier-link" ? "var first = ignored.Input(data => data.Marked).From(_ => 1); var continuation = first.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);" :
                    use == "deferred-initializer" ? "IWorkflowInvocationSetupBuilder<Data, Child, Noop> continuation; continuation = ignored.Input(data => data.Marked).From(_ => 1);" :
                    "var continuation = ignored.Input(data => data.Marked).From(_ => 1);")}}
                {{(useAfter ? "" : escape)}}
                continuation.Input(data => data.Listed).From(_ => 1);
                continuation.Output(data => data.Listed).To(ctx => ctx.WorkflowData.Value);
                continuation.Input(data => data.{|#0:ReadOnly|}).From(_ => 1);
                continuation.Output(data => data.{|#1:WriteOnly|}).To(ctx => ctx.WorkflowData.Value);
                {{(useAfter ? escape : "")}}
            });
            """;
        await Analyzer<MappingAnalyzer>($$"""
            using IxIFlow.Builders.Interfaces;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                [WorkflowInput, Required] public int Required { get; set; }
                public int Listed { get; set; }
                public int ReadOnly => 1;
                public int WriteOnly { internal get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build()
                {
                    var child = Workflow.Create<Child>("Child").Input(data => data.Listed).Output(data => data.Listed).Build();
                    {{(localDelegate ? "Action<IWorkflowInvocationSetupBuilder<Data, Child, Noop>> Setup = setup => { " + setup + " };" : "")}}
                    return Workflow.Create<Data>("Parent").Step<Noop>()
                        .Invoke<Child>("Child", 1, WorkflowDefinitionValidator.GetContract(child), Setup).Build();
                }
                {{(localDelegate ? "" : "private static void Setup(IWorkflowInvocationSetupBuilder<Data, Child, Noop> setup) { " + setup + " }")}}
                private static void ReplaceRef(ref IWorkflowInvocationSetupBuilder<Data, Child, Noop> target,
                    IWorkflowInvocationSetupBuilder<Data, Child, Noop> value) => target = value;
                private static void ReplaceOut(out IWorkflowInvocationSetupBuilder<Data, Child, Noop> target,
                    IWorkflowInvocationSetupBuilder<Data, Child, Noop> value) => target = value;
                private static void Inspect(in IWorkflowInvocationSetupBuilder<Data, Child, Noop> builder) { }
                private static void Configure(IWorkflowInvocationSetupBuilder<Data, Child, Noop> builder) =>
                    builder.Input(data => data.Required).From(_ => 1);
            }
            """, Diagnostic(Descriptors.NotAnInput, 0, "Child workflow", "Child.ReadOnly", "public setter"),
            Diagnostic(Descriptors.NotAnOutput, 1, "Child workflow", "Child.WriteOnly", "public getter"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalContinuationChainsKeepMembershipAndRequiredInputDiagnostics(bool nameOnly)
    {
        await Analyzer<MappingAnalyzer>($$"""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput, Required] public int Mapped { get; set; }
                [WorkflowInput, Required] public int Missing { get; set; }
                public int Other { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .{|#0:Invoke<Child>|}("Child", 1, setup =>
                    {
                        var first = setup.Input(data => data.Mapped).From(_ => 1);
                        var continuation = first.Input(data => data.{|#1:Other|}).From(_ => 1);
                        var output = continuation.Output(data => data.{|#2:Other|});
                        output.To(ctx => ctx.WorkflowData.Value);
                        {{(nameOnly ? "_ = nameof(first); Func<string> label = () => nameof(continuation); string Label() => nameof(output);" : "")}}
                    }).Build();
            }
            """, Diagnostic(Descriptors.WorkflowRequiredInputNotMapped, 0, "Child.Missing"),
            Diagnostic(Descriptors.WorkflowNotAnInput, 1, "Child.Other"),
            Diagnostic(Descriptors.WorkflowNotAnOutput, 2, "Child.Other"));
    }

    [Fact]
    public async Task CallerViewsTraceTheirOwnBuilderThroughLocalContinuations()
    {
        await Analyzer<MappingAnalyzer>("""
            public sealed class Data { public int Value { get; set; } }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                public int Other { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .Invoke<Child>("Child", 1, setup =>
                    {
                        var continuation = setup.Input(data => data.Marked).From(_ => 1);
                        var alias = continuation.Input(data => data.Marked).From(_ => 1);
                        alias.Input(data => data.{|#0:Other|}).From(_ => 1)
                            .Output(data => data.{|#1:Other|}).To(ctx => ctx.WorkflowData.Value);
                    }).Build();
            }
            """, Diagnostic(Descriptors.WorkflowNotAnInput, 0, "Child.Other"),
            Diagnostic(Descriptors.WorkflowNotAnOutput, 1, "Child.Other"));
    }

    [Fact]
    public async Task CallerViewsInCatchBodiesCheckMembership()
    {
        await Analyzer<MappingAnalyzer>("""
            public sealed class Data { }
            public sealed class Child
            {
                [WorkflowInput] public int Marked { get; set; }
                public int Other { get; set; }
            }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>()
                    .Try(body => body.Step<Noop>())
                    .Catch<Exception>(body => body.Invoke<Child>("Child", 1, setup => setup
                        .Input(data => data.{|#0:Other|}).From(_ => 1))).Build();
            }
            """, Diagnostic(Descriptors.WorkflowNotAnInput, 0, "Child.Other"));
    }

    [Fact]
    public async Task WorkflowMarkersWinOverActivityMarkersForChildData()
    {
        await Analyzer<MappingAnalyzer>("""
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { }
            public sealed class Child : IAsyncActivity
            {
                [WorkflowInput, Required] public int WorkflowOnly { get; set; }
                [ActivityInput] public int ActivityOnly { get; set; }
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Child>()
                    .{|#0:Invoke<Child>|}("Child", 1, setup => setup.Input(data => data.{|#1:ActivityOnly|}).From(_ => 1)).Build();
            }
            """, Diagnostic(Descriptors.WorkflowRequiredInputNotMapped, 0, "Child.WorkflowOnly"),
            Diagnostic(Descriptors.WorkflowNotAnInput, 1, "Child.ActivityOnly"));
    }

    [Theory]
    [InlineData("[WorkflowInput]", "{ get; private set; }", "setter")]
    [InlineData("[WorkflowOutput]", "{ private get; set; }", "getter")]
    [InlineData("[WorkflowOutput, Required]", "{ get; set; }", "required")]
    [InlineData("[DefaultValue(1)]", "{ get; private set; }", "default-setter")]
    [InlineData("[WorkflowInput, DefaultValue(\"bad\")]", "{ get; set; }", "default")]
    public async Task UnmappedInvalidDeclarationsAreReported(string attributes, string accessors, string reason)
    {
        var descriptor = reason switch
        {
            "setter" => Descriptors.WorkflowInputNeedsSetter,
            "getter" => Descriptors.WorkflowOutputNeedsGetter,
            "required" => Descriptors.WorkflowRequiredOnOutput,
            "default-setter" => Descriptors.WorkflowDefaultNeedsSetter,
            _ => Descriptors.WorkflowInvalidDefault
        };
        await Analyzer<MappingAnalyzer>($$"""
            using System.ComponentModel;
            using System.ComponentModel.DataAnnotations;
            public sealed class Data { }
            public sealed class Child { {{attributes}} public int {|#0:Value|} {{accessors}} }
            public sealed class Noop : IAsyncActivity
            { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
            public static class Flow
            {
                public static object Build() => Workflow.Create<Data>("Parent").Step<Noop>().Invoke<Child>("Child", 1).Build();
            }
            """, reason == "default" ? Diagnostic(descriptor, 0, "Child.Value", "Int32") : Diagnostic(descriptor, 0, "Child.Value"));
    }
}
