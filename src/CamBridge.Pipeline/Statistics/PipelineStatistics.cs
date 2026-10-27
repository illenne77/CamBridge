namespace CamBridge.Pipeline.Statistics;

/// <summary>Point-in-time pipeline counters for display.</summary>
/// <param name="CameraDroppedFrames">Frames lost before reaching the host, detected from FrameId gaps.</param>
/// <param name="LiveViewSkippedFrames">Frames replaced in the live view slot before display; expected, not a loss.</param>
public sealed record PipelineStatistics(
    long ReceivedFrames,
    double ReceivedFps,
    long CameraDroppedFrames,
    long LiveViewSkippedFrames,
    IReadOnlyList<ConsumerStatistics> Consumers)
{
    /// <summary>Camera-side losses plus frames dropped by any consumer queue.</summary>
    public long TotalDroppedFrames => CameraDroppedFrames + Consumers.Sum(c => c.DroppedFrames);
}

/// <param name="DroppedFrames">Frames discarded by the queue overflow policy.</param>
/// <param name="Faults">Frames whose processing threw.</param>
public sealed record ConsumerStatistics(
    string Name,
    long ProcessedFrames,
    double ProcessedFps,
    long DroppedFrames,
    int QueueLength,
    int QueueCapacity,
    long Faults,
    string? LastError);
