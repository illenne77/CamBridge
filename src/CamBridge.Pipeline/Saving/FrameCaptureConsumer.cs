using System.Globalization;
using CamBridge.Core;

namespace CamBridge.Pipeline.Saving;

/// <summary>
/// Saves one frame per request, for collecting sample images while watching the live view.
/// Between requests it declines every frame, so it holds no queue slot and no pooled buffer.
/// </summary>
/// <remarks>
/// PNG only: JPEG artifacts would shift the thresholds a recipe is tuned on. Requests run one at
/// a time, so a queue of one slot can never overflow and lose the requested frame.
/// </remarks>
public sealed class FrameCaptureConsumer(IFrameEncoder? encoder = null) : IFrameConsumer
{
    private const string FileNamePattern = "{timestamp:yyyyMMdd_HHmmss_fff}_{frameId:D8}";

    private Request? _current;

    public string Name => "FrameCapture";

    /// <summary>Saves the next frame into <paramref name="directory"/> and returns the file's path.</summary>
    /// <exception cref="InvalidOperationException">Another capture is still in progress.</exception>
    /// <exception cref="TimeoutException">No frame was saved in time, for example while waiting for a trigger.</exception>
    public async Task<string> CaptureNextAsync(string directory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var request = new Request(directory);
        if (Interlocked.CompareExchange(ref _current, request, null) is not null)
        {
            throw new InvalidOperationException("Another frame capture is still in progress.");
        }

        try
        {
            return await request.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"No frame was saved within {timeout.TotalSeconds:0.#} s."), ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, request);
        }
    }

    public bool Accepts(Frame frame) => Volatile.Read(ref _current) is { } request && request.TryClaim(frame.FrameId);

    public async ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Claimed by a request that timed out while the frame was queued; the next request wants a newer one.
        if (Volatile.Read(ref _current) is not { } request || !request.HasClaimed(frame.FrameId))
        {
            return;
        }

        try
        {
            var saver = new ImageSaveConsumer(new ImageSaveOptions { Directory = request.Directory, FileNamePattern = FileNamePattern }, encoder);
            await saver.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
            request.Completion.TrySetResult(saver.LastSavedPath!);
        }
        catch (Exception ex)
        {
            request.Completion.TrySetException(ex);
            throw; // the pipeline counts and logs it too
        }
    }

    private sealed class Request(string directory)
    {
        private const long Unclaimed = long.MinValue;
        private long _frameId = Unclaimed;

        public string Directory { get; } = directory;

        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryClaim(long frameId) => Interlocked.CompareExchange(ref _frameId, frameId, Unclaimed) == Unclaimed;

        public bool HasClaimed(long frameId) => Interlocked.Read(ref _frameId) == frameId;
    }
}
