using CamBridge.Core;
using CamBridge.Pipeline;

namespace CamBridge.Inspection;

public sealed class InspectionResultEventArgs(InspectionResult result) : EventArgs
{
    public InspectionResult Result { get; } = result;
}

/// <summary>
/// Pipeline consumer that inspects each frame, keeps OK/NG counters and the latest result,
/// and optionally logs to CSV.
/// </summary>
/// <remarks>
/// <see cref="ResultProduced"/> is raised on the pipeline worker thread; UI handlers must
/// marshal to the dispatcher.
/// </remarks>
public sealed class InspectionConsumer(InspectionEngine engine, CsvResultLogger? csvLogger = null) : IFrameConsumer
{
    private InspectionResult? _latest;
    private long _okCount;
    private long _ngCount;

    public string Name => "Inspection";

    public long OkCount => Interlocked.Read(ref _okCount);

    public long NgCount => Interlocked.Read(ref _ngCount);

    /// <summary>Most recent result, e.g. for drawing overlays.</summary>
    public InspectionResult? LatestResult => Volatile.Read(ref _latest);

    public event EventHandler<InspectionResultEventArgs>? ResultProduced;

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _okCount, 0);
        Interlocked.Exchange(ref _ngCount, 0);
    }

    public ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
    {
        InspectionResult result = engine.Inspect(frame);

        Interlocked.Increment(ref result.Verdict == Verdict.Ok ? ref _okCount : ref _ngCount);
        Volatile.Write(ref _latest, result);
        csvLogger?.Write(result);
        ResultProduced?.Invoke(this, new InspectionResultEventArgs(result));

        return ValueTask.CompletedTask;
    }
}
