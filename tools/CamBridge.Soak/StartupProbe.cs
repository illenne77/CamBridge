using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CamBridge.Core;
using CamBridge.Pipeline.Statistics;

namespace CamBridge.Soak;

/// <summary>
/// Records what the process loads around the start of acquisition: managed assemblies with the
/// thread that loaded them, and native modules. Anything loaded on the camera or a consumer thread
/// after frames start is a stall the queues have to absorb, and on a fresh build it also waits for
/// the antivirus scan of the new file.
/// </summary>
internal sealed class StartupProbe : IDisposable
{
    private readonly ICamera _camera;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Load> _loads = new();
    private HashSet<string> _modulesBeforeWarmUp = [];
    private HashSet<string> _modulesBeforeStart = [];
    private HashSet<string> _modulesAfterStart = [];
    private TimeSpan _runningSince = TimeSpan.MaxValue;
    private int _cameraThreadId;
    private volatile string _phase = "setup";

    public StartupProbe(ICamera camera)
    {
        _camera = camera;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        camera.FrameReceived += OnFrameReceived;
    }

    public void BeginWarmUp()
    {
        _modulesBeforeWarmUp = NativeModules();
        _phase = "warm-up";
    }

    public void BeforeAcquisitionStart()
    {
        _modulesBeforeStart = NativeModules();
        if (_modulesBeforeWarmUp.Count == 0)
        {
            _modulesBeforeWarmUp = _modulesBeforeStart; // no warm-up ran
        }

        _phase = "acquisition start";
    }

    public void AfterAcquisitionStart()
    {
        _modulesAfterStart = NativeModules();
        _runningSince = _clock.Elapsed;
        _phase = "running";
    }

    /// <summary>Lines for the console and the summary, taken <paramref name="window"/> after the start.</summary>
    public string Report(PipelineStatistics stats, TimeSpan window)
    {
        Load[] loads = _loads.ToArray();
        int cameraThread = Volatile.Read(ref _cameraThreadId);
        var text = new StringBuilder();
        void Line(string line) => text.Append("  info  ").AppendLine(line);

        string consumers = string.Join(", ", stats.Consumers.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.Name} drop {c.DroppedFrames} peak {c.PeakQueueLength}/{c.QueueCapacity} max {c.MaxProcessingTime.TotalMilliseconds:0}ms")));
        Line(string.Create(CultureInfo.InvariantCulture,
            $"Startup {window.TotalSeconds:0} s     received {stats.ReceivedFrames:N0}, camera dropped {stats.CameraDroppedFrames}, {consumers}"));
        Line($"Warm-up loads        {Names(loads.Where(l => l.Phase == "warm-up"))}; native {List(_modulesBeforeStart.Except(_modulesBeforeWarmUp))}");
        Line($"Start loads          {Names(loads.Where(l => l.Phase == "acquisition start"))}; native {List(_modulesAfterStart.Except(_modulesBeforeStart))}");

        Load[] late = loads.Where(l => l.Phase == "running").ToArray();
        Line(late.Length == 0
            ? "Late loads           none"
            : "Late loads           " + string.Join(", ", late.Select(l => string.Create(CultureInfo.InvariantCulture,
                $"{l.Name} +{(l.At - _runningSince).TotalMilliseconds:0}ms on {ThreadLabel(l, cameraThread)}"))));
        Line($"Late native modules  {List(NativeModules().Except(_modulesAfterStart))}");
        return text.ToString();
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        _camera.FrameReceived -= OnFrameReceived;
    }

    private static HashSet<string> NativeModules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Names(IEnumerable<Load> loads) => List(loads.Select(l => l.Name));

    private static string List(IEnumerable<string> names)
    {
        string[] sorted = names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return sorted.Length == 0 ? "none" : $"{sorted.Length} ({string.Join(", ", sorted)})";
    }

    private static string ThreadLabel(Load load, int cameraThread) =>
        load.ThreadId == cameraThread ? "camera thread"
        : load.IsPoolThread ? $"pool thread {load.ThreadId}"
        : $"thread {load.ThreadId}";

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e) =>
        _loads.Enqueue(new Load(
            _phase,
            _clock.Elapsed,
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.IsThreadPoolThread,
            e.LoadedAssembly.GetName().Name ?? "?"));

    private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        if (Volatile.Read(ref _cameraThreadId) == 0)
        {
            Interlocked.CompareExchange(ref _cameraThreadId, Environment.CurrentManagedThreadId, 0);
        }
    }

    private sealed record Load(string Phase, TimeSpan At, int ThreadId, bool IsPoolThread, string Name);
}
