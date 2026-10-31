namespace CamBridge.Pipeline;

/// <summary>What a consumer queue does when a frame arrives and the queue is full.</summary>
public enum QueueOverflowPolicy
{
    /// <summary>Discard the oldest queued frame. Keeps the consumer working on recent frames.</summary>
    DropOldest,

    /// <summary>Discard the incoming frame. Keeps the frames already queued.</summary>
    DropNewest,

    /// <summary>
    /// Block the camera thread up to <see cref="ConsumerOptions.WaitTimeout"/>, then drop the
    /// incoming frame. Pushes back-pressure into the SDK buffer pool; use only when frames are
    /// rate-limited (e.g. triggered) and none may be lost.
    /// </summary>
    Wait,
}
