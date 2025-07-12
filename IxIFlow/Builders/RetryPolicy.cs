namespace IxIFlow.Builders;

public class RetryPolicy
{
    public RetryPolicy()
    { }

    public RetryPolicy(int maximumAttempts) : this(TimeSpan.Zero, TimeSpan.Zero, maximumAttempts, 1)
    { }

    public RetryPolicy(TimeSpan initialInterval, TimeSpan maximumInterval, int maximumAttempts,
        double backoffCoefficient)
    {
        InitialInterval = initialInterval;
        MaximumInterval = maximumInterval;
        MaximumAttempts = maximumAttempts;
        BackoffCoefficient = backoffCoefficient;
    }

    public TimeSpan InitialInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int MaximumAttempts { get; set; } = 3;
    public double BackoffCoefficient { get; set; } = 2.0; // Exponential backoff coefficient
}