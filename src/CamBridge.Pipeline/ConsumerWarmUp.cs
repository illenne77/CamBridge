namespace CamBridge.Pipeline;

/// <summary>Outcome of one consumer's <see cref="IFrameConsumer.WarmUp"/> over all sample formats.</summary>
/// <param name="Duration">Time spent in the consumer, all samples together.</param>
/// <param name="Error">Message of the first failure, or null when every sample ran.</param>
public sealed record ConsumerWarmUp(string Consumer, TimeSpan Duration, string? Error = null);
