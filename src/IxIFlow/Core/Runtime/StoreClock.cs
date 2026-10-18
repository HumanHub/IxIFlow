namespace IxIFlow.Core.Runtime;

/// <summary>
/// The store's clock as a run last read it. Deadlines and runnable times are the store's; the
/// host's clock only decides when to look, shifted by the difference between the two at the last read.
/// Until a run has read the store's time it assumes the clocks agree.
/// </summary>
internal sealed class StoreClock
{
    private TimeSpan _offset;

    /// <summary>Records the store's time read when the host's was <paramref name="localUtc"/>.</summary>
    public void Observe(DateTime storeUtc, DateTime localUtc) => _offset = storeUtc - localUtc;

    /// <summary>The store's time when the host's is <paramref name="localUtc"/>.</summary>
    public DateTime ToStore(DateTime localUtc) => localUtc + _offset;

    /// <summary>The host's time when the store's is <paramref name="storeUtc"/>.</summary>
    public DateTime ToLocal(DateTime storeUtc) => storeUtc - _offset;
}
