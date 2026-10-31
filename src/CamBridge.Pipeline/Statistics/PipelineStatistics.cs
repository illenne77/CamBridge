namespace CamBridge.Pipeline.Statistics;

/// <summary>Point-in-time pipeline counters for display.</summary>
/// <param name="CameraDroppedFrames">Frames lost before reaching the host: reported by the camera as lost, or detected from FrameId gaps.</param>
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

/// <param name="ProcessedFrames">Frames the consumer accepted and finished; frames it declined are not counted.</param>
/// <param name="DroppedFrames">Frames discarded by the queue overflow policy.</param>
/// <param name="Faults">Frames whose processing threw.</param>
/// <param name="PeakQueueLength">Longest the queue has been since start, seen right after each enqueue. A stall between statistics samples still shows here.</param>
/// <param name="MaxProcessingTime">Longest one frame has taken in the consumer since start.</param>
public sealed record ConsumerStatistics(
    string Name,
    long ProcessedFrames,
    double ProcessedFps,
    long DroppedFrames,
    int QueueLength,
    int QueueCapacity,
    long Faults,
    string? LastError,
    int PeakQueueLength,
    TimeSpan MaxProcessingTime);
