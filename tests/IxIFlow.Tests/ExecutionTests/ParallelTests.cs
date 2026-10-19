using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests.Models
{
    // Classes needed for parallel tests
    public class ParallelTestData
    {
        public int InputA { get; set; }
        public int InputB { get; set; }
        public int OutputA { get; set; }
        public int OutputB { get; set; }
        public int FinalResult { get; set; }
    }

    public class ActivityA : IAsyncActivity
    {
        public int Input { get; set; }
        public int Output { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Output = Input * 2; // Example transformation
            return Task.CompletedTask;
        }
    }

    public class ActivityB : IAsyncActivity
    {
        public int Input { get; set; }
        public int Output { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Output = Input * 3; // Example transformation
            return Task.CompletedTask;
        }
    }

    public class AggregateActivity : IAsyncActivity
    {
        public int InputA { get; set; }
        public int InputB { get; set; }
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = InputA + InputB;
            return Task.CompletedTask;
        }
    }
}

namespace IxIFlow.Tests.ExecutionTests
{
    [Trait("Feature", "control-flow")]
    public class ParallelTests
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IWorkflowClient _workflowEngine;

        public ParallelTests()
        {
            // Setup DI container
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
            services.AddSingleton<BranchGate>();
            services.AddTransient<GatedBranchActivity>();

            _serviceProvider = services.BuildServiceProvider();
            _workflowEngine = _serviceProvider.GetRequiredService<IWorkflowClient>();
        }

        [Fact]
        public async Task TestBasicParallelExecution()
        {
            // Arrange: Define a workflow with parallel branches
            var definition = Workflow
                .Create<ParallelTestData>("BasicParallelWorkflow")
                .Step<StepExecutionAsyncActivity>(setup => setup
                    .Input(act => act.StepName).From(ctx => "InitialStep")
                    .Input(act => act.StepNumber).From(ctx => 0))
                .Parallel(parallelBuilder =>
                {
                    parallelBuilder
                        .Do(branch => branch
                            .Step<ActivityA>(setup => setup
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputA)
                                .Output(act => act.Output).To(ctx => ctx.WorkflowData.OutputA)));

                    parallelBuilder
                        .Do(branch => branch
                            .Step<ActivityB>(setup => setup
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputB)
                                .Output(act => act.Output).To(ctx => ctx.WorkflowData.OutputB)));
                })
                .Step<AggregateActivity>(setup => setup
                    .Input(act => act.InputA).From(ctx => ctx.WorkflowData.OutputA)
                    .Input(act => act.InputB).From(ctx => ctx.WorkflowData.OutputB)
                    .Output(act => act.Result).To(ctx => ctx.WorkflowData.FinalResult))
                .Build();

            var workflowData = new ParallelTestData
            {
                InputA = 10,
                InputB = 20
            };

            // Act
            var result = await _workflowEngine.RunAsync(definition, workflowData);

            // Assert
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.NotNull(result.WorkflowData);
            var finalData = (ParallelTestData)result.WorkflowData;
            Assert.Equal(80, finalData.FinalResult); // 10*2 + 20*3 = 20+60=80
        }

        [Fact]
        public async Task TestParallelWithBranchFailure()
        {
            // Test when one branch fails
            var definition = Workflow
                .Create<ParallelTestData>("BranchFailureWorkflow")
                .Step<StepExecutionAsyncActivity>(setup => setup
                    .Input(act => act.StepName).From(ctx => "InitialStep")
                    .Input(act => act.StepNumber).From(ctx => 0))
                .Parallel(parallelBuilder =>
                {
                    parallelBuilder
                        .Do(branch => branch
                            .Step<ActivityA>(setup => setup
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputA)
                                .Output(act => act.Output).To(ctx => ctx.WorkflowData.OutputA)));
                    parallelBuilder
                        .Do(branch => branch
                            .Step<FailingActivity>(setup => setup
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputB)));
                })
                .Build(); // Added Build() call

            var workflowData = new ParallelTestData
            {
                InputA = 10,
                InputB = 20
            };

            // Act
            var result = await _workflowEngine.RunAsync(definition, workflowData);

            // Assert
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            // Check both possible error messages
            Assert.True(
                result.ErrorMessage.Contains("Simulated failure") ||
                result.ErrorMessage.Contains("Parallel execution failed"),
                $"Actual error: {result.ErrorMessage}"
            );
        }

        [Fact]
        public async Task TestParallelWithDifferentDurations()
        {
            // Each branch's activity holds until the test releases it, which it does only once both have
            // started: the branches finish only if they run at the same time.
            var definition = Workflow
                .Create<ParallelTestData>("DifferentDurationsWorkflow")
                .Step<StepExecutionAsyncActivity>(setup => setup
                    .Input(act => act.StepName).From(ctx => "InitialStep")
                    .Input(act => act.StepNumber).From(ctx => 0))
                .Parallel(parallelBuilder =>
                {
                    parallelBuilder
                        .Do(branch => branch
                            .Step<GatedBranchActivity>(setup => setup
                                .Input(act => act.Branch).From(ctx => "A")
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputA)
                                .Input(act => act.Multiplier).From(ctx => 2)
                                .Output(act => act.Output).To(ctx => ctx.WorkflowData.OutputA)));
                    parallelBuilder
                        .Do(branch => branch
                            .Step<GatedBranchActivity>(setup => setup
                                .Input(act => act.Branch).From(ctx => "B")
                                .Input(act => act.Input).From(ctx => ctx.WorkflowData.InputB)
                                .Input(act => act.Multiplier).From(ctx => 3)
                                .Output(act => act.Output).To(ctx => ctx.WorkflowData.OutputB)));
                })
                .Step<AggregateActivity>(setup => setup
                    .Input(act => act.InputA).From(ctx => ctx.WorkflowData.OutputA)
                    .Input(act => act.InputB).From(ctx => ctx.WorkflowData.OutputB)
                    .Output(act => act.Result).To(ctx => ctx.WorkflowData.FinalResult))
                .Build(); // Added Build() call

            var workflowData = new ParallelTestData
            {
                InputA = 10,
                InputB = 20
            };
            var gate = _serviceProvider.GetRequiredService<BranchGate>();

            // Act
            var run = _workflowEngine.RunAsync(definition, workflowData);
            try
            {
                // Both branches are running before either is released.
                await Task.WhenAll(gate.Started("A"), gate.Started("B")).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                gate.Release();
            }
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var finalData = (ParallelTestData)result.WorkflowData;
            Assert.Equal(80, finalData.FinalResult); // 10*2 + 20*3 = 20+60=80
        }

        // Each branch works on its own copy of the data, so two branches incrementing one counter
        // without a combiner would lose an update; the build refuses it (D-14, spec E-04).
        [Fact]
        [Trait("Spec", "E-04")]
        public void BranchesWritingOnePropertyWithoutACombinerAreABuildError()
        {
            var error = Assert.Throws<WorkflowEngineException>(() => Workflow
                .Create<SharedDataTestData>("CommonDataAccessWorkflow")
                .Step<InitializeCounterActivity>(setup => setup
                    .Output(act => act.Counter).To(ctx => ctx.WorkflowData.Counter))
                .Parallel(parallelBuilder =>
                {
                    parallelBuilder
                        .Do(branch => branch
                            .Step<IncrementCounterActivity>(setup => setup
                                .Input(act => act.Counter).From(ctx => ctx.WorkflowData.Counter)
                                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Counter)));
                    parallelBuilder
                        .Do(branch => branch
                            .Step<IncrementCounterActivity>(setup => setup
                                .Input(act => act.Counter).From(ctx => ctx.WorkflowData.Counter)
                                .Output(act => act.Result).To(ctx => ctx.WorkflowData.Counter)));
                })
                .Build());

            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.Contains("no combiner", error.Message);
        }

        // With Sum, the join adds every branch's change to the value at the fork, also when a branch
        // finishes in a later run, rebuilt from its saved changes (spec E-01, E-06).
        [Fact]
        [Trait("Spec", "E-06")]
        [Trait("Spec", "E-01")]
        public async Task SumAddsEveryBranchsChangeToTheValueAtTheForkAcrossARestart()
        {
            var definition = Workflow
                .Create<SummedCounterData>("SummedCounter")
                .Step<InitializeCounterActivity>()
                .Parallel(parallel => parallel
                    .Do(branch => branch
                        .Step<IncrementCounterActivity>(setup => setup
                            .Input(act => act.Counter).From(ctx => ctx.WorkflowData.Counter)
                            .Output(act => act.Result).To(ctx => ctx.WorkflowData.Counter)))
                    .Do(branch => branch
                        .Step<IncrementCounterActivity>(setup => setup
                            .Input(act => act.Counter).From(ctx => ctx.WorkflowData.Counter)
                            .Output(act => act.Result).To(ctx => ctx.WorkflowData.Counter))
                        .WaitFor<GoOn>("go")))
                .Build();

            var started = await _workflowEngine.RunAsync(definition, new SummedCounterData { Counter = 5 });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(5, ((SummedCounterData)started.WorkflowData!).Counter);
            var finished = await _workflowEngine.ResumeAsync(started.InstanceId, "go", new GoOn());

            Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
            Assert.Equal(7, ((SummedCounterData)finished.WorkflowData!).Counter);
        }

        // A conflict the build cannot see, here activity code changing its branch's copy directly, fails
        // the parallel step, where a Catch can handle it, and leaves the data as it was at the fork (spec E-04).
        [Fact]
        [Trait("Spec", "E-04")]
        [Trait("Spec", "E-07")]
        public async Task ConflictingBranchesFailTheParallelStepWithoutChangingTheData()
        {
            var definition = Workflow
                .Create<SharedDataTestData>("ConflictingBranches")
                .Step<InitializeCounterActivity>()
                .Try(attempt => attempt.Parallel(parallel => parallel
                    .Do(branch => branch.Step<BumpCounterInPlaceActivity>())
                    .Do(branch => branch.Step<BumpCounterInPlaceActivity>())))
                .Catch<JoinConflictException>(handler => handler
                    .Step<IncrementCounterActivity>(setup => setup
                        .Input(act => act.Counter).From(_ => 41)
                        .Output(act => act.Result).To(ctx => ctx.WorkflowData.Caught)))
                .Build();

            var result = await _workflowEngine.RunAsync(definition, new SharedDataTestData { Counter = 3 });

            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var data = (SharedDataTestData)result.WorkflowData!;
            Assert.Equal((3, 42), (data.Counter, data.Caught));
        }
    }

    // Additional classes needed for tests
    public class FailingActivity : IAsyncActivity
    {
        public int Input { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            throw new Exception("Simulated failure in parallel branch");
        }
    }

    /// <summary>Holds each branch's activity, once it has started, until the test releases them all.</summary>
    public sealed class BranchGate
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the activity of <paramref name="branch"/> has started.</summary>
        public Task Started(string branch) => Signal(branch).Task;

        public void Release() => _released.TrySetResult();

        public async Task EnterAsync(string branch, CancellationToken cancellationToken)
        {
            Signal(branch).TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }

        private TaskCompletionSource Signal(string branch) =>
            _started.GetOrAdd(branch, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public class GatedBranchActivity(BranchGate gate) : IAsyncActivity
    {
        public string Branch { get; set; } = "";
        public int Input { get; set; }
        public int Multiplier { get; set; } = 1;
        public int Output { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await gate.EnterAsync(Branch, cancellationToken);
            Output = Input * Multiplier;
        }
    }

    public class SharedDataTestData
    {
        public int Counter { get; set; }
        public int Caught { get; set; }
    }

    /// <summary>Changes its branch's copy of the data in place, which no output mapping shows.</summary>
    public class BumpCounterInPlaceActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((SharedDataTestData)context.WorkflowData).Counter++;
            return Task.CompletedTask;
        }
    }

    public class SummedCounterData
    {
        [Combine<Sum<int>>]
        public int Counter { get; set; }
    }

    public class GoOn;

    public class InitializeCounterActivity : IAsyncActivity
    {
        public int Counter { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Counter = 0;
            return Task.CompletedTask;
        }
    }

    public class IncrementCounterActivity : IAsyncActivity
    {
        public int Counter { get; set; }
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Counter + 1;
            return Task.CompletedTask;
        }
    }
}
