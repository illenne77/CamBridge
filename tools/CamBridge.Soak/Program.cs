using System.Diagnostics;
using System.Globalization;
using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Saving;
using CamBridge.Pipeline.Statistics;
using CamBridge.Soak;
using CamBridge.Sources.ImageFolder;
using CamBridge.Sources.Pylon;

// Usage:
//   dotnet run -c Release --project tools/CamBridge.Soak -- [--source pylon|folder] [--minutes 30]
//       [--fps 120] [--width 640] [--height 480] [--save-every 100] [--interval 10] [--out <dir>] [--keep-images]
// pylon uses the first emulated camera (PYLON_CAMEMU is set to 2 for this process when missing).
Dictionary<string, string> options = ParseArgs(args);
string source = options.GetValueOrDefault("source", "pylon");
TimeSpan duration = TimeSpan.FromMinutes(Number("minutes", 30));
TimeSpan interval = TimeSpan.FromSeconds(Number("interval", 10));
double fps = Number("fps", 120);
int saveEvery = (int)Number("save-every", 100);
string outDir = Path.GetFullPath(options.GetValueOrDefault("out", Path.Combine("artifacts", "soak", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
string imageDir = Path.Combine(outDir, "images");
TimeSpan warmUp = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, duration.Ticks / 5));
Directory.CreateDirectory(outDir);

var pool = new CountingArrayPool();
ICamera camera = await CreateCameraAsync();
long cameraErrors = 0;
camera.ErrorOccurred += (_, e) =>
{
    if (Interlocked.Increment(ref cameraErrors) <= 5)
    {
        Console.WriteLine($"  camera error {e.Kind}: {e.Message}");
    }
};

await camera.OpenAsync();
Configure(camera);
string recipePath = FindUpwards(Path.Combine("samples", "part-inspection", "part.recipe.json"))
    ?? throw new FileNotFoundException("Sample recipe not found; run from the repository.");

using InspectionEngine engine = InspectionEngine.Load(recipePath);
using var inspection = new InspectionConsumer(engine);
var saving = new ImageSaveConsumer(new ImageSaveOptions
{
    Directory = imageDir,
    CameraName = "soak",
    SaveEveryNth = saveEvery,
    FileNamePattern = "{camera}/{frameId:D10}",
});
var inspectionQueue = new ConsumerOptions { Capacity = 2, OverflowPolicy = QueueOverflowPolicy.DropOldest };
var saveQueue = new ConsumerOptions { Capacity = 4, OverflowPolicy = QueueOverflowPolicy.DropOldest };
// Each queue plus its frame in progress, the live view slot and one frame in the camera callback.
long bufferBound = inspectionQueue.Capacity + 1 + saveQueue.Capacity + 1 + 1 + 1;

var pipeline = new AcquisitionPipeline(camera);
pipeline.AddConsumer(inspection, inspectionQueue);
pipeline.AddConsumer(saving, saveQueue);

Console.WriteLine($"Soak: {camera.Info.DisplayName}, {camera.GetParameter(ParameterNames.Width)}x{camera.GetParameter(ParameterNames.Height)} " +
                  $"{camera.GetParameter(ParameterNames.PixelFormat)} @ {fps} fps for {duration.TotalMinutes} min, save 1/{saveEvery}, warm-up {warmUp.TotalMinutes:0.#} min");
Console.WriteLine($"Output: {outDir}");

using var csv = new StreamWriter(Path.Combine(outDir, "samples.csv"));
await csv.WriteLineAsync(SoakSample.CsvHeader);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

pipeline.Start();
await camera.StartAcquisitionAsync();
var clock = Stopwatch.StartNew();

// Stand-in for the UI: take the latest frame at display rate, as the WPF live view does.
Task display = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
    while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
    {
        pipeline.LiveView.TryTake()?.Dispose();
    }
}, stop.Token);

var samples = new List<SoakSample>();
using var process = Process.GetCurrentProcess();
try
{
    while (clock.Elapsed < duration && !stop.IsCancellationRequested)
    {
        await Task.Delay(interval, stop.Token);
        SoakSample sample = Sample(clock.Elapsed, pipeline.GetStatistics());
        samples.Add(sample);
        await csv.WriteLineAsync(sample.ToCsv());
        await csv.FlushAsync();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{sample.Elapsed:hh\\:mm\\:ss}  {sample.ReceivedFps,6:0.0} fps  drop {sample.TotalDropped,4}  ws {sample.WorkingSetMb,7:0.0} MB  priv {sample.PrivateMb,7:0.0} MB  " +
            $"managed {sample.RetainedManagedMb,6:0.00} MB  handles {sample.Handles,4}  threads {sample.Threads,3}  buffers {sample.OutstandingBuffers,2}  gen2 {sample.Gen2}"));
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped early.");
}

stop.Cancel();
await camera.StopAcquisitionAsync();
await pipeline.StopAsync(drain: false);
try
{
    await display;
}
catch (OperationCanceledException)
{
}

await camera.CloseAsync();
await pipeline.DisposeAsync(); // releases the frame held by the live view slot
await camera.DisposeAsync();

using (var summary = new StringWriter(CultureInfo.InvariantCulture))
{
    bool passed = SoakReport.Write(samples, warmUp, bufferBound, summary);
    summary.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  Camera errors        {cameraErrors}"));
    summary.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  info  Buffers after stop   {pool.Outstanding} outstanding of {pool.Rented:N0} rented"));
    Console.Write(summary);
    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary.ToString());

    if (!options.ContainsKey("keep-images") && Directory.Exists(imageDir))
    {
        Directory.Delete(imageDir, recursive: true);
    }

    return passed ? 0 : 1;
}

SoakSample Sample(TimeSpan elapsed, PipelineStatistics stats)
{
    process.Refresh();
    // Forced full collection: what is still referenced, without the noise of uncollected garbage.
    double retained = GC.GetTotalMemory(forceFullCollection: true) / 1048576.0;
    ConsumerStatistics? save = stats.Consumers.FirstOrDefault(c => c.Name == saving.Name);
    return new SoakSample(
        elapsed,
        process.WorkingSet64 / 1048576.0,
        process.PrivateMemorySize64 / 1048576.0,
        retained,
        GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0,
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        process.HandleCount,
        process.Threads.Count,
        pool.Outstanding,
        stats.ReceivedFrames,
        stats.ReceivedFps,
        stats.CameraDroppedFrames,
        stats.TotalDroppedFrames,
        string.Join("; ", stats.Consumers.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.Name} {c.ProcessedFps:0.0}fps q{c.QueueLength}/{c.QueueCapacity} drop {c.DroppedFrames} err {c.Faults}"))),
        inspection.OkCount,
        inspection.NgCount,
        save is null ? 0 : (save.ProcessedFrames + saveEvery - 1) / saveEvery);
}

async Task<ICamera> CreateCameraAsync()
{
    if (source == "folder")
    {
        string folder = FindUpwards(Path.Combine("samples", "part-inspection", "images")) ?? throw new DirectoryNotFoundException("Sample images not found.");
        return new ImageFolderCamera(ImageFolderProvider.CreateInfo(folder), new ImageFolderOptions { BufferPool = pool });
    }

    if (Environment.GetEnvironmentVariable("PYLON_CAMEMU") is null)
    {
        Environment.SetEnvironmentVariable("PYLON_CAMEMU", "2");
    }

    ICameraProvider provider = PylonProviderFactory.TryCreate(new PylonOptions { BufferPool = pool }, out string? reason)
        ?? throw new InvalidOperationException($"pylon is not available: {reason}");
    IReadOnlyList<CameraInfo> cameras = await provider.EnumerateAsync();
    CameraInfo info = cameras.FirstOrDefault(c => c.Interface == CameraInterface.Emulated)
        ?? (cameras.Count > 0 ? cameras[0] : throw new InvalidOperationException("No pylon camera found."));
    return provider.Create(info);
}

void Configure(ICamera camera)
{
    if (source != "folder")
    {
        camera.SetParameter(ParameterNames.PixelFormat, "Mono8");
        camera.SetParameter(ParameterNames.Width, (long)Number("width", 640));
        camera.SetParameter(ParameterNames.Height, (long)Number("height", 480));
        camera.ExposureTimeUs = 1000;
    }

    camera.TriggerMode = TriggerMode.Off;
    camera.SetParameter(ParameterNames.AcquisitionFrameRate, fps);
}

double Number(string name, double fallback) =>
    options.TryGetValue(name, out string? text) ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            string key = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result[key] = hasValue ? args[++i] : "true";
        }
    }

    return result;
}

static string? FindUpwards(string relativePath)
{
    for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, relativePath);
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}
