using System.Data.Common;

namespace IxIFlow.DemoActivities;

public interface IDemoConnectionFactory
{
    Task<DbConnection> OpenAsync(string connectionRef, CancellationToken cancellationToken);
}
