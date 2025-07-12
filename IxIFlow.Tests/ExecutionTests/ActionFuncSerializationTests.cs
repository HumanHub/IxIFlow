using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace IxIFlow.Tests.ExecutionTests;

public class ActionFuncSerializationTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public ActionFuncSerializationTests()
    {
        // Setup DI container - same pattern as ConditionalTests
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<IActivityExecutor, ActivityExecutor>();
        services.AddSingleton<ISuspendResumeExecutor, SuspendResumeExecutor>();
        services.AddSingleton<ISagaExecutor, SagaExecutor>();
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();
        services.AddSingleton<IEventCorrelator, EventCorrelator>();
        services.AddSingleton<IWorkflowTracer, WorkflowTracer>();
        services.AddSingleton<IWorkflowStateRepository, InMemoryWorkflowStateRepository>();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<IWorkflowInvoker, WorkflowInvoker>();
        services.AddSingleton<WorkflowEngine>();

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();
    }

    [Fact]
    public async Task ActionExpression_InCatchBlock_ShouldHandleContextCorrectly()
    {
        // Arrange
        var definition = Workflow
            .Create<ActionTestWorkflowData>("ActionExpressionTest")
            .Step<TestInitializationActivity>(setup => setup
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                .Output(step => step.WorkflowId).To(data => data.WorkflowData.Id))
            .Try(tb => tb
                .Step<TestThrowSecurityExceptionActivity>(_ => { })
            )
            .Catch<TestSecurityException>(c => c
                .Step<TestGenericFaultHandler<TestErrorResponse>>(setup => setup
                    .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                    .Input(step => step.ProcessName).From(data => data.WorkflowData.ProcessName)
                    .Input(step => step.Exception).From(data => data.Exception)
                    .Input(step => step.OnException).From(data => CreateSecurityErrorResponse)
                    .Output(step => step.ResponseHeader).To(data => data.WorkflowData.ErrorResponse)
                )
            )
            .Build();

        var data = new ActionTestWorkflowData();

        // Act & Assert - Should not throw serialization exception
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, data);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(data.ErrorResponse);
        Assert.Equal("Security Error: Security check failed. Invalid credentials.", data.ErrorResponse.Status.StatusDesc);
    }

    [Fact]
    public async Task NestedLambdaExpression_ShouldBeReplacedWithStaticMethod()
    {
        // Arrange
        var definition = Workflow
            .Create<StaticMethodTestWorkflowData>("StaticMethodTest")
            .Step<TestInitializationActivity>(setup => setup
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                .Output(step => step.WorkflowId).To(data => data.WorkflowData.Id))
            .Try(tb => tb
                .Step<TestThrowSecurityExceptionActivity>(_ => { })
            )
            .Catch<Exception>(c => c
                .Step<TestGenericFaultHandler<TestErrorResponse>>(setup => setup
                    .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                    .Input(step => step.ProcessName).From(data => data.WorkflowData.ProcessName)
                    .Input(step => step.Exception).From(data => data.Exception)
                    .Input(step => step.OnException).From(data => CreateGenericErrorResponse)
                    .Output(step => step.ResponseHeader).To(data => data.WorkflowData.ErrorResponse)
                )
            )
            .Build();

        var data = new StaticMethodTestWorkflowData();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(data.ErrorResponse);
        Assert.Equal("-3", data.ErrorResponse.Status.StatusCode);
    }

    [Fact]
    public async Task AsyncActionExpression_InLogContext_ShouldExecuteCorrectly()
    {
        // Arrange
        var definition = Workflow
            .Create<AsyncActionTestWorkflowData>("AsyncActionTest")
            .Step<TestAsyncLogActivity>(_ => { })
            .Build();

        var data = new AsyncActionTestWorkflowData();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.True(data.AsyncLogExecuted);
    }

    [Fact]
    public async Task ComplexWorkflow_WithMultipleCatchBlocks_ShouldHandleCorrectly()
    {
        // Arrange
        var definition = Workflow
            .Create<ComplexCatchTestWorkflowData>("ComplexCatchTest")
            .Step<TestInitializationActivity>(setup => setup
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                .Output(step => step.WorkflowId).To(data => data.WorkflowData.Id))
            .Try(tb => tb
                .Step<TestSecurityCheckActivity>(setup => setup
                    .Input(step => step.Username).From(data => data.WorkflowData.Username)
                    .Input(step => step.Password).From(data => data.WorkflowData.Password)
                    .Input(step => step.WorkflowName).From(data => data.WorkflowData.ProcessName))
                .Step<TestThrowSecurityExceptionActivity>(_ => { })
            )
            .Catch<TestSecurityException>(c => c
                .Step<TestGenericFaultHandler<TestErrorResponse>>(setup => setup
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                    .Input(step => step.ProcessName).From(data => data.WorkflowData.ProcessName)
                    .Input(step => step.Exception).From(data => data.Exception)
                .Input(step => step.OnException).From(data => CreateSecurityErrorResponse)
                    .Output(step => step.ResponseHeader).To(data => data.WorkflowData.ErrorResponse)
                )
            )
            .Catch<Exception>(c => c
                .Step<TestGenericFaultHandler<TestErrorResponse>>(setup => setup
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                    .Input(step => step.ProcessName).From(data => data.WorkflowData.ProcessName)
                    .Input(step => step.Exception).From(data => data.Exception)
                .Input(step => step.OnException).From(data => CreateGenericErrorResponse)
                    .Output(step => step.ResponseHeader).To(data => data.WorkflowData.ErrorResponse)
                )
            )
            .Build();

        var data = new ComplexCatchTestWorkflowData();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(data.ErrorResponse);
        Assert.Contains("Security Error:", data.ErrorResponse.Status.StatusDesc);
    }

    [Fact]
    public async Task WorkflowDataMethods_ShouldBeSerializable()
    {
        // Arrange - This demonstrates the BEST approach: methods on WorkflowData
        var definition = Workflow
            .Create<WorkflowDataWithMethods>("WorkflowDataMethodTest")
            .Step<TestLogActivity>(setup => setup
                .Input(step => step.OnLog).From(data => data.WorkflowData.OnLogInit)
                .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id))
            .Try(tb => tb
                .Step<TestThrowSecurityExceptionActivity>(_ => { })
            )
            .Catch<TestSecurityException>(c => c
                .Step<TestGenericFaultHandler<TestErrorResponse>>(setup => setup
                    .Input(step => step.WorkflowId).From(data => data.WorkflowData.Id)
                    .Input(step => step.ProcessName).From(data => data.WorkflowData.ProcessName)
                    .Input(step => step.Exception).From(data => data.Exception)
                .Input(step => step.OnException).From(data => data.WorkflowData.CreateSecurityError)
                    .Output(step => step.ResponseHeader).To(data => data.WorkflowData.ErrorResponse)
                )
            )
            .Build();

        var data = new WorkflowDataWithMethods();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(data.ErrorResponse);
        Assert.Contains("Security Error for WorkflowDataMethodTest:", data.ErrorResponse.Status.StatusDesc);
    }

    #region Test Workflow Data Models

    /// <summary>
    /// Creates a security error response - replaces nested lambda expressions
    /// </summary>
    public TestErrorResponse CreateSecurityErrorResponse(TestExceptionContext context)
    {
        return new TestErrorResponse
        {
            Status = new TestStatus
            {
                StatusCode = "-3",
                Severity = "Error",
                StatusDesc = $"Security Error: {context.Exception.Message}"
            }
        };
    }

    /// <summary>
    /// Creates a generic error response - replaces nested lambda expressions
    /// </summary>
    public TestErrorResponse CreateGenericErrorResponse(TestExceptionContext context)
    {
        return new TestErrorResponse
        {
            Status = new TestStatus
            {
                StatusCode = "-3",
                Severity = "Error",
                StatusDesc = $"Bur. Error: {context.Exception.Message}"
            }
        };
    }

    /// <summary>
    /// Creates a validation error response
    /// </summary>
    public TestErrorResponse CreateValidationErrorResponse(TestExceptionContext context)
    {
        return new TestErrorResponse
        {
            Status = new TestStatus
            {
                StatusCode = "-2",
                Severity = "Error",
                StatusDesc = $"Validation Error: {context.Exception.Message}"
            }
        };
    }

    /// <summary>
    /// Creates a timeout error response
    /// </summary>
    public TestErrorResponse CreateTimeoutErrorResponse(TestExceptionContext context)
    {
        return new TestErrorResponse
        {
            Status = new TestStatus
            {
                StatusCode = "-5",
                Severity = "Error",
                StatusDesc = $"Timeout Error: {context.Exception.Message}"
            }
        };
    }

    public class ActionTestWorkflowData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProcessName => "ActionExpressionTest";
        public TestErrorResponse ErrorResponse { get; set; }
    }

    public class StaticMethodTestWorkflowData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProcessName => "StaticMethodTest";
        public TestErrorResponse ErrorResponse { get; set; }
    }

    public class AsyncActionTestWorkflowData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public bool AsyncLogExecuted { get; set; }
    }

    public class ComplexCatchTestWorkflowData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProcessName => "ComplexCatchTest";
        public TestErrorResponse ErrorResponse { get; set; }
        public string Username { get; set; } = "TEST123";
        public string Password { get; set; } = "password";
    }

    public class TestErrorResponse
    {
        public TestStatus Status { get; set; } = new();
    }

    public class TestStatus
    {
        public string StatusCode { get; set; } = "";
        public string StatusDesc { get; set; } = "";
        public string Severity { get; set; } = "";
    }

    /// <summary>
    /// Demonstrates the BEST approach: methods on WorkflowData classes
    /// These are serializable because WorkflowData is part of the workflow state
    /// </summary>
    public class WorkflowDataWithMethods
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProcessName => "WorkflowDataMethodTest";
        public TestErrorResponse ErrorResponse { get; set; }

        public void OnLogInit(TestLogContext ctx)
        {
            ctx.LogMessage = $"OnLogInit: Workflow started: {ProcessName}";
            ctx.Severity = "Info";
            ctx.WorkflowId = Id;
        }

        public async Task OnLogInitAsync(TestLogContext ctx)
        {
            ctx.LogMessage = $"OnLogInitAsync: Workflow started: {ProcessName}";
            ctx.Severity = "Info";
            ctx.WorkflowId = Id;
            await Task.CompletedTask;
        }

        public TestErrorResponse CreateSecurityError(TestExceptionContext context)
        {
            return new TestErrorResponse
            {
                Status = new TestStatus
                {
                    StatusCode = "-3",
                    Severity = "Error",
                    StatusDesc = $"Security Error for {ProcessName}: {context.Exception.Message}"
                }
            };
        }
    }

    public class TestLogContext
    {
        public string LogMessage { get; set; } = "";
        public string Severity { get; set; } = "";
        public string WorkflowId { get; set; } = "";
    }

    #endregion

    #region Test Activities

    /// <summary>
    /// Custom exception for testing - replaces SecurityException
    /// </summary>
    public class TestSecurityException : Exception
    {
        public TestSecurityException(string message) : base(message) { }
        public TestSecurityException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class TestInitializationActivity : IAsyncActivity
    {
        public string WorkflowId { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(WorkflowId))
            {
                WorkflowId = Guid.NewGuid().ToString();
            }
            await Task.CompletedTask;
        }
    }

    public class TestThrowSecurityExceptionActivity : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken); // Simulate some work
            throw new TestSecurityException("Security check failed. Invalid credentials.");
        }
    }

    public class TestAsyncLogActivity : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var data = (AsyncActionTestWorkflowData)context.WorkflowData;
            await Task.Delay(10, cancellationToken);
            data.AsyncLogExecuted = true;
        }
    }

    public class TestSecurityCheckActivity : IAsyncActivity
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string WorkflowName { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                throw new TestSecurityException("Invalid credentials provided");
            }

            // Simulate security check failure
            throw new TestSecurityException("Security check failed. Invalid credentials.");
        }
    }

    public class TestGenericFaultHandler<T> : IAsyncActivity where T : class, new()
    {
        public string WorkflowId { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public Exception Exception { get; set; }
        public Func<TestExceptionContext, T> OnException { get; set; }
        public T ResponseHeader { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            if (OnException != null)
            {
                var exceptionContext = new TestExceptionContext
                {
                    Exception = Exception,
                    WorkflowId = WorkflowId,
                    ProcessName = ProcessName
                };

                ResponseHeader = OnException(exceptionContext);
            }
            else
            {
                ResponseHeader = new T();
            }
        }
    }

    public class TestLogActivity : IAsyncActivity
    {
        [JsonIgnore]
        public Action<TestLogContext> OnLog { get; set; }
        public string WorkflowId { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            if (OnLog != null)
            {
                var logContext = new TestLogContext
                {
                    WorkflowId = WorkflowId
                };

                OnLog(logContext);

                // Log the message using the workflow logger
                context.Logger.LogInformation($"[{logContext.Severity}] {logContext.LogMessage}");
            }
        }
    }

    public class TestExceptionContext
    {
        public Exception Exception { get; set; }
        public string WorkflowId { get; set; } = "";
        public string ProcessName { get; set; } = "";
    }

    #endregion

}
