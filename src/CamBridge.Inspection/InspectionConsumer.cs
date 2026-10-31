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
/// <para>
/// <see cref="ResultProduced"/> is raised on the pipeline worker thread; UI handlers must
/// marshal to the dispatcher.
/// </para>
/// <para>
/// The recipe can change while frames flow: <see cref="ReplaceEngine"/> only parks the new
/// engine, and the worker swaps it in before its next frame, so an engine (not thread-safe)
/// is never used by two threads or disposed while inspecting. The engine and logger given to
/// the constructor stay owned by the caller; those given to <see cref="ReplaceEngine"/> are
/// owned by the consumer and disposed once replaced or when the consumer is disposed.
/// </para>
/// </remarks>
public sealed class InspectionConsumer : IFrameConsumer, IDisposable
{
    private Setup _current;
    private Setup? _pending;
    private InspectionResult? _latest;
    private long _okCount;
    private long _ngCount;
    private int _disposed;

    public InspectionConsumer(InspectionEngine engine, CsvResultLogger? csvLogger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _current = new Setup(engine, csvLogger, Owned: false);
    }

    public string Name => "Inspection";

    public long OkCount => Interlocked.Read(ref _okCount);

    public long NgCount => Interlocked.Read(ref _ngCount);

    /// <summary>Most recent result, e.g. for drawing overlays. May come from the previous recipe right after a swap.</summary>
    public InspectionResult? LatestResult => Volatile.Read(ref _latest);

    public event EventHandler<InspectionResultEventArgs>? ResultProduced;

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _okCount, 0);
        Interlocked.Exchange(ref _ngCount, 0);
    }

    /// <summary>
    /// Schedules <paramref name="engine"/> (and optionally a CSV logger for its measurement
    /// columns) to be used from the next frame on. Callable from any thread. If several
    /// replacements arrive before the next frame, only the last is used and the others are disposed.
    /// </summary>
    public void ReplaceEngine(InspectionEngine engine, CsvResultLogger? csvLogger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var next = new Setup(engine, csvLogger, Owned: true);
        if (Volatile.Read(ref _disposed) != 0)
        {
            next.Release();
            throw new ObjectDisposedException(nameof(InspectionConsumer));
        }

        // A superseded pending setup never ran and can no longer be picked up: safe to release here.
        Interlocked.Exchange(ref _pending, next)?.Release();
    }

    public ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pending, null) is Setup next)
        {
            Setup previous = _current;
            _current = next;
            previous.Release();
        }

        Setup setup = _current;
        InspectionResult result = setup.Engine.Inspect(frame);
        Interlocked.Increment(ref result.Verdict == Verdict.Ok ? ref _okCount : ref _ngCount);
        Volatile.Write(ref _latest, result);
        setup.CsvLogger?.Write(result);
        ResultProduced?.Invoke(this, new InspectionResultEventArgs(result));
        return ValueTask.CompletedTask;
    }

    /// <summary>Releases owned engines and loggers. Stop the pipeline first.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, null)?.Release();
        _current.Release();
    }

    private sealed record Setup(InspectionEngine Engine, CsvResultLogger? CsvLogger, bool Owned)
    {
        public void Release()
        {
            if (Owned)
            {
                Engine.Dispose();
                CsvLogger?.Dispose();
            }
        }
    }
}
