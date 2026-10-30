using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

/// <summary>A scope survives logically across an approval wait; an independent child has no session.</summary>
public static class ScopedApproval
{
    public static WorkflowDefinition Definition() => new WorkflowBuilder<Data, object>([], "ScopedApproval")
        .Scope<MailboxScope>(null, body => body.Step<ReadMailbox>()
            .WaitFor<Decision>("approval")
            .Invoke<Data>("ScopedApprovalChild", 1,
                setup => setup.Output(data => data.ChildSawScope).To(ctx => ctx.WorkflowData.ChildSawScope))
            .Step<ReadMailbox>()).Build();

    public static WorkflowDefinition Child() => Workflow.Create<Data>("ScopedApprovalChild")
        .Step<CheckChild>(s => s.Output(a => a.SawScope).To(c => c.WorkflowData.ChildSawScope)).Build();

    public sealed class Data
    {
        public bool ChildSawScope { get; set; }
    }

    public sealed class Decision;

    public sealed class Probe
    {
        public int Opens;
        public int Reads;
        public int Disposals;
        public List<ScopeCloseReason> Closes { get; } = [];
    }

    public sealed class MailSession(Probe probe) : IDisposable
    {
        private bool _disposed;

        public void Read()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MailSession));

            probe.Reads++;
        }

        public void Dispose()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MailSession));

            _disposed = true;
            probe.Disposals++;
        }
    }

    public sealed class MailboxScope(Probe probe) : IScopeActivity<MailSession>
    {
        public Task<MailSession> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default)
        {
            probe.Opens++;

            return Task.FromResult(new MailSession(probe));
        }

        public Task CloseAsync(MailSession value, ScopeClosing closing, IScopeContext context,
            CancellationToken cancellationToken = default)
        {
            probe.Closes.Add(closing.Reason);
            value.Dispose();

            return Task.CompletedTask;
        }
    }

    [RequiresScope<MailSession>]
    public sealed class ReadMailbox : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            context.Scope<MailSession>().Read();

            return Task.CompletedTask;
        }
    }

    public sealed class CheckChild : IAsyncActivity
    {
        public bool SawScope { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            SawScope = context.ScopeOrDefault<MailSession>() != null;

            return Task.CompletedTask;
        }
    }
}