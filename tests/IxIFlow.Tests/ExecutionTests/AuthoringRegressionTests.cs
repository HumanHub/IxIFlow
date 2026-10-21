using IxIFlow.Builders;
using IxIFlow.Core.Runtime;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.WorkflowRegistrationTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class AuthoringRegressionTests
{
    [Fact]
    [Trait("Spec", "F-05")]
    public void DuplicateFluentIdsAreRefusedAtBuildTime()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<RegistrationData>("Duplicate")
            .Step<RegistrationActivity>().WithId("same")
            .Sequence(body => body.Step<RegistrationActivity>().WithId("same")).WithId("container").Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("Duplicate step ID 'same'", error.Message);
        Assert.Contains(nameof(AuthoringRegressionTests) + ".cs:", error.Message);
    }

    [Fact]
    [Trait("Spec", "F-05")]
    public void FluentIdsCaptureCallerLocationsAndNamesAreExplicit()
    {
        var definition = Workflow.Create<RegistrationData>("Located")
            .Step<RegistrationActivity>().WithId("activity").Named("Explicit name").Build();
        var step = Assert.Single(definition.Steps);
        Assert.EndsWith(".cs", step.SourceLocation!.Split(':')[^2]);
        Assert.Contains(nameof(AuthoringRegressionTests), step.SourceLocation);
        Assert.Equal("Explicit name", step.Name);
    }

    [Fact]
    [Trait("Spec", "F-06")]
    public void ExistingFluentStructureHashIsUnchangedByIdsAndLocations()
    {
        var definition = Workflow.Create<SyntaxTests.WorkflowBuilderIdentityTests.WorkflowData>("OrderApproval", 2)
            .Step<SyntaxTests.WorkflowBuilderIdentityTests.StartActivity>()
            .Try(body => body.Step<SyntaxTests.WorkflowBuilderIdentityTests.StartActivity>())
            .Catch<InvalidOperationException>(body => body.Step<SyntaxTests.WorkflowBuilderIdentityTests.StartActivity>())
            .Build();
        const string expected = "3FEB6507E9158B0D42B6913927679E5B800E45DB0373A60F31212155B2A5CBDE";
        Assert.Equal(expected, new WorkflowScopeCatalog(definition).StructureHash);
        foreach (var step in ParallelRules.Flatten(definition.Steps))
        {
            step.Id = "located/" + step.Id;
            step.SourceLocation = "another.yaml:3";
        }
        Assert.Equal(expected, new WorkflowScopeCatalog(definition).StructureHash);
    }

    [Fact]
    public async Task DiRegisteredChildMustMatchTheRegistryStructureHash()
    {
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(Workflow.Create<RegistrationData>("Customer approval", 3)
            .Step<RegistrationActivity>().Build());
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.RegisterWorkflow<NamedWorkflow>();
        services.AddSingleton<IWorkflowCatalog>(registry);
        using var provider = services.BuildServiceProvider();
        var parent = Workflow.Create<ParentData>("Mismatch parent").Step<RegistrationActivity>()
            .Invoke<NamedWorkflow, RegistrationData>().Build();

        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData()));
        Assert.Contains("differs from its registered definition", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "F-08")]
    public async Task CompensationSavedValueMismatchesKeepTheInvalidStateMessage(bool previousCompensation)
    {
        var compensation = new CompensationActivityInfo { ActivityType = typeof(YamlMappingActivity) };
        var builder = new CompensationActivityBuilder<RegressionData, YamlMappingActivity, MarkChildActivity, RegistrationActivity>(compensation);
        if (previousCompensation)
            builder.Input(activity => activity.Value).From((RegistrationActivity _) => "value");
        else
            builder.Input(activity => activity.Value).From((MarkChildActivity previous) => previous.Value);
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var request = new TypedActivityExecutionRequest<RegressionData, object>
        {
            ActivityType = typeof(YamlMappingActivity),
            WorkflowData = new(),
            Step = new(),
            InputMappings = compensation.InputMappings,
            CompensationPreviousStepData = new RegressionApprovalEvent(),
            CompensationPreviousChainData = new RegressionApprovalEvent(),
            ExecutionContext = new()
            {
                WorkflowInstance = new() { InstanceId = "mismatch" },
                WorkflowDefinition = new() { Name = "Mismatch" },
                WorkflowData = new()
            }
        };
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IActivityExecutor>()
            .PrepareTypedActivityAsync(request, true, "invocation", "attempt"));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("requires " + (previousCompensation ? nameof(RegistrationActivity) : nameof(MarkChildActivity)) +
            ", but saved value is " + nameof(RegressionApprovalEvent), error.Message);
    }

    [Fact]
    [Trait("Spec", "F-05")]
    public void CatchAndSagaBuildersExposeStepIds()
    {
        var definition = Workflow.Create<RegistrationData>("Ids")
            .Step<RegistrationActivity>()
            .Try(body => body.Step<RegistrationActivity>())
            .Catch<Exception>(body => body.Step<RegistrationActivity>().WithId("catch"))
            .Saga(body => body.Step<RegistrationActivity>().WithId("saga"))
            .OnError<Exception>(body => body.Step<RegistrationActivity>().WithId("error"))
            .Build();
        Assert.Equal("catch", definition.Steps[1].CatchBlocks[0].SequenceSteps[0].Id);
        Assert.Equal("saga", definition.Steps[2].SequenceSteps[0].Id);
        Assert.Equal("error", definition.Steps[2].CatchBlocks[0].SequenceSteps[0].Id);
    }

    [Fact]
    public void RequestAndForEachRulesKeepTheirOffendingLocations()
    {
        var request = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<RegistrationData>("Request")
            .Request<QuestionActivity>("question").WithId("request", "request.yaml", 8).Build());
        Assert.StartsWith("request.yaml:8:", request.Message);
        var each = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<ItemData, object>([], "Each")
            .ForEach(ctx => ctx.WorkflowData.Values, (_, _) => { }, setup => setup.Into(ctx => ctx.WorkflowData.Results))
            .WithId("each", "each.yaml", 12).Build());
        Assert.StartsWith("each.yaml:12:", each.Message);
    }

    [Fact]
    public void SagaAndCatalogRulesKeepTheirOffendingLocations()
    {
        var sagaDefinition = new WorkflowDefinition
        {
            Name = "Hand-built saga",
            Version = 1,
            Steps = [new WorkflowStep
            {
                StepType = WorkflowStepType.Saga,
                SequenceSteps = [new WorkflowStep { StepType = WorkflowStepType.Activity, ActivityType = typeof(RegistrationActivity) }],
                CatchBlocks = [new WorkflowStep
                {
                    Name = "Handler", StepType = WorkflowStepType.CatchBlock, SourceLocation = "saga.yaml:21",
                    StepMetadata = { ["SagaErrorConfig"] = new SagaErrorConfiguration
                    {
                        CompensationStrategy = CompensationStrategy.CompensateUpTo,
                        CompensationTargetType = typeof(CompleteActivity),
                        ContinuationAction = SagaContinuationAction.Continue
                    } }
                }]
            }]
        };
        var saga = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(sagaDefinition));
        Assert.StartsWith("saga.yaml:21:", saga.Message);
        Assert.Contains("not a step of the saga", saga.Message);
        var definition = new WorkflowDefinition
        {
            Name = "Hand-built fixture", Version = 1,
            Steps = [new WorkflowStep
            {
                Id = "incomplete", StepType = WorkflowStepType.WorkflowInvocation,
                SourceLocation = "catalog.yaml:14"
            }]
        };
        var catalog = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.StartsWith("catalog.yaml:14:", catalog.Message);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("alias")]
    [InlineData("concurrency")]
    [InlineData("join")]
    [InlineData("result")]
    public async Task DocumentValidationChecksForEach(string invalid)
    {
        var each = new ForEachStepDocument
        {
            Id = "each", As = invalid == "alias" ? "workflow" : "item",
            Items = new PathExpressionDocument { Path = invalid == "items" ? "" : "workflow.Values" },
            Parallel = new()
            {
                MaxConcurrency = invalid == "concurrency" ? 0 : 2,
                Join = invalid == "join" ? "unknown" : "all"
            },
            Result = invalid == "result" ? null : new PathExpressionDocument { Path = "item" },
            Into = new() { Path = "workflow.Results" },
            Steps = [new ActivityStepDocument { Id = "body", Activity = "missing" }]
        };
        var result = await YamlTestContracts.ValidateAsync(new()
        {
            Workflow = new() { Name = "Each", DataType = "data" }, Definitions = [each]
        }, YamlTestContracts.Create(("data", typeof(ItemData))));
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Path!.StartsWith("definitions[0]."));
        Assert.Contains(result.Diagnostics, item => item.Code == "STEP003" && item.Path == "definitions[0].steps[0].activity");
        Assert.Contains(result.Diagnostics, item => invalid == "items" ? item.Code == "EXPR001" : item.Code.StartsWith("EACH"));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentValidationAcceptsForEachItemAliases(bool asset)
    {
        var document = new WorkflowDocument
        {
            Workflow = new() { Name = "Each", DataType = "data" },
            Code = new() { ["done"] = new() { Language = "csharp-expression", Source = "results.Count > 0" } },
            Definitions = [new ForEachStepDocument
            {
                Id = "each", As = "number", Items = new PathExpressionDocument { Path = "workflow.Values" },
                Result = new PathExpressionDocument { Path = "number" }, Into = new() { Path = "workflow.Results" },
                Parallel = new()
                {
                    Join = "conditionally", ResultType = "int",
                    Condition = asset ? new FunctionExpressionDocument { Function = "done" } : new BinaryExpressionDocument
                    {
                        Operator = "gt", Left = new PathExpressionDocument { Path = "results.Count" },
                        Right = new ConstantExpressionDocument { Value = 0 }
                    }
                }
            }]
        };
        var result = await YamlTestContracts.ValidateAsync(document,
            YamlTestContracts.Create(("data", typeof(ItemData))));
        Assert.True(result.IsValid, string.Join(", ", result.Diagnostics));
    }

    [Fact]
    public void HandBuiltStepsWithoutRuntimeAreInvalidAtTheirSource()
    {
        var definition = new WorkflowDefinition
        {
            Name = "Hand-built fixture", Version = 1,
            WorkflowDataType = typeof(RegistrationData),
            Steps = [new WorkflowStep
            {
                Id = "hand-built", StepType = WorkflowStepType.Activity,
                ActivityType = typeof(RegistrationActivity), WorkflowDataType = typeof(RegistrationData),
                SourceLocation = "hand-built.yaml:17"
            }]
        };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.Validate(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.StartsWith("hand-built.yaml:17:", error.Message);
        Assert.Contains("no runtime delegates", error.Message);
    }

    [Fact]
    public void CatalogsReuseTheDefinitionRuleResultsWithoutRevisitingPurity()
    {
        var visits = new CountingCondition();
        var step = new WorkflowStep
        {
            Id = "condition", StepType = WorkflowStepType.Conditional, WorkflowDataType = typeof(RegistrationData),
            Runtime = WorkflowStepDelegates.Create<RegistrationData>(),
            ConditionExpression = System.Linq.Expressions.Expression.Lambda<Func<bool>>(visits)
        };
        var definition = new WorkflowDefinition { Name = "Hand-built fixture", Version = 1, WorkflowDataType = typeof(RegistrationData), Steps = [step] };
        WorkflowDefinitionValidator.Validate(definition);
        var count = visits.Visits;
        Assert.Equal(1, count);
        System.Threading.Tasks.Parallel.For(0, 20, _ =>
        {
            WorkflowDefinitionValidator.Validate(definition);
            _ = new WorkflowScopeCatalog(definition).StructureHash.Length;
        });
        Assert.Equal(count, visits.Visits);
    }

    [Fact]
    public async Task StructFaultAllowsAnIfInTheCatchBody()
    {
        var definition = Workflow.Create<RegistrationData>("Struct fault")
            .Step<RegistrationActivity>()
            .Try(body => body.Step<ThrowInvalidActivity>())
            .Catch<InvalidOperationException, StructFault>(body => body
                .If(_ => true, then => then.Step<CompleteActivity>(setup => setup
                    .Output(activity => activity.Completed).To(ctx => ctx.WorkflowData.Completed))))
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new RegistrationData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.True(Assert.IsType<RegistrationData>(result.WorkflowData).Completed);
    }

    [Fact]
    [Trait("Spec", "F-06")]
    public void ItemCapturingForEachWithIntoAndCombineHasAPinnedStructureHash()
    {
        var definition = new WorkflowBuilder<FingerprintItems, object>([], "Captured items")
            .ForEach(ctx => ctx.WorkflowData.Values, (body, item) => body
                .Step<FingerprintActivity>(setup => setup
                    .Input(activity => activity.Value).From(ctx => ctx.Item(item))
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Total))
                .Result(ctx => ctx.PreviousStep.Value),
                setup => setup.Parallel(2).Into(ctx => ctx.WorkflowData.Results)
                    .Combine<int, Sum<int>>(data => data.Total))
            .Build();
        var structureHash = new WorkflowScopeCatalog(definition).StructureHash;
        Assert.True(structureHash == "A42E414DF7A56DD5322596591655C6FB54BF043788849BA695FACC232AA626EC", structureHash);
    }

    private sealed class CountingCondition : System.Linq.Expressions.Expression
    {
        public int Visits { get; private set; }
        public override System.Linq.Expressions.ExpressionType NodeType => System.Linq.Expressions.ExpressionType.Extension;
        public override Type Type => typeof(bool);
        public override bool CanReduce => true;
        public override System.Linq.Expressions.Expression Reduce() => System.Linq.Expressions.Expression.Constant(true);
        protected override System.Linq.Expressions.Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor)
        {
            if (visitor.GetType().DeclaringType == typeof(ExpressionRules)) Visits++;
            return Reduce();
        }
    }

    public struct StructFault { public string Message { get; set; } }
    public sealed class ThrowInvalidActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("caught");
    }
    public sealed class FingerprintItems
    {
        public List<int> Values { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
        public int Total { get; set; }
    }
    public sealed class FingerprintActivity : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task YamlCompilationRejectsActivityVersionBeforeEmission()
    {
        var document = new WorkflowDocument
        {
            Workflow = new() { Name = "Version", DataType = "data" },
            Definitions = [new ActivityStepDocument { Activity = "mapping", ActivityVersion = "2.0" }]
        };
        var options = YamlTestContracts.Create(("data", typeof(ItemData)), ("mapping", typeof(YamlMappingActivity)));
        options.Activities["mapping"] = new("mapping", "1.0");
        var error = await Assert.ThrowsAsync<IxIFlow.Generators.WorkflowSourceException>(() => IxIFlow.Authoring.WorkflowCompilation.BuildAsync(document, options));
        Assert.Contains("requires version '1.0'", error.Message);
    }

    public sealed class ItemData
    {
        public List<int> Values { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
    }

    public sealed class QuestionActivity : IAsyncActivity, IRequestActivity<RegistrationEvent>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
