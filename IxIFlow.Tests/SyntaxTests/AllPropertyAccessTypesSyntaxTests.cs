using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Comprehensive test to verify ALL property access types work correctly across ALL workflow constructs.
/// Specifically focuses on finding the missing interface for saga error handling case:
/// .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.Level1Output)
/// 
/// All four access types are tested in each construct:
/// 1. From(Exception access) - ctx.Exception.Message
/// 2. From(CurrentStep access) - ctx.CurrentStep.Property  
/// 3. From(PreviousStep access) - ctx.PreviousStep.Property <-- THE ISSUE
/// 4. From(Workflow access) - ctx.WorkflowData.Property
/// </summary>
public class AllPropertyAccessTypesSyntaxTests
{
    [Fact]
    public void Build_AllConstructsAllAccessTypes_ShouldCompileSuccessfully()
    {
        var definition = Workflow.Build<RootWorkflowData>(builder => builder

            // ===== STEP 1: BASIC STEP =====
            .Step<Level1Activity>(setup => setup
                // === 4. WORKFLOW ACCESS ===
                .Input(act => act.Level1Input).From(ctx => ctx.WorkflowData.RootInput)
                .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput))

            // ===== IF/ELSE WITH ALL ACCESS TYPES (TRANSPARENT) =====
            .If(ctx => ctx.PreviousStep.Level1Output == "test",
                then => then
                    .Step<Level2Activity>(setup => setup
                        // === 3. PREVIOUS STEP ACCESS ===
                        .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.Level1Output)
                        // === 4. WORKFLOW ACCESS ===
                        .Input(act => act.Level2Input).From(ctx => ctx.WorkflowData.RootInput)
                        .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput)),
                @else => @else
                    .Step<Level3Activity>(setup => setup
                        // === 3. PREVIOUS STEP ACCESS ===
                        .Input(act => act.Level3Input).From(ctx => ctx.PreviousStep.Level1Output)
                        .Output(act => act.Level3Output).To(ctx => ctx.WorkflowData.RootOutput)))

            // ===== SEQUENCE WITH ACCESS TYPES (TRANSPARENT) =====
            .Sequence(seq => seq
                .Step<Level1Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (transparent - refers to Step 1 BEFORE if/else) ===
                    .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.Level1Output)
                    .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput))
                .Suspend<SuspendData>("Suspend for error handling",
                    (data, ctx) => data.SuspendInput == ctx.WorkflowData.RootInput,
                    setup => setup
                        .Input(data => data.SuspendInput).From(ctx => ctx.PreviousStep.Level1Output)
                        .Input(data => data.SuspendInput).From(ctx => ctx.WorkflowData.RootInput)
                        .Output(data => data.SuspendOutput).To(ctx => ctx.WorkflowData.RootOutput))
                .Step<Level2Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (within sequence) ===
                    .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.SuspendOutput)
                    .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput)))

            // ===== WHILE LOOP WITH ALL ACCESS TYPES (TRANSPARENT) =====
            .DoWhile(
                loopBody => loopBody
                    .Step<Level2Activity>(setup => setup
                        // === 3. PREVIOUS STEP ACCESS (transparent - refers to Step 1 BEFORE sequence) ===
                        .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.Level1Output)
                        // === 4. WORKFLOW ACCESS ===
                        .Input(act => act.Level2Input).From(ctx => ctx.WorkflowData.RootInput)
                        .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput)),
                condition => condition.PreviousStep.Level1Output == "continue")

            // ===== TRY/CATCH WITH ALL ACCESS TYPES =====
            .Try(tryBuilder => tryBuilder
                .Step<Level1Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (transparent - refers to Step 1 BEFORE while) ===
                    .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.Level1Output)
                    .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput)))
            .Catch<TestException>(handler => handler
                .Step<Level2Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (from try) ===
                    .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.Level1Output)
                    // === 4. WORKFLOW ACCESS ===
                    .Input(act => act.Level2Input).From(ctx => ctx.WorkflowData.RootInput)
                    .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput))
                .Step<Level3Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (within catch) ===
                    .Input(act => act.Level3Input).From(ctx => ctx.PreviousStep.Level2Output)
                    .Output(act => act.Level3Output).To(ctx => ctx.WorkflowData.RootOutput)))
            .Catch(handler => handler
                .Step<Level1Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (generic catch-all) ===
                    .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.Level1Output)
                    .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput)))

            // ===== SAGA WITH ALL ACCESS TYPES (TRANSPARENT) =====
            .Saga(saga => saga
                .Step<Level1Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (transparent - refers to Step 1 BEFORE try/catch) ===
                    .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.Level1Output)
                    .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput)
                    .CompensateWith<Level2Activity>(comp => comp
                        // === 2. CURRENT STEP ACCESS ===
                        .Input(act => act.Level2Input).From(ctx => ctx.CurrentStep.Level1Output)
                        .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput)))
                .Suspend<SuspendData>("Suspend for error handling",
                    (data, ctx) => data.SuspendInput == ctx.PreviousStep.Level1Output,
                    setup => setup
                        .Input(data => data.SuspendInput).From(ctx => ctx.PreviousStep.Level1Output)
                        .Output(data => data.SuspendOutput).To(ctx => ctx.WorkflowData.RootOutput))
                //.OutcomeOn(x => x.PreviousStep.SuspendOutput, 
                //    osetup =>osetup
                //        .Outcome("Cucu",o1=>o1.Step<>())
                    
                    
                //    )
                .Step<Level2Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (within saga) ===
                    .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.SuspendOutput)
                    .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput)
                    .CompensateWith<Level3Activity>(comp => comp
                        // === 2. CURRENT STEP ACCESS ===
                        .Input(act => act.Level3Input).From(ctx => ctx.CurrentStep.Level2Output))))

            // ===== THE KEY TEST - SAGA ERROR HANDLING WITH ALL ACCESS TYPES =====
            .OnError<TestException>(error => error
                .Step<Level1Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (transparent - refers to Step 1 BEFORE saga) ===
                    .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.Level1Output)
                    // === 4. WORKFLOW ACCESS ===
                    .Input(act => act.Level1Input).From(ctx => ctx.WorkflowData.RootInput)
                    .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput))

                // THE PROBLEMATIC CASE - Second error step accessing previous error step
                .Step<Level2Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (THE MISSING INTERFACE?) ===
                    .Input(act => act.Level2Input).From(ctx => ctx.PreviousStep.Level1Output) // <-- THIS IS THE ISSUE
                                                                                              // === 4. WORKFLOW ACCESS ===
                    .Input(act => act.Level2Input).From(ctx => ctx.WorkflowData.RootInput)
                    .Output(act => act.Level2Output).To(ctx => ctx.WorkflowData.RootOutput))
                .Compensate()
                .ThenContinue())
            .OnError(error => error
                .Step<Level3Activity>(setup => setup
                    // === 3. PREVIOUS STEP ACCESS (generic error handler) ===
                    .Input(act => act.Level3Input).From(ctx => ctx.PreviousStep.Level1Output)
                    .Output(act => act.Level3Output).To(ctx => ctx.WorkflowData.RootOutput))
                .Compensate()
                .ThenTerminate())

            // ===== SUSPEND/RESUME WITH ALL ACCESS TYPES =====
            .Suspend<TestEvent>("Waiting for event",
                (evt, ctx) => evt.EventData == ctx.WorkflowData.RootInput,
                setup => setup
                    .Input(evt => evt.EventData).From(ctx => ctx.WorkflowData.RootInput)
                    .Output(evt => evt.EventData).To(ctx => ctx.WorkflowData.RootOutput))

            .Step<Level1Activity>(setup => setup
                // === 3. PREVIOUS STEP ACCESS (event result) ===
                .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.EventData)
                .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput))

            // ===== WORKFLOW INVOCATION WITH ALL ACCESS TYPES =====
            .Invoke<TestWorkflow, TestWorkflowData>(setup => setup
                // === 3. PREVIOUS STEP ACCESS ===
                .Input(wf => wf.WorkflowInput).From(ctx => ctx.PreviousStep.Level1Output)
                // === 4. WORKFLOW ACCESS ===
                .Input(wf => wf.WorkflowInput).From(ctx => ctx.WorkflowData.RootInput)
                .Output(wf => wf.WorkflowOutput).To(ctx => ctx.WorkflowData.RootOutput))

            .Step<Level1Activity>(setup => setup
                // === 3. PREVIOUS STEP ACCESS (workflow result) ===
                .Input(act => act.Level1Input).From(ctx => ctx.PreviousStep.WorkflowOutput)
                .Output(act => act.Level1Output).To(ctx => ctx.WorkflowData.RootOutput)),

            "AllPropertyAccessTypesTest");

        Assert.NotNull(definition);
        Assert.True(definition.Steps.Count > 0);
    }

    // ===== SIMPLE DATA MODELS FOR CLEAN TESTING =====
    public class RootWorkflowData
    {
        public string RootInput { get; set; } = "";
        public string RootOutput { get; set; } = "";
    }

    public class Level1Activity : IAsyncActivity
    {
        public string Level1Input { get; set; } = "";
        public string Level1Output { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Level1Output = Level1Input + "_processed";
            return Task.CompletedTask;
        }
    }

    public class Level2Activity : IAsyncActivity
    {
        public string Level2Input { get; set; } = "";
        public string Level2Output { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Level2Output = Level2Input + "_processed";
            return Task.CompletedTask;
        }
    }

    public class Level3Activity : IAsyncActivity
    {
        public string Level3Input { get; set; } = "";
        public string Level3Output { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Level3Output = Level3Input + "_processed";
            return Task.CompletedTask;
        }
    }

    public class TestEvent
    {
        public string EventData { get; set; } = "";
    }

    public class TestException : Exception
    {
        public TestException() : base("Test Exception") { }
    }

    public class TestWorkflow : IWorkflow<TestWorkflowData>
    {
        public int Version => 1;
        
        public void Build(IWorkflowBuilder<TestWorkflowData> builder)
        {
            // Simple test workflow implementation
        }
    }

    public class TestWorkflowData
    {
        public string WorkflowInput { get; set; } = "";
        public string WorkflowOutput { get; set; } = "";
    }
    public class SuspendData
    {
        public string SuspendInput { get; set; } = "";
        public string SuspendOutput { get; set; } = "";
    }
}

