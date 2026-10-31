using System.Collections.Concurrent;

namespace CamBridge.Sources.Webcam;

/// <summary>
/// A dedicated thread that owns a device which is not thread-safe (OpenCV VideoCapture and the
/// DirectShow filter). Other threads run work on it through <see cref="Invoke{T}"/>; while a
/// <see cref="Pump"/> is installed the thread calls it repeatedly (one frame per call) and runs
/// queued commands between calls, so a parameter change waits for at most one frame.
/// </summary>
internal sealed class DeviceThread : IDisposable
{
    private readonly BlockingCollection<Action> _commands = [];
    private readonly Thread _thread;
    private readonly TimeSpan _commandTimeout;
    private Action? _pump;
    private int _disposed;

    public DeviceThread(string name, TimeSpan commandTimeout)
    {
        _commandTimeout = commandTimeout;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>
    /// Work repeated while no command is pending (e.g. grabbing a frame). Set and clear it only
    /// on the device thread. Exceptions escaping it end the pump and are passed to <see cref="PumpFailed"/>.
    /// </summary>
    public Action? Pump
    {
        get => _pump;
        set
        {
            if (!IsCurrentThread)
            {
                throw new InvalidOperationException("Pump can only be changed on the device thread.");
            }

            _pump = value;
        }
    }

    /// <summary>Raised on the device thread when the pump throws; the pump has already been removed.</summary>
    public event Action<Exception>? PumpFailed;

    /// <summary>
    /// Runs <paramref name="work"/> on the device thread and returns its result, rethrowing its
    /// exception. Runs inline when already on the device thread (e.g. from a frame handler).
    /// </summary>
    public T Invoke<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsCurrentThread)
        {
            return work();
        }

        Task<T> task = InvokeAsync(work);
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(_commandTimeout))
        {
            throw new TimeoutException($"The device thread did not respond within {_commandTimeout.TotalSeconds:0.#} s.");
        }

        return task.GetAwaiter().GetResult();
    }

    public void Invoke(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Invoke(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>Queues <paramref name="work"/> without blocking the caller.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _commands.Add(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
#pragma warning disable CA1031 // The exception is handed to the caller through the task.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    completion.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(DeviceThread));
        }

        return completion.Task;
    }

    public Task InvokeAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>
    /// Stops accepting commands, runs those already queued, and waits for the thread to exit.
    /// Called on the device thread itself, it only requests the exit.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.CompleteAdding();
        if (!IsCurrentThread)
        {
            _thread.Join();
            _commands.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            while (_commands.TryTake(out Action? command))
            {
                command();
            }

            Action? pump = _pump;
            if (pump is null || _commands.IsAddingCompleted)
            {
                // Idle: block until the next command, or exit once disposed and drained.
                if (!_commands.TryTake(out Action? next, Timeout.Infinite))
                {
                    return;
                }

                next();
                continue;
            }

            try
            {
                pump();
            }
#pragma warning disable CA1031 // Reported through PumpFailed; the thread keeps serving commands.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _pump = null;
                PumpFailed?.Invoke(ex);
            }
        }
    }
}
