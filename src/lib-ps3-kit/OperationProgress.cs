namespace LibPs3Kit;

/// <summary>Stage of a long-running operation.</summary>
public enum OperationStage
{
    Opening,
    CheckingKey,
    Writing,
    Extracting,
    Verifying,
    Finished,
}

/// <summary>Progress report for UI binding. <see cref="Total"/> may be 0 when unknown.</summary>
public readonly record struct OperationProgress(OperationStage Stage, long Done, long Total, string? CurrentItem = null)
{
    /// <summary>0..1, or null when the total is unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Throttles progress reports so UI threads are not flooded. Always forwards stage changes and completion.
/// </summary>
internal sealed class ThrottledProgress(IProgress<OperationProgress>? inner, TimeSpan? interval = null) : IProgress<OperationProgress>
{
    private readonly long _intervalTicks = (interval ?? TimeSpan.FromMilliseconds(100)).Ticks;
    private long _lastTicks;
    private OperationStage? _lastStage;

    public void Report(OperationProgress value)
    {
        if (inner is null) return;
        var now = DateTime.UtcNow.Ticks;
        var stageChanged = _lastStage != value.Stage;
        var finished = value.Total > 0 && value.Done >= value.Total;
        if (!stageChanged && !finished && now - _lastTicks < _intervalTicks) return;
        _lastTicks = now;
        _lastStage = value.Stage;
        inner.Report(value);
    }
}
