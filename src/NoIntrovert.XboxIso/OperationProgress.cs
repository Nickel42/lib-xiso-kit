namespace NoIntrovert.XboxIso;

/// <summary>Stage of a long-running operation.</summary>
public enum OperationStage
{
    Opening,
    Hashing,
    Analyzing,
    Deduplicating,
    Writing,
    Compressing,
    Verifying,
    Extracting,
    Finished,
}

/// <summary>Progress report for UI binding. <see cref="Total"/> may be 0 when unknown.</summary>
public readonly record struct OperationProgress(OperationStage Stage, long Done, long Total, string? CurrentItem = null)
{
    /// <summary>0..1, or null when the total is unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Throttles progress reports so UI threads (e.g. Avalonia's dispatcher) are not flooded.
/// Always forwards stage changes and completion.
/// </summary>
internal sealed class ThrottledProgress : IProgress<OperationProgress>
{
    private readonly IProgress<OperationProgress>? _inner;
    private readonly long _intervalTicks;
    private long _lastTicks;
    private OperationStage? _lastStage;

    public ThrottledProgress(IProgress<OperationProgress>? inner, TimeSpan? interval = null)
    {
        _inner = inner;
        _intervalTicks = (interval ?? TimeSpan.FromMilliseconds(100)).Ticks;
    }

    public void Report(OperationProgress value)
    {
        if (_inner is null)
        {
            return;
        }
        long now = DateTime.UtcNow.Ticks;
        bool stageChanged = _lastStage != value.Stage;
        bool complete = value.Total > 0 && value.Done >= value.Total;
        if (stageChanged || complete || now - _lastTicks >= _intervalTicks)
        {
            _lastTicks = now;
            _lastStage = value.Stage;
            _inner.Report(value);
        }
    }
}
