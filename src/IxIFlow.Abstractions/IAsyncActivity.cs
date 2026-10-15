namespace IxIFlow.Core;

public interface IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default);
}