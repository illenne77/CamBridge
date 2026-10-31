using System.Threading.Channels;
using CamBridge.Core;
using CamBridge.Pipeline.Statistics;
using Microsoft.Extensions.Logging;

namespace CamBridge.Pipeline;

/// <summary>Bounded queue and worker loop for one <see cref="IFrameConsumer"/>.</summary>
internal sealed partial class ConsumerRunner : IDisposable
{
    private readonly IFrameConsumer _consumer;
    private readonly ConsumerOptions _options;
    private readonly ILogger _logger;
    private readonly RateMeter _rate;
    private Channel<Frame> _channel;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _processed;
    private long _dropped;
    private long _faults;
    private string? _lastError;
    private volatile bool _accepting;

    public ConsumerRunner(IFrameConsumer consumer, ConsumerOptions options, TimeProvider timeProvider, ILogger logger)
    {
        _consumer = consumer;
        _options = options;
        _logger = logger;
        _rate = new RateMeter(timeProvider);
        _channel = CreateChannel();
    }

    public void Start()
    {
        _channel = CreateChannel();
        _rate.Reset();
        Interlocked.Exchange(ref _processed, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _faults, 0);
        Volatile.Write(ref _lastError, null);

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _loop = Task.Run(() => RunAsync(_channel.Reader, token), CancellationToken.None);
        _accepting = true;
    }

    public bool Accepts(Frame frame) => _consumer.Accepts(frame);

    /// <summary>Queues a frame, taking ownership of the handle. Called on the camera thread.</summary>
    public void Enqueue(Frame frame)
    {
        ChannelWriter<Frame> writer = _channel.Writer;
        if (writer.TryWrite(frame))
        {
            return;
        }

        if (_options.OverflowPolicy == QueueOverflowPolicy.Wait && WaitAndWrite(writer, frame))
        {
            return;
        }

        // Wait timed out, or the queue was completed by a concurrent stop (not a drop).
        frame.Dispose();
        if (_accepting)
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public async Task StopAsync(bool drain)
    {
        _accepting = false;
        _channel.Writer.TryComplete();
        if (_loop is null)
        {
            return;
        }

        if (!drain)
        {
            await _cts!.CancelAsync().ConfigureAwait(false);
        }

        await _loop.ConfigureAwait(false);

        // Frames left behind after a non-draining stop are discarded, not counted as drops.
        while (_channel.Reader.TryRead(out Frame? frame))
        {
            frame.Dispose();
        }

        _cts!.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => _cts?.Dispose();

    public ConsumerStatistics GetStatistics() => new(
        _consumer.Name,
        Interlocked.Read(ref _processed),
        _rate.GetRate(),
        Interlocked.Read(ref _dropped),
        _channel.Reader.Count,
        _options.Capacity,
        Interlocked.Read(ref _faults),
        Volatile.Read(ref _lastError));

    private Channel<Frame> CreateChannel()
    {
        var options = new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = _options.OverflowPolicy switch
            {
                QueueOverflowPolicy.DropOldest => BoundedChannelFullMode.DropOldest,
                QueueOverflowPolicy.DropNewest => BoundedChannelFullMode.DropWrite,
                _ => BoundedChannelFullMode.Wait,
            },
        };

        return Channel.CreateBounded<Frame>(options, OnItemDropped);
    }

    private void OnItemDropped(Frame frame)
    {
        frame.Dispose();
        Interlocked.Increment(ref _dropped);
    }

    private bool WaitAndWrite(ChannelWriter<Frame> writer, Frame frame)
    {
        long deadline = Environment.TickCount64 + (long)_options.WaitTimeout.TotalMilliseconds;
        while (true)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
                if (!writer.WaitToWriteAsync(timeout.Token).AsTask().GetAwaiter().GetResult())
                {
                    return false; // completed
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (writer.TryWrite(frame))
            {
                return true;
            }
        }
    }

    private async Task RunAsync(ChannelReader<Frame> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out Frame? frame))
                {
                    using (frame)
                    {
                        await ProcessOneAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Non-draining stop.
        }
    }

    private async Task ProcessOneAsync(Frame frame, CancellationToken cancellationToken)
    {
        try
        {
            await _consumer.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
            _rate.Record();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failing consumer must not stop the pipeline; the error is counted and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _faults);
            Volatile.Write(ref _lastError, ex.Message);
            LogConsumerFault(_logger, _consumer.Name, frame.FrameId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on frame {FrameId}")]
    private static partial void LogConsumerFault(ILogger logger, string consumer, long frameId, Exception exception);
}
