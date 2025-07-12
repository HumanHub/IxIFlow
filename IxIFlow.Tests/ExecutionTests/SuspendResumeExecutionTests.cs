using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
///     Tests for suspend/resume execution functionality
/// </summary>
public class SuspendResumeExecutionTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;
    private readonly IWorkflowStateRepository _stateRepository;

    public SuspendResumeExecutionTests()
    {
        var services = new ServiceCollection();

        // Register interfaces for WorkflowEngine dependencies
        services.AddSingleton<IActivityExecutor, ActivityExecutor>();
        services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<IWorkflowTracer, WorkflowTracer>();
        services.AddSingleton<IWorkflowInvoker, WorkflowInvoker>();
        services.AddSingleton<ISuspendResumeExecutor, SuspendResumeExecutor>();
        services.AddSingleton<ISagaExecutor, SagaExecutor>();
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();

        // Register event management services
        services.AddSingleton<IEventCorrelator, EventCorrelator>();
        services.AddSingleton<IEventRepository, InMemoryEventRepository>();

        // Register mock implementations for persistence dependencies
        services.AddSingleton<IWorkflowStateRepository, MockWorkflowStateRepository>();
        services.AddSingleton<IEventStore, MockEventStore>();

        // Register the main engine
        services.AddSingleton<WorkflowEngine>();

        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
        // Check workflow instance status
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(workflowData.ProcessOrderCompleted);
        Assert.False(workflowData.CompleteOrderCompleted);
        Assert.NotEmpty(workflowData.ProcessedOrder);

        // Act - Resume with approval event
        var approvalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-001",
            Approved = true,
            Declined = false
        };

        var resumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, approvalEvent);

        // Debug: Check the resume result
        Console.WriteLine($"RESUME RESULT: IsSuccess={resumeResult.IsSuccess}, Status={resumeResult.Status}");
        Console.WriteLine($"WORKFLOW DATA TYPE: {resumeResult.WorkflowData?.GetType().Name}");
        if (resumeResult.WorkflowData is SuspendResumeTestData resumedData)
        {
            Console.WriteLine($"RESUMED DATA: CompleteOrderCompleted={resumedData.CompleteOrderCompleted}");
        }
        Console.WriteLine($"ORIGINAL DATA: CompleteOrderCompleted={workflowData.CompleteOrderCompleted}");

        // Assert - Workflow should complete successfully
        Assert.True(resumeResult.IsSuccess);
        
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
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

        var invalidResumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, invalidApprovalEvent);

        // Assert - Workflow should still be suspended (condition not met)
        Assert.False(invalidResumeResult.IsSuccess);
        
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

        var validResumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, validApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.True(validResumeResult.IsSuccess);
        
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended at first suspend point
        Assert.False(result.IsSuccess);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(workflowData.ProcessOrderCompleted);
        Assert.False(workflowData.ValidateOrderCompleted);

        // Act - Resume with first approval event
        var firstApprovalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-003",
            Approved = true,
            RequiresDoubleApproval = true
        };

        var firstResumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, firstApprovalEvent);

        // Assert - Workflow should be suspended at second suspend point
        Assert.False(firstResumeResult.IsSuccess);
        
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

        var secondResumeResult = await _workflowEngine.ResumeWorkflowAsync(firstResumeResult.InstanceId, secondApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.True(secondResumeResult.IsSuccess);
        
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
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

        var resumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, approvalEvent);

        // Assert - Workflow should complete and event data should be mapped
        Assert.True(resumeResult.IsSuccess);
        
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(workflowData.ProcessOrderCompleted);
        Assert.False(workflowData.CompleteOrderCompleted);

        // Act - Resume with approval event
        var approvalEvent = new ApprovalEvent
        {
            OrderId = "ORDER-005",
            Approved = true,
            Declined = false
        };

        var resumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, approvalEvent);

        // Assert - Workflow should complete successfully
        Assert.True(resumeResult.IsSuccess);
        
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
        var instance = await _stateRepository.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(WorkflowStatus.Suspended, instance.Status);
        
        Assert.True(workflowData.ProcessOrderCompleted);
        Assert.False(workflowData.CompleteOrderCompleted);

        // Verify suspension info is set
        Assert.NotNull(instance.SuspensionInfo);
        Assert.Equal("Waiting for approval with timeout", instance.SuspensionInfo.SuspendReason);
        Assert.Equal(typeof(ApprovalEvent).AssemblyQualifiedName, instance.SuspensionInfo.ResumeEventType);
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
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Workflow should be suspended
        Assert.False(result.IsSuccess);
        
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

        var invalidResumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, invalidApprovalEvent);

        // Assert - Workflow should still be suspended
        Assert.False(invalidResumeResult.IsSuccess);
        
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

        var validResumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, validApprovalEvent);

        // Assert - Workflow should complete successfully
        Assert.True(validResumeResult.IsSuccess);
        
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
    private class MockWorkflowStateRepository : IWorkflowStateRepository
    {
        private readonly Dictionary<string, WorkflowInstance> _instances = new();

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
        {
            _instances.TryGetValue(instanceId, out var instance);
            return Task.FromResult(instance);
        }

        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
        {
            _instances[instance.InstanceId] = instance;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
        {
            var instances = _instances.Values.Where(i => i.WorkflowName == workflowName);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
        {
            var instances = _instances.Values.Where(i => i.Status == status);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
        {
            var instances = _instances.Values.Where(i => i.CorrelationId == correlationId);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync()
        {
            var instances = _instances.Values.Where(i => i.Status == WorkflowStatus.Suspended);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task DeleteWorkflowInstanceAsync(string instanceId)
        {
            _instances.Remove(instanceId);
            return Task.CompletedTask;
        }

        public Task<List<WorkflowInstance>> GetWorkflowInstancesAsync(string workflowName, int? version = null)
        {
            var instances = _instances.Values.Where(i => i.WorkflowName == workflowName);
            if (version.HasValue)
            {
                instances = instances.Where(i => i.WorkflowVersion == version.Value);
            }
            return Task.FromResult(instances.ToList());
        }
    }

    /// <summary>
    ///     Mock implementation of IEventStore for testing
    /// </summary>
    private class MockEventStore : IEventStore
    {
        public Task AppendEventAsync(string streamId, WorkflowEvent workflowEvent)
        {
            return Task.CompletedTask;
        }

        public Task<IEnumerable<WorkflowEvent>> GetEventsAsync(string streamId)
        {
            return Task.FromResult<IEnumerable<WorkflowEvent>>(new List<WorkflowEvent>());
        }

        public Task<IEnumerable<WorkflowEvent>> GetEventsFromSequenceAsync(string streamId, long fromSequenceNumber)
        {
            return Task.FromResult<IEnumerable<WorkflowEvent>>(new List<WorkflowEvent>());
        }

        public Task<long> GetLatestSequenceNumberAsync(string streamId)
        {
            return Task.FromResult(0L);
        }

        public Task SaveEventAsync<TEvent>(string eventId, TEvent eventData, string? correlationId = null)
            where TEvent : class
        {
            return Task.CompletedTask;
        }

        public Task<TEvent?> GetEventAsync<TEvent>(string eventId) where TEvent : class
        {
            return Task.FromResult<TEvent?>(null);
        }

        public Task<List<TEvent>> GetEventsAsync<TEvent>(string? correlationId = null) where TEvent : class
        {
            return Task.FromResult(new List<TEvent>());
        }

        public Task DeleteEventAsync(string eventId)
        {
            return Task.CompletedTask;
        }
    }
}
