using System.Collections.Concurrent;
using CamBridge.Core;
using CamBridge.Pipeline;

namespace CamBridge.Tests.Fakes;

/// <summary>Records processed frame ids; optionally waits on a gate before each frame.</summary>
internal sealed class RecordingConsumer(string name = "Recorder") : IFrameConsumer
{
    private readonly ConcurrentQueue<long> _ids = new();

    public string Name { get; } = name;

    public SemaphoreSlim? Gate { get; init; }

    /// <summary>Completed when the first frame enters <see cref="ProcessAsync"/>.</summary>
    public TaskCompletionSource FirstFrameStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<Frame, Task>? OnFrame { get; init; }

    public Func<Frame, bool>? Filter { get; init; }

    public IReadOnlyList<long> Ids => _ids.ToArray();

    public bool Accepts(Frame frame) => Filter?.Invoke(frame) ?? true;

    public async ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
    {
        FirstFrameStarted.TrySetResult();
        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        if (OnFrame is not null)
        {
            await OnFrame(frame);
        }

        _ids.Enqueue(frame.FrameId);
    }
}
