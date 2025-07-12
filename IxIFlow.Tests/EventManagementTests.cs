using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests;

public class EventManagementTests
{
    private readonly WorkflowEventManager _eventManager;
    private readonly InMemoryEventRepository _eventRepository;
    private readonly Mock<ILogger<WorkflowEventManager>> _mockManagerLogger;
    private readonly Mock<ILogger<InMemoryEventRepository>> _mockRepositoryLogger;
    private readonly Mock<ISuspensionManager> _mockSuspensionManager;
    private readonly IServiceProvider _serviceProvider;

    public EventManagementTests()
    {
        // Setup mocks
        _mockSuspensionManager = new Mock<ISuspensionManager>();
        _mockRepositoryLogger = new Mock<ILogger<InMemoryEventRepository>>();
        _mockManagerLogger = new Mock<ILogger<WorkflowEventManager>>();

        // Setup service provider
        var services = new ServiceCollection();
        services.AddSingleton(_mockSuspensionManager.Object);
        services.AddSingleton(_mockRepositoryLogger.Object);
        services.AddSingleton(_mockManagerLogger.Object);
        _serviceProvider = services.BuildServiceProvider();

        // Create repository and manager
        _eventRepository = new InMemoryEventRepository(_mockSuspensionManager.Object, _mockRepositoryLogger.Object);
        _eventManager =
            new WorkflowEventManager(_eventRepository, _mockSuspensionManager.Object, _mockManagerLogger.Object);
    }

    [Fact]
    public async Task CreateEventTemplate_ShouldStoreTemplate()
    {
        // Arrange
        var workflowId = Guid.NewGuid().ToString();
        var eventTemplate = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId,
            WorkflowName = "TestWorkflow",
            WorkflowVersion = 1,
            SuspendReason = "Waiting for approval",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };

        // Act
        var result = await _eventRepository.CreateEventTemplateAsync(workflowId, eventTemplate);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(workflowId, result.WorkflowInstanceId);
        Assert.Equal("TestWorkflow", result.WorkflowName);
        Assert.Equal("Waiting for approval", result.SuspendReason);
        Assert.Equal("Pending", result.EventData.ApprovalStatus);
    }

    [Fact]
    public async Task GetEventTemplate_ShouldReturnTemplate()
    {
        // Arrange
        var workflowId = Guid.NewGuid().ToString();
        var eventTemplate = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId,
            WorkflowName = "TestWorkflow",
            WorkflowVersion = 1,
            SuspendReason = "Waiting for approval",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };
        await _eventRepository.CreateEventTemplateAsync(workflowId, eventTemplate);

        // Act
        var result = await _eventRepository.GetEventTemplateAsync<TestEvent>(workflowId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(workflowId, result.WorkflowInstanceId);
        Assert.Equal("TestWorkflow", result.WorkflowName);
        Assert.Equal("Waiting for approval", result.SuspendReason);
        Assert.Equal("Pending", result.EventData.ApprovalStatus);
    }

    [Fact]
    public async Task UpdateEventTemplate_ShouldUpdateTemplate()
    {
        // Arrange
        var workflowId = Guid.NewGuid().ToString();
        var eventTemplate = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId,
            WorkflowName = "TestWorkflow",
            WorkflowVersion = 1,
            SuspendReason = "Waiting for approval",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };
        await _eventRepository.CreateEventTemplateAsync(workflowId, eventTemplate);

        var updatedEvent = new TestEvent { ApprovalStatus = "Approved" };

        // Act
        var result = await _eventRepository.UpdateEventTemplateAsync(workflowId, updatedEvent);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(workflowId, result.WorkflowInstanceId);
        Assert.Equal("Approved", result.EventData.ApprovalStatus);

        // Verify that ProcessEventAsync was called
        _mockSuspensionManager.Verify(m => m.ProcessEventAsync(
                It.Is<TestEvent>(e => e.ApprovalStatus == "Approved"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetEventTemplatesByType_ShouldReturnMatchingTemplates()
    {
        // Arrange
        var workflowId1 = Guid.NewGuid().ToString();
        var workflowId2 = Guid.NewGuid().ToString();
        var workflowId3 = Guid.NewGuid().ToString();

        var eventTemplate1 = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId1,
            WorkflowName = "TestWorkflow1",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };

        var eventTemplate2 = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId2,
            WorkflowName = "TestWorkflow2",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };

        var eventTemplate3 = new EventTemplate<OtherTestEvent>
        {
            WorkflowInstanceId = workflowId3,
            WorkflowName = "TestWorkflow3",
            EventData = new OtherTestEvent { Message = "Test" }
        };

        await _eventRepository.CreateEventTemplateAsync(workflowId1, eventTemplate1);
        await _eventRepository.CreateEventTemplateAsync(workflowId2, eventTemplate2);
        await _eventRepository.CreateEventTemplateAsync(workflowId3, eventTemplate3);

        // Act
        var results = await _eventRepository.GetEventTemplatesByTypeAsync<TestEvent>();

        // Assert
        Assert.Equal(2, results.Count());
        Assert.Contains(results, t => t.WorkflowInstanceId == workflowId1);
        Assert.Contains(results, t => t.WorkflowInstanceId == workflowId2);
    }

    [Fact]
    public async Task WorkflowEventManager_GetEventTemplate_ShouldReturnTemplate()
    {
        // Arrange
        var workflowId = Guid.NewGuid().ToString();
        var eventTemplate = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId,
            WorkflowName = "TestWorkflow",
            WorkflowVersion = 1,
            SuspendReason = "Waiting for approval",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };
        await _eventRepository.CreateEventTemplateAsync(workflowId, eventTemplate);

        // Setup mock for GetSuspendedWorkflowAsync
        var mockWorkflowInstance = new WorkflowInstance
        {
            InstanceId = workflowId,
            Status = WorkflowStatus.Suspended
        };
        _mockSuspensionManager.Setup(m => m.GetSuspendedWorkflowAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockWorkflowInstance);

        // Act
        var result = await _eventManager.GetEventTemplateAsync<TestEvent>(workflowId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(workflowId, result.WorkflowInstanceId);
        Assert.Equal("TestWorkflow", result.WorkflowName);
        Assert.Equal("Waiting for approval", result.SuspendReason);
        Assert.Equal("Pending", result.EventData.ApprovalStatus);
    }

    [Fact]
    public async Task WorkflowEventManager_UpdateEventAndResume_ShouldUpdateAndTriggerResume()
    {
        // Arrange
        var workflowId = Guid.NewGuid().ToString();
        var eventTemplate = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId,
            WorkflowName = "TestWorkflow",
            WorkflowVersion = 1,
            SuspendReason = "Waiting for approval",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };
        await _eventRepository.CreateEventTemplateAsync(workflowId, eventTemplate);

        // Setup mock for GetSuspendedWorkflowAsync
        var mockWorkflowInstance = new WorkflowInstance
        {
            InstanceId = workflowId,
            Status = WorkflowStatus.Suspended
        };
        _mockSuspensionManager.Setup(m => m.GetSuspendedWorkflowAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockWorkflowInstance);

        var updatedEvent = new TestEvent { ApprovalStatus = "Approved" };

        // Act
        var result = await _eventManager.UpdateEventAndResumeAsync(workflowId, updatedEvent);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(workflowId, result.WorkflowInstanceId);
        Assert.Equal("Approved", result.EventData.ApprovalStatus);

        // Verify that ProcessEventAsync was called
        _mockSuspensionManager.Verify(m => m.ProcessEventAsync(
                It.Is<TestEvent>(e => e.ApprovalStatus == "Approved"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WorkflowEventManager_GetSuspendedWorkflowEventTemplates_ShouldReturnTemplates()
    {
        // Arrange
        var workflowId1 = Guid.NewGuid().ToString();
        var workflowId2 = Guid.NewGuid().ToString();

        var eventTemplate1 = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId1,
            WorkflowName = "TestWorkflow1",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };

        var eventTemplate2 = new EventTemplate<TestEvent>
        {
            WorkflowInstanceId = workflowId2,
            WorkflowName = "TestWorkflow2",
            EventData = new TestEvent { ApprovalStatus = "Pending" }
        };

        await _eventRepository.CreateEventTemplateAsync(workflowId1, eventTemplate1);
        await _eventRepository.CreateEventTemplateAsync(workflowId2, eventTemplate2);

        // Act
        var results = await _eventManager.GetSuspendedWorkflowEventTemplatesAsync<TestEvent>();

        // Assert
        Assert.Equal(2, results.Count());
        Assert.Contains(results, t => t.WorkflowInstanceId == workflowId1);
        Assert.Contains(results, t => t.WorkflowInstanceId == workflowId2);
    }

    // Test event classes
    public class TestEvent
    {
        public string ApprovalStatus { get; set; } = "";
    }

    public class OtherTestEvent
    {
        public string Message { get; set; } = "";
    }
}