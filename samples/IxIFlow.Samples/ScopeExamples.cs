using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class ScopeData
{
    public string Reference { get; set; } = "customer-db";
    public string Result { get; set; } = "";
}

public static class ScopeExamples
{
    // snippet: connection
    public static WorkflowDefinition Connection() =>
        new WorkflowBuilder<ScopeData, object>([], "ScopedConnection")
            .Scope<FakeConnectionScope>(setup => setup
                    .Input(scope => scope.ConnectionRef).From(ctx => ctx.WorkflowData.Reference)
                    .Retry(new RetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 2, 2)),
                body => body.Step<ReadConnection>(setup => setup
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result)))
            .Build();

    // end-snippet: connection

    public static WorkflowDefinition Mailbox() =>
        new WorkflowBuilder<ScopeData, object>([], "ScopedMailbox")
            .Scope<MailboxScope>(setup => setup.Input(scope => scope.MailboxRef).From(ctx => ctx.WorkflowData.Reference),
                body => body.Step<ReadMail>(setup => setup.Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result)))
            .Build();

    // snippet: excel
    public static WorkflowDefinition Excel() =>
        new WorkflowBuilder<ScopeData, object>([], "ScopedWorkbook")
            .Scope<ExcelScope>(setup => setup.Input(scope => scope.WorkbookRef).From(ctx => ctx.WorkflowData.Reference),
                body => body.Step<WriteCells>().Step<SaveWorkbook>())
            .Build();

    // end-snippet: excel
}

public interface IConnectionSession : IDisposable
{
    string Query();
}

public interface IConnectionSessionFactory
{
    IConnectionSession Open(string reference);
}

[WorkflowActivity("samples.connection-scope")]
public sealed class FakeConnectionScope(IConnectionSessionFactory factory) : IScopeActivity<IConnectionSession>
{
    [Required] public string ConnectionRef { get; set; } = "";

    public Task<IConnectionSession> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(factory.Open(ConnectionRef));

    public Task CloseAsync(IConnectionSession value, ScopeClosing closing, IScopeContext context,
        CancellationToken cancellationToken = default)
    {
        value.Dispose();

        return Task.CompletedTask;
    }
}

[RequiresScope<IConnectionSession>]
public sealed class ReadConnection : IAsyncActivity
{
    public string Result { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Result = context.Scope<IConnectionSession>().Query();

        return Task.CompletedTask;
    }
}

public interface IMailSession : IDisposable
{
    string Read();
}

public interface IMailSessionFactory
{
    IMailSession Open(string mailboxRef);
}

[WorkflowActivity("samples.mailbox-scope")]
public sealed class MailboxScope(IMailSessionFactory factory) : IScopeActivity<IMailSession>
{
    [Required] public string MailboxRef { get; set; } = "";

    public Task<IMailSession> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(factory.Open(MailboxRef));

    public Task CloseAsync(IMailSession value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken = default)
    {
        value.Dispose();

        return Task.CompletedTask;
    }
}

[RequiresScope<IMailSession>]
public sealed class ReadMail : IAsyncActivity
{
    public string Result { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Result = context.Scope<IMailSession>().Read();

        return Task.CompletedTask;
    }
}

public interface IExcelSession : IDisposable
{
    void Write();
    void Save();
}

public interface IExcelSessionFactory
{
    IExcelSession Open(string workbookRef);
}

[ScopeThreading(ScopeThreading.ThreadAffine)]
[WorkflowActivity("samples.workbook-scope")]
public sealed class ExcelScope(IExcelSessionFactory factory) : IScopeActivity<IExcelSession>
{
    [Required] public string WorkbookRef { get; set; } = "";

    public Task<IExcelSession> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(factory.Open(WorkbookRef));

    public Task CloseAsync(IExcelSession value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken = default)
    {
        value.Dispose();

        return Task.CompletedTask;
    }
}

[RequiresScope<IExcelSession>]
public sealed class WriteCells : IAsyncActivity
{
    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        context.Scope<IExcelSession>().Write();
        await Task.Yield();
        context.Scope<IExcelSession>().Write();
    }
}

[RequiresScope<IExcelSession>]
public sealed class SaveWorkbook : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        context.Scope<IExcelSession>().Save();

        return Task.CompletedTask;
    }
}

/// <summary>A portable fake; checks every call and disposal on the opening thread.</summary>
public sealed class FakeExcelSession : IExcelSession
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    public int Writes { get; private set; }
    public int Saves { get; private set; }
    public bool Disposed { get; private set; }

    private void Check()
    {
        if (_thread != Environment.CurrentManagedThreadId || Disposed)
            throw new InvalidOperationException("Workbook used outside its owning thread or lifetime");
    }

    public void Write()
    {
        Check();
        Writes++;
    }

    public void Save()
    {
        Check();
        Saves++;
    }

    public void Dispose()
    {
        Check();
        Disposed = true;
    }
}

/// <summary>Host-supplied fake factories; names stand in for externally configured resources.</summary>
public sealed class FakeScopeSessions : IConnectionSessionFactory, IMailSessionFactory, IExcelSessionFactory
{
    public int Opens { get; private set; }
    public FakeSession? Session { get; private set; }
    public FakeExcelSession? Workbook { get; private set; }

    IConnectionSession IConnectionSessionFactory.Open(string reference)
    {
        Opens++;

        return Session = new(reference);
    }

    IMailSession IMailSessionFactory.Open(string reference)
    {
        Opens++;

        return Session = new(reference);
    }

    IExcelSession IExcelSessionFactory.Open(string reference)
    {
        Opens++;

        return Workbook = new();
    }

    public sealed class FakeSession(string reference) : IConnectionSession, IMailSession
    {
        public bool Disposed { get; private set; }

        public string Query()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            return reference;
        }

        public string Read() => Query();

        public void Dispose()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Disposed = true;
        }
    }
}