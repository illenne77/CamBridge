using CamBridge.Core;

namespace CamBridge.Pipeline;

/// <summary>
/// Processes frames from its own queue on a pipeline worker task.
/// </summary>
/// <remarks>
/// The frame is owned by the pipeline and disposed when <see cref="ProcessAsync"/> completes;
/// call <see cref="Frame.Share"/> to keep it longer. Calls are sequential per consumer.
/// </remarks>
public interface IFrameConsumer
{
    /// <summary>Name shown in statistics.</summary>
    string Name { get; }

    ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken);
}
