using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.ExecutionTests.Models;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
///     Tests for suspend/resume execution functionality
/// </summary>
[Trait("Feature", "waits")]
public class SuspendResumeExecutionTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IWorkflowClient _workflowEngine;
    private readonly IWorkflowStateRepository _stateRepository;

    public SuspendResumeExecutionTests()
    {
        var services = new ServiceCollection();

        // Register mock implementations for persistence dependencies
        services.AddSingleton<IWorkflowStateRepository, MockWorkflowStateRepository>();

        // Register IxIFlow
        services.AddIxIFlow();

        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<IWorkflowClient>();
        _stateRepository = _serviceProvider.GetRequiredService<IWorkflowStateRepository>();
    }

    [Fact]
    public async Task BasicSuspendResume_SuspendOnEvent_ResumeSuccessfully()
    {
        // Arrange - Create workflow that suspends waiting for approval
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-001",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("BasicSuspendResumeTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for manager approval",
                (resumeEvent, ctx) => resumeEvent.OrderId == ctx.WorkflowData.OrderId &&
                                      (resumeEvent.Approved || resumeEvent.Declined))
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.ProcessedData).From(ctx => ctx.WorkflowData.ProcessedOrder)
                .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);
        var startedData = Assert.IsType<SuspendResumeTestData>(result.WorkflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        // Check workflow instance status
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(startedData.ProcessOrderCompleted);
        Assert.False(startedData.CompleteOrderCompleted);
        Assert.NotEmpty(startedData.ProcessedOrder);

        // Act - Resume with approval event
        var approvalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-001",
            Approved = true,
            Declined = false
        };

        var resumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, approvalEvent);

        // Debug: Check the resume result
        Console.WriteLine($"RESUME RESULT: Status={resumeResult.Status}");
        Console.WriteLine($"WORKFLOW DATA TYPE: {resumeResult.WorkflowData?.GetType().Name}");
        if (resumeResult.WorkflowData is SuspendResumeTestData resumedData)
        {
            Console.WriteLine($"RESUMED DATA: CompleteOrderCompleted={resumedData.CompleteOrderCompleted}");
        }
        Console.WriteLine($"ORIGINAL DATA: CompleteOrderCompleted={workflowData.CompleteOrderCompleted}");

        // Assert - Workflow should complete successfully
        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);
        
        // Check workflow instance status after resume
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the workflow data from the resume result, not the original
        var finalWorkflowData = (SuspendResumeTestData)resumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
        Assert.True(finalWorkflowData.CompletedTimestamp > DateTime.MinValue);
    }

    [Fact]
    public async Task SuspendWithCondition_EventDoesNotMeetCondition_RemainsSuspended()
    {
        // Arrange - Create workflow with specific resume condition
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-002",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendWithConditionTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for approval without double approval requirement",
                (resumeEvent, ctx) => resumeEvent.OrderId == ctx.WorkflowData.OrderId &&
                                      (resumeEvent.Approved || resumeEvent.Declined) &&
                                      !resumeEvent.RequiresDoubleApproval) // Specific condition
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);

        // Act - Try to resume with event that doesn't meet condition (requires double approval)
        var invalidApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-002",
            Approved = true,
            Declined = false,
            RequiresDoubleApproval = true // This should prevent resumption
        };

        var invalidResumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, invalidApprovalEvent);

        // Assert - Workflow should still be suspended (condition not met)
        Assert.Equal(WorkflowExecutionStatus.Suspended, invalidResumeResult.Status);
        EventAssert.Rejected(invalidResumeResult, EventRejection.NotMatched);
        
        var stillSuspendedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(stillSuspendedInstance);
        Assert.Equal(WorkflowStatus.Suspended, stillSuspendedInstance.Status);
        
        Assert.False(workflowData.CompleteOrderCompleted);

        // Act - Resume with valid event that meets condition
        var validApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-002",
            Approved = true,
            Declined = false,
            RequiresDoubleApproval = false // This should allow resumption
        };

        var validResumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, validApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.Equal(WorkflowExecutionStatus.Success, validResumeResult.Status);
        
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SuspendResumeTestData)validResumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    [Fact]
    public async Task MultipleSuspends_SequentialSuspendResume_BothSuspendsWork()
    {
        // Arrange - Create workflow with multiple suspend points
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-003",
            ProcessOrderCompleted = false,
            ValidateOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("MultipleSuspendsTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for first approval",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                setup => setup
                    .Output(evt => evt.RequiresDoubleApproval).To(ctx => ctx.WorkflowData.RequiresSecondApproval))
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ValidateOrderCompleted).To(ctx => ctx.WorkflowData.ValidateOrderCompleted))
            .If(ctx => ctx.WorkflowData.RequiresSecondApproval,
                then =>
                {
                    then.Suspend<SecondApprovalEvent>("Waiting for second approval",
                        (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId);
                })
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend at first suspend point)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);
        var startedData = Assert.IsType<SuspendResumeTestData>(result.WorkflowData);

        // Assert - Workflow should be suspended at first suspend point
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(startedData.ProcessOrderCompleted);
        Assert.False(startedData.ValidateOrderCompleted);

        // Act - Resume with first approval event
        var firstApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-003",
            Approved = true,
            RequiresDoubleApproval = true
        };

        var firstResumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, firstApprovalEvent);

        // Assert - Workflow should be suspended at second suspend point
        Assert.Equal(WorkflowExecutionStatus.Suspended, firstResumeResult.Status);
        
        var secondSuspendInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(secondSuspendInstance);
        Assert.Equal(WorkflowStatus.Suspended, secondSuspendInstance.Status);
        
        // Use the resumed workflow data from the first resume result
        var firstResumeData = (SuspendResumeTestData)firstResumeResult.WorkflowData!;
        Assert.True(firstResumeData.ValidateOrderCompleted);
        Assert.True(firstResumeData.RequiresSecondApproval);
        Assert.False(firstResumeData.CompleteOrderCompleted);

        // Act - Resume with second approval event
        var secondApprovalEvent = new SecondApprovalEvent
        {
            OrderId = "ORDER-003",
            Approved = true,
            Declined = false
        };

        var secondResumeResult = await _workflowEngine.ResumeAsync(firstResumeResult.InstanceId, secondApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.Equal(WorkflowExecutionStatus.Success, secondResumeResult.Status);
        
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SuspendResumeTestData)secondResumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    [Fact]
    public async Task SuspendWithEventMapping_ResumeEventDataMapped_DataAvailableInSubsequentSteps()
    {
        // Arrange - Create workflow that maps resume event data
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-004",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendWithEventMappingTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for approval with data mapping",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                setup => setup
                    .Output(evt => evt.Approved).To(ctx => ctx.WorkflowData.ApprovalStatus)
                    .Output(evt => evt.RequiresDoubleApproval).To(ctx => ctx.WorkflowData.RequiresSecondApproval)
                    .Output(evt => evt.ApproverName).To(ctx => ctx.WorkflowData.ApproverName))
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.ApprovalStatus).From(ctx => ctx.WorkflowData.ApprovalStatus)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);

        // Act - Resume with approval event containing data
        var approvalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-004",
            Approved = true,
            Declined = false,
            RequiresDoubleApproval = false,
            ApproverName = "John Manager"
        };

        var resumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, approvalEvent);

        // Assert - Workflow should complete and event data should be mapped
        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);
        
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SuspendResumeTestData)resumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.ApprovalStatus);
        Assert.False(finalWorkflowData.RequiresSecondApproval);
        Assert.Equal("John Manager", finalWorkflowData.ApproverName);
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    [Fact]
    public async Task SuspendThenResume_NextStepCanReadResumeEventAsPreviousStep()
    {
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-004B",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendResumePreviousStepContextTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for approval with previous-step propagation",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId)
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.PreviousStep.OrderId)
                .Input(act => act.ApprovalStatus).From(ctx => ctx.PreviousStep.Approved)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted)
                .Output(act => act.ApprovalStatus).To(ctx => ctx.WorkflowData.ObservedApprovalStatus))
            .Build();

        var startResult = await _workflowEngine.RunAsync(workflow, workflowData);

        Assert.Equal(WorkflowExecutionStatus.Suspended, startResult.Status);

        var resumeResult = await _workflowEngine.ResumeAsync(startResult.InstanceId, new ApprovalEvent
        {
            OrderId = "ORDER-004B",
            Approved = true,
            Declined = false
        });

        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);

        var finalWorkflowData = Assert.IsType<SuspendResumeTestData>(resumeResult.WorkflowData);
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
        Assert.True(finalWorkflowData.ObservedApprovalStatus);
    }

    [Fact]
    public async Task NonMatchingResumeConditionRejectsTheEventBeforeItResumes()
    {
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-007B"
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("CorrelationConditionValidationTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for manager approval",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId &&
                              evt.Approved &&
                              !string.IsNullOrWhiteSpace(evt.ApproverName) &&
                              evt.ApproverName.Contains("Manager"))
            .Build();

        var startResult = await _workflowEngine.RunAsync(workflow, workflowData);
        Assert.Equal(WorkflowExecutionStatus.Suspended, startResult.Status);

        var rejected = await _workflowEngine.ResumeAsync(startResult.InstanceId, new ApprovalEvent
        {
            OrderId = "ORDER-007B",
            Approved = true,
            Declined = false,
            ApproverName = "John Employee"
        });

        EventAssert.Rejected(rejected, EventRejection.NotMatched);
        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);
        var instance = await _stateRepository.GetWorkflowInstanceAsync(startResult.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, instance!.Status);
    }

    [Fact]
    public async Task SuspendWithinTryBlock_Should_Capture_ExecutionSnapshot_WithTryFrame()
    {
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-TRY-001"
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendWithinTrySnapshotTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Try(tryBlock => tryBlock
                .Suspend<ApprovalEvent>("Waiting inside try block",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId)
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted)))
            .Catch<Exception, MessageFault>(catchBlock => catchBlock
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Fault.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.RequiresApproval)))
            .Build();

        var startResult = await _workflowEngine.RunAsync(workflow, workflowData);

        Assert.Equal(WorkflowExecutionStatus.Suspended, startResult.Status);

        Assert.NotNull(await _stateRepository.GetWorkflowInstanceAsync(startResult.InstanceId));
        var snapshot = await _workflowEngine.GetSnapshotAsync(startResult.InstanceId);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Pointers);
        Assert.Contains(snapshot.Frames, frame => frame.Kind == WorkflowStepType.TryCatch.ToString());
    }

    [Fact]
    public async Task SuspendWithinTryBlock_Should_Resume_And_Complete_AfterSnapshotRestore()
    {
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-TRY-002"
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendWithinTryResumeTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Try(tryBlock => tryBlock
                .Suspend<ApprovalEvent>("Waiting inside try block",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId)
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted)))
            .Catch<Exception, MessageFault>(catchBlock => catchBlock
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Fault.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.RequiresApproval)))
            .Build();

        var startResult = await _workflowEngine.RunAsync(workflow, workflowData);
        Assert.Equal(WorkflowExecutionStatus.Suspended, startResult.Status);

        var resumeResult = await _workflowEngine.ResumeAsync(startResult.InstanceId, new ApprovalEvent
        {
            OrderId = "ORDER-TRY-002",
            Approved = true,
            Declined = false
        });

        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);

        var finalWorkflowData = Assert.IsType<SuspendResumeTestData>(resumeResult.WorkflowData);
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    [Fact]
    public async Task SuspendInConditionalBranch_ConditionalSuspend_ResumeCorrectly()
    {
        // Arrange - Create workflow with suspend inside conditional branch
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-005",
            ProcessOrderCompleted = false,
            RequiresApproval = true,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendInConditionalTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .If(ctx => ctx.WorkflowData.RequiresApproval,
                then =>
                {
                    then.Suspend<ApprovalEvent>("Waiting for conditional approval",
                        (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId);
                })
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend in conditional branch)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);
        var startedData = Assert.IsType<SuspendResumeTestData>(result.WorkflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(startedData.ProcessOrderCompleted);
        Assert.False(startedData.CompleteOrderCompleted);

        // Act - Resume with approval event
        var approvalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-005",
            Approved = true,
            Declined = false
        };

        var resumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, approvalEvent);

        // Assert - Workflow should complete successfully
        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);
        
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SuspendResumeTestData)resumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    [Fact]
    public async Task SuspendWithTimeout_TimeoutNotExpired_WorkflowStillSuspended()
    {
        // Arrange - Create workflow with suspend timeout
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-006",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendWithTimeoutTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for approval with timeout",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId)
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);
        var startedData = Assert.IsType<SuspendResumeTestData>(result.WorkflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(startedData.ProcessOrderCompleted);
        Assert.False(startedData.CompleteOrderCompleted);

        // The open wait records what it waits for
        var wait = Assert.Single(await _stateRepository.GetOpenWaitsAsync(new WaitQuery(InstanceId: result.InstanceId)));
        Assert.Equal("Waiting for approval with timeout", wait.Key);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(ApprovalEvent)), wait.EventType);
    }

    [Fact]
    public async Task SuspendWithComplexCondition_ComplexResumeLogic_ResumeOnlyWhenConditionMet()
    {
        // Arrange - Create workflow with complex suspend condition
        var workflowData = new SuspendResumeTestData
        {
            OrderId = "ORDER-007",
            ProcessOrderCompleted = false,
            CompleteOrderCompleted = false
        };

        var workflow = Workflow
            .Create<SuspendResumeTestData>("SuspendComplexConditionTest")
            .Step<ProcessOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.ProcessOrderCompleted).To(ctx => ctx.WorkflowData.ProcessOrderCompleted))
            .Suspend<ApprovalEvent>("Waiting for complex approval condition",
                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId &&
                              evt.Approved &&
                              !evt.Declined &&
                              !string.IsNullOrEmpty(evt.ApproverName) &&
                              evt.ApproverName.Contains("Manager"))
            .Step<CompleteOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.CompleteOrderCompleted).To(ctx => ctx.WorkflowData.CompleteOrderCompleted))
            .Build();

        // Act - Execute workflow (should suspend)
        var result = await _workflowEngine.RunAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);

        // Act - Try to resume with event that doesn't meet complex condition
        var invalidApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-007",
            Approved = true,
            Declined = false,
            ApproverName = "John Employee" // Doesn't contain "Manager"
        };

        var invalidResumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, invalidApprovalEvent);

        // Assert - Workflow should still be suspended
        Assert.Equal(WorkflowExecutionStatus.Suspended, invalidResumeResult.Status);
        EventAssert.Rejected(invalidResumeResult, EventRejection.NotMatched);
        
        var stillSuspendedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(stillSuspendedInstance);
        Assert.Equal(WorkflowStatus.Suspended, stillSuspendedInstance.Status);

        // Act - Resume with event that meets complex condition
        var validApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-007",
            Approved = true,
            Declined = false,
            ApproverName = "John Manager" // Contains "Manager"
        };

        var validResumeResult = await _workflowEngine.ResumeAsync(result.InstanceId, validApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.Equal(WorkflowExecutionStatus.Success, validResumeResult.Status);
        
        var completedInstance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(completedInstance);
        Assert.Equal(WorkflowStatus.Completed, completedInstance.Status);
        
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SuspendResumeTestData)validResumeResult.WorkflowData!;
        Assert.True(finalWorkflowData.CompleteOrderCompleted);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    ///     Mock implementation of IWorkflowStateRepository for testing
    /// </summary>
    private class MockWorkflowStateRepository : InMemoryWorkflowStateRepository
    {
        public async Task<List<WorkflowInstance>> GetWorkflowInstancesAsync(string workflowName, int? version = null)
        {
            var instances = await Task.WhenAll((await QueryInstancesAsync(new InstanceQuery(WorkflowName: workflowName, Limit: int.MaxValue))).Items.Select(async row => (await GetWorkflowInstanceAsync(row.InstanceId))!));
            return instances.Where(instance => !version.HasValue || instance.WorkflowVersion == version.Value).ToList();
        }
    }
}
