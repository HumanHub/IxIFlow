using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Tests for distributed WorkflowHost functionality including tag-based selection,
/// coordination, and distributed execution scenarios
/// </summary>
public class WorkflowHostDistributionTests
{
    [Fact(Timeout = 1000)]
    public async Task WorkflowHost_Should_Execute_Basic_Workflow_Successfully()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Add logging for DI resolution
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "test-host-01";
            options.EndpointUrl = "http://test-host-01:5000";
            options.Tags = new[] { "test", "basic" };
            options.MaxConcurrentWorkflows = 10;
        });
        
        // Use in-memory implementations for testing
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        // Register the host
        await workflowHost.StartAsync();
        
        var workflowDefinition = Workflow
            .Create<SequenceTestData>("BasicWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Test")
                .Input(act => act.StepNumber).From(ctx => 1)
                .Output(act => act.StepOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        var workflowData = new SequenceTestData { TestId = "test-001", StepCount = 1 };
        
        // Act
        var result = await workflowHost.ExecuteWorkflowAsync(workflowDefinition, workflowData);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.IsType<SequenceTestData>(result.WorkflowData);
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowCoordinator_Should_Select_Host_Based_On_Required_Tags()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IWorkflowHostClient, MockWorkflowHostClient>();
        
        var serviceProvider = services.BuildServiceProvider();
        var coordinator = serviceProvider.GetRequiredService<IWorkflowCoordinator>();
        var hostRegistry = serviceProvider.GetRequiredService<IHostRegistry>();
        
        // Register multiple hosts with different tags
        await hostRegistry.RegisterHostAsync("gpu-host-01", "http://gpu-host-01:5000", 
            new HostCapabilities { Tags = new[] { "gpu", "ml", "cuda" }, Weight = 4, MaxConcurrentWorkflows = 50 });
        
        await hostRegistry.RegisterHostAsync("general-host-01", "http://general-host-01:5000", 
            new HostCapabilities { Tags = new[] { "general", "api" }, Weight = 2, MaxConcurrentWorkflows = 100 });
        
        await hostRegistry.RegisterHostAsync("compliance-host-01", "http://compliance-host-01:5000", 
            new HostCapabilities { Tags = new[] { "compliance", "gdpr", "eu-region" }, Weight = 1, MaxConcurrentWorkflows = 25 });
        
        // Act - Select host requiring GPU capabilities
        var selectedHostId = await coordinator.SelectHostForWorkflowAsync(new WorkflowOptions
        {
            RequiredTags = new[] { "gpu", "ml" }
        });
        
        // Assert
        Assert.Equal("gpu-host-01", selectedHostId);
    }

    [Fact]
    public async Task WorkflowCoordinator_Should_Throw_When_Required_Tags_Not_Available()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IWorkflowHostClient, MockWorkflowHostClient>();
        
        var serviceProvider = services.BuildServiceProvider();
        var coordinator = serviceProvider.GetRequiredService<IWorkflowCoordinator>();
        var hostRegistry = serviceProvider.GetRequiredService<IHostRegistry>();
        
        // Register hosts without required tags
        await hostRegistry.RegisterHostAsync("general-host-01", "http://general-host-01:5000", 
            new HostCapabilities { Tags = new[] { "general", "api" }, Weight = 2, MaxConcurrentWorkflows = 100 });
        
        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => 
            await coordinator.SelectHostForWorkflowAsync(new WorkflowOptions
            {
                RequiredTags = new[] { "gpu", "quantum-computing" },
                AllowTagFallback = false
            }));
        
        Assert.Contains("No hosts available with required tags: gpu, quantum-computing", exception.Message);
    }

    [Fact]
    public async Task WorkflowCoordinator_Should_Prefer_Hosts_With_Preferred_Tags()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IWorkflowHostClient, MockWorkflowHostClient>();
        
        var serviceProvider = services.BuildServiceProvider();
        var coordinator = serviceProvider.GetRequiredService<IWorkflowCoordinator>();
        var hostRegistry = serviceProvider.GetRequiredService<IHostRegistry>();
        
        // Register hosts with different capabilities
        await hostRegistry.RegisterHostAsync("gpu-host-basic", "http://gpu-host-basic:5000", 
            new HostCapabilities { Tags = new[] { "gpu", "ml" }, Weight = 2, MaxConcurrentWorkflows = 50 });
        
        await hostRegistry.RegisterHostAsync("gpu-host-premium", "http://gpu-host-premium:5000", 
            new HostCapabilities { Tags = new[] { "gpu", "ml", "cuda", "high-memory" }, Weight = 4, MaxConcurrentWorkflows = 50 });
        
        // Act - Select host preferring CUDA and high-memory
        var selectedHostId = await coordinator.SelectHostForWorkflowAsync(new WorkflowOptions
        {
            RequiredTags = new[] { "gpu", "ml" },
            PreferredTags = new[] { "cuda", "high-memory" }
        });
        
        // Assert - Should select the premium host with preferred tags
        Assert.Equal("gpu-host-premium", selectedHostId);
    }

    [Fact]
    public async Task WorkflowCoordinator_Should_Select_Specific_Host_When_Required()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IWorkflowHostClient, MockWorkflowHostClient>();
        
        var serviceProvider = services.BuildServiceProvider();
        var coordinator = serviceProvider.GetRequiredService<IWorkflowCoordinator>();
        var hostRegistry = serviceProvider.GetRequiredService<IHostRegistry>();
        
        // Register multiple hosts
        await hostRegistry.RegisterHostAsync("host-01", "http://host-01:5000", 
            new HostCapabilities { Tags = new[] { "general" }, Weight = 2, MaxConcurrentWorkflows = 100 });
        
        await hostRegistry.RegisterHostAsync("host-02", "http://host-02:5000", 
            new HostCapabilities { Tags = new[] { "general" }, Weight = 2, MaxConcurrentWorkflows = 100 });
        
        // Act - Require specific host
        var selectedHostId = await coordinator.SelectHostForWorkflowAsync(new WorkflowOptions
        {
            RequiredHostId = "host-02"
        });
        
        // Assert
        Assert.Equal("host-02", selectedHostId);
    }

    [Fact]
    public async Task HostRegistry_Should_Track_Host_Heartbeats()
    {
        // Arrange
        var hostRegistry = new InMemoryHostRegistry();
        var hostId = "test-host-01";
        var capabilities = new HostCapabilities 
        { 
            Tags = new[] { "test" }, 
            Weight = 1, 
            MaxConcurrentWorkflows = 10 
        };
        
        // Act
        await hostRegistry.RegisterHostAsync(hostId, "http://test-host-01:5000", capabilities);
        
        var initialRegistration = await hostRegistry.GetHostAsync(hostId);
        var initialHeartbeat = initialRegistration?.LastHeartbeat;
        
        // Wait a bit and update heartbeat
        await Task.Delay(100);
        await hostRegistry.UpdateHeartbeatAsync(hostId);
        
        var updatedRegistration = await hostRegistry.GetHostAsync(hostId);
        var updatedHeartbeat = updatedRegistration?.LastHeartbeat;
        
        // Assert
        Assert.NotNull(initialHeartbeat);
        Assert.NotNull(updatedHeartbeat);
        Assert.True(updatedHeartbeat > initialHeartbeat);
    }

    [Fact]
    public async Task MessageBus_Should_Publish_And_Consume_Messages()
    {
        // Arrange
        var messageBus = new InMemoryMessageBus();
        var testCommand = new WorkflowExecutionCommand
        {
            WorkflowDefinitionId = "test-workflow",
            WorkflowData = "test-data",
            Priority = 100
        };
        
        // Act
        await messageBus.PublishAsync(testCommand);
        
        var messages = new List<WorkflowExecutionCommand>();
        await foreach (var message in messageBus.ConsumeAsync<WorkflowExecutionCommand>())
        {
            messages.Add(message);
            if (messages.Count >= 1) break; // Take only 1 message
        }
        
        // Assert
        Assert.Single(messages);
        Assert.Equal("test-workflow", messages.First().WorkflowDefinitionId);
        Assert.Equal(100, messages.First().Priority);
    }

    [Fact(Timeout = 1000)]
    public async Task WorkflowHost_Should_Handle_Immediate_Execution()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Add logging for DI resolution
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "immediate-host";
            options.EndpointUrl = "http://immediate-host:5000";
            options.AllowImmediateExecution = true;
            options.AllowCapacityOverride = true;
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        var workflowDefinition = Workflow
            .Create<SequenceTestData>("ImmediateWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Immediate")
                .Input(act => act.StepNumber).From(ctx => 1)
                .Output(act => act.StepOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        var workflowData = new SequenceTestData { TestId = "immediate-test" };
        
        // Act
        var result = await workflowHost.ExecuteImmediateAsync(workflowDefinition, workflowData, 
            new ImmediateExecutionOptions
            {
                BypassQueue = true,
                OverrideCapacityLimit = true,
                Priority = int.MaxValue
            });
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowHost_Should_Report_Health_Status()
    {
        // Arrange
        var services = new ServiceCollection();
        
        // Add logging for DI resolution
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "health-test-host";
            options.EndpointUrl = "http://health-test-host:5000";
            options.MaxConcurrentWorkflows = 50;
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        // Act
        var health = await workflowHost.GetHealthAsync();
        var canAccept = await workflowHost.CanAcceptWorkflow();
        
        // Assert
        Assert.NotNull(health);
        Assert.True(health.IsHealthy);
        Assert.Equal("health-test-host", health.HostId);
        Assert.True(canAccept);
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowHost_Should_Reject_Workflows_At_Capacity()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "capacity-test-host";
            options.EndpointUrl = "http://capacity-test-host:5000";
            options.MaxConcurrentWorkflows = 3; // Small capacity for fast testing
            options.AllowCapacityOverride = false; // Strict capacity enforcement
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        // Create slow workflow definition (1 second delay)
        var slowWorkflowDefinition = Workflow
            .Create<SequenceTestData>("SlowWorkflow")
            .Step<SlowExecutionAsyncActivity>(setup => setup
                .Input(act => act.TaskName).From(ctx => "Capacity Test")
                .Input(act => act.DelayMs).From(ctx => 1000)
                .Input(act => act.TaskId).From(ctx => ctx.WorkflowData.StepCount)
                .Output(act => act.TaskOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        // Act - Fill host to capacity with concurrent workflows
        var tasks = new List<Task<WorkflowExecutionResult>>();
        for (int i = 0; i < 3; i++)
        {
            var workflowData = new SequenceTestData { TestId = $"capacity-test-{i}", StepCount = i + 1 };
            tasks.Add(workflowHost.ExecuteWorkflowAsync(slowWorkflowDefinition, workflowData));
        }
        
        // Wait a bit for workflows to start
        await Task.Delay(100);
        
        // Verify host is at capacity
        Assert.Equal(3, workflowHost.CurrentWorkflowCount);
        Assert.False(await workflowHost.CanAcceptWorkflow());
        
        // Try to add one more workflow - should fail
        var overflowData = new SequenceTestData { TestId = "overflow-test", StepCount = 99 };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflowHost.ExecuteWorkflowAsync(slowWorkflowDefinition, overflowData));
        
        Assert.Contains("at capacity", exception.Message);
        
        // Wait for all workflows to complete
        var results = await Task.WhenAll(tasks);
        
        // Assert
        Assert.All(results, result => Assert.Equal(WorkflowExecutionStatus.Success, result.Status));
        Assert.Equal(0, workflowHost.CurrentWorkflowCount); // Should be empty after completion
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowHost_Should_Allow_Immediate_Execution_With_Override()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "override-test-host";
            options.EndpointUrl = "http://override-test-host:5000";
            options.MaxConcurrentWorkflows = 2; // Small capacity
            options.AllowImmediateExecution = true;
            options.AllowCapacityOverride = true; // Allow override
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        // Create workflow definitions
        var slowWorkflowDefinition = Workflow
            .Create<SequenceTestData>("SlowWorkflow")
            .Step<SlowExecutionAsyncActivity>(setup => setup
                .Input(act => act.TaskName).From(ctx => "Background")
                .Input(act => act.DelayMs).From(ctx => 800)
                .Input(act => act.TaskId).From(ctx => ctx.WorkflowData.StepCount)
                .Output(act => act.TaskOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();

        var fastWorkflowDefinition = Workflow
            .Create<SequenceTestData>("FastWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Fast")
                .Input(act => act.StepNumber).From(ctx => 999)
                .Output(act => act.StepOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        // Act - Fill host to capacity
        var backgroundTasks = new List<Task<WorkflowExecutionResult>>();
        for (int i = 0; i < 2; i++)
        {
            var workflowData = new SequenceTestData { TestId = $"background-{i}", StepCount = i + 1 };
            backgroundTasks.Add(workflowHost.ExecuteWorkflowAsync(slowWorkflowDefinition, workflowData));
        }
        
        // Wait for workflows to start
        await Task.Delay(100);
        
        // Verify host is at capacity
        Assert.Equal(2, workflowHost.CurrentWorkflowCount);
        
        // Execute immediate workflow with capacity override - should succeed!
        var immediateData = new SequenceTestData { TestId = "immediate-override", StepCount = 999 };
        var immediateResult = await workflowHost.ExecuteImmediateAsync(fastWorkflowDefinition, immediateData,
            new ImmediateExecutionOptions
            {
                BypassQueue = true,
                OverrideCapacityLimit = true, // Override capacity!
                Priority = int.MaxValue
            });
        
        // Assert immediate execution succeeded despite capacity
        Assert.Equal(WorkflowExecutionStatus.Success, immediateResult.Status);
        var immediateWorkflowData = immediateResult.WorkflowData as SequenceTestData;
        Assert.NotNull(immediateWorkflowData);
        Assert.Contains("STEP-999-Fast-COMPLETED", immediateWorkflowData.FinalResult);
        
        // Wait for background workflows to complete
        var backgroundResults = await Task.WhenAll(backgroundTasks);
        Assert.All(backgroundResults, result => Assert.Equal(WorkflowExecutionStatus.Success, result.Status));
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowHost_Should_Execute_Workflows_Concurrently_Within_Limits()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "concurrent-test-host";
            options.EndpointUrl = "http://concurrent-test-host:5000";
            options.MaxConcurrentWorkflows = 5;
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        var workflowDefinition = Workflow
            .Create<SequenceTestData>("ConcurrentWorkflow")
            .Step<SlowExecutionAsyncActivity>(setup => setup
                .Input(act => act.TaskName).From(ctx => "Concurrent")
                .Input(act => act.DelayMs).From(ctx => 500) // 500ms delay
                .Input(act => act.TaskId).From(ctx => ctx.WorkflowData.StepCount)
                .Output(act => act.TaskOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        // Act - Launch 5 concurrent workflows
        var startTime = DateTime.UtcNow;
        var tasks = new List<Task<WorkflowExecutionResult>>();
        
        for (int i = 0; i < 5; i++)
        {
            var workflowData = new SequenceTestData { TestId = $"concurrent-{i}", StepCount = i + 1 };
            tasks.Add(workflowHost.ExecuteWorkflowAsync(workflowDefinition, workflowData));
        }
        
        // Check that workflows are running concurrently
        await Task.Delay(100);
        Assert.True(workflowHost.CurrentWorkflowCount > 1, "Workflows should be running concurrently");
        
        var results = await Task.WhenAll(tasks);
        var endTime = DateTime.UtcNow;
        var totalTime = (endTime - startTime).TotalMilliseconds;
        
        // Assert
        Assert.All(results, result => Assert.Equal(WorkflowExecutionStatus.Success, result.Status));
        
        // If they ran concurrently, total time should be much less than 5 * 500ms = 2500ms
        // Allow some buffer for test execution overhead
        Assert.True(totalTime < 1500, $"Expected concurrent execution to take < 1500ms, but took {totalTime}ms");
        
        await workflowHost.StopAsync();
    }

    [Fact]
    public async Task WorkflowCoordinator_Should_Distribute_Load_Across_Multiple_Hosts()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IWorkflowHostClient, MockWorkflowHostClient>();
        
        var serviceProvider = services.BuildServiceProvider();
        var coordinator = serviceProvider.GetRequiredService<IWorkflowCoordinator>();
        var hostRegistry = serviceProvider.GetRequiredService<IHostRegistry>();
        
        // Register multiple hosts with different weights and capacities
        await hostRegistry.RegisterHostAsync("host-01", "http://host-01:5000", 
            new HostCapabilities { Weight = 1, MaxConcurrentWorkflows = 10, Tags = new[] { "general" } });
        
        await hostRegistry.RegisterHostAsync("host-02", "http://host-02:5000", 
            new HostCapabilities { Weight = 2, MaxConcurrentWorkflows = 20, Tags = new[] { "general" } });
        
        await hostRegistry.RegisterHostAsync("host-03", "http://host-03:5000", 
            new HostCapabilities { Weight = 3, MaxConcurrentWorkflows = 30, Tags = new[] { "general" } });
        
        // Simulate different load levels by updating host status
        await hostRegistry.UpdateHostStatusAsync("host-01", new HostStatus 
        { 
            HostId = "host-01", IsHealthy = true, CurrentWorkflowCount = 5, MaxConcurrentWorkflows = 10, Weight = 1 
        });
        
        await hostRegistry.UpdateHostStatusAsync("host-02", new HostStatus 
        { 
            HostId = "host-02", IsHealthy = true, CurrentWorkflowCount = 10, MaxConcurrentWorkflows = 20, Weight = 2 
        });
        
        await hostRegistry.UpdateHostStatusAsync("host-03", new HostStatus 
        { 
            HostId = "host-03", IsHealthy = true, CurrentWorkflowCount = 3, MaxConcurrentWorkflows = 30, Weight = 3 
        });
        
        // Act - Select hosts multiple times and track distribution
        var selections = new Dictionary<string, int>();
        
        for (int i = 0; i < 100; i++)
        {
            var selectedHost = await coordinator.SelectHostForWorkflowAsync(new WorkflowOptions());
            selections[selectedHost] = selections.GetValueOrDefault(selectedHost, 0) + 1;
        }
        
        // Assert - Host 3 should get most selections due to lowest load/weight ratio
        Assert.True(selections.ContainsKey("host-03"));
        Assert.True(selections["host-03"] > selections.GetValueOrDefault("host-01", 0));
        Assert.True(selections["host-03"] > selections.GetValueOrDefault("host-02", 0));
    }

    [Fact]
    public async Task WorkflowHost_Should_Update_Status_During_Workflow_Execution()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        
        services.AddIxIFlowHost(options =>
        {
            options.HostId = "status-test-host";
            options.EndpointUrl = "http://status-test-host:5000";
            options.MaxConcurrentWorkflows = 10;
        });
        
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        
        var serviceProvider = services.BuildServiceProvider();
        var workflowHost = serviceProvider.GetRequiredService<IWorkflowHost>();
        
        await workflowHost.StartAsync();
        
        var workflowDefinition = Workflow
            .Create<SequenceTestData>("StatusTestWorkflow")
            .Step<SlowExecutionAsyncActivity>(setup => setup
                .Input(act => act.TaskName).From(ctx => "Status Test")
                .Input(act => act.DelayMs).From(ctx => 300)
                .Input(act => act.TaskId).From(ctx => 1)
                .Output(act => act.TaskOutput).To(ctx => ctx.WorkflowData.FinalResult))
            .Build();
        
        // Act & Assert
        var initialStatus = workflowHost.Status;
        Assert.Equal("Available", initialStatus.Status);
        Assert.Equal(0, initialStatus.CurrentWorkflowCount);
        
        // Start workflow
        var workflowData = new SequenceTestData { TestId = "status-test", StepCount = 1 };
        var workflowTask = workflowHost.ExecuteWorkflowAsync(workflowDefinition, workflowData);
        
        // Check status during execution
        await Task.Delay(50); // Let workflow start
        var runningStatus = workflowHost.Status;
        Assert.Equal("Busy", runningStatus.Status);
        Assert.Equal(1, runningStatus.CurrentWorkflowCount);
        
        // Wait for completion
        var result = await workflowTask;
        
        // Check final status
        var finalStatus = workflowHost.Status;
        Assert.Equal("Available", finalStatus.Status);
        Assert.Equal(0, finalStatus.CurrentWorkflowCount);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        
        await workflowHost.StopAsync();
    }
}

/// <summary>
/// Mock WorkflowHostClient for testing without actual HTTP calls
/// </summary>
public class MockWorkflowHostClient : IWorkflowHostClient
{
    public async Task<HostHealthResponse> CheckHealthAsync(string hostId, TimeSpan timeout = default)
    {
        await Task.Delay(10); // Simulate network delay
        return new HostHealthResponse
        {
            IsHealthy = true,
            HostId = hostId,
            CheckedAt = DateTime.UtcNow,
            CurrentWorkflowCount = 0,
            MaxWorkflowCount = 100,
            Status = "Available"
        };
    }

    public async Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class
    {
        await Task.Delay(50); // Simulate execution time
        return new WorkflowExecutionResult
        {
            InstanceId = Guid.NewGuid().ToString(),
            Status = WorkflowExecutionStatus.Success,
            WorkflowData = workflowData
        };
    }

    public async Task<HostStatusResponse> GetStatusAsync(string hostId)
    {
        await Task.Delay(10);
        return new HostStatusResponse
        {
            HostId = hostId,
            Status = new HostStatus
            {
                HostId = hostId,
                CurrentWorkflowCount = 0,
                MaxConcurrentWorkflows = 100,
                IsHealthy = true,
                Status = "Available"
            }
        };
    }

    public async Task<bool> CanAcceptWorkflowAsync(string hostId, bool allowOverride = false)
    {
        await Task.Delay(10);
        return true;
    }

    public async Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEvent>(
        string hostId, 
        string instanceId, 
        TEvent @event)
        where TEvent : class
    {
        await Task.Delay(50);
        return new WorkflowExecutionResult
        {
            InstanceId = instanceId,
            Status = WorkflowExecutionStatus.Success,
            WorkflowData = new SequenceTestData()
        };
    }

    public async Task<bool> CancelWorkflowAsync(string hostId, string instanceId, CancellationReason reason)
    {
        await Task.Delay(10);
        return true;
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string hostId, string instanceId)
    {
        await Task.Delay(10);
        return new WorkflowInstance
        {
            InstanceId = instanceId,
            Status = WorkflowStatus.Running
        };
    }

    public async Task<string> QueueWorkflowAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        WorkflowOptions? options = null)
        where TWorkflowData : class
    {
        await Task.Delay(10);
        return Guid.NewGuid().ToString();
    }
}

/// <summary>
/// Workflow execution command for message bus testing
/// </summary>
public class WorkflowExecutionCommand
{
    public string WorkflowDefinitionId { get; set; } = "";
    public string WorkflowData { get; set; } = "";
    public int Priority { get; set; }
}
