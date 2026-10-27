namespace CamBridge.App.ViewModels;

/// <summary>
/// Applies values one at a time, keeping only the latest posted value while an apply is in
/// flight. Dragging a slider posts dozens of values per second; the camera sees one write per
/// round trip and always ends on the final value, instead of a growing backlog.
/// </summary>
/// <remarks>Not thread-safe: post from one thread (the UI thread).</remarks>
public sealed class CoalescingApplier<T>(Func<T, Task> apply)
{
    private T? _pending;
    private bool _hasPending;
    private Task _running = Task.CompletedTask;

    /// <summary>Completes when every posted value has been applied.</summary>
    public Task Idle => _running;

    /// <summary>True when a newer value is waiting; an apply can use it to skip follow-up work.</summary>
    public bool HasPending => _hasPending;

    public void Post(T value)
    {
        _pending = value;
        _hasPending = true;
        if (_running.IsCompleted)
        {
            _running = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        while (_hasPending)
        {
            T value = _pending!;
            _hasPending = false;
            await apply(value);
        }
    }
}
