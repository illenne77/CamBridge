using System.Globalization;

namespace CamBridge.Soak;

internal sealed record SoakSample(
    TimeSpan Elapsed,
    double WorkingSetMb,
    double PrivateMb,
    double RetainedManagedMb,
    double GcHeapMb,
    int Gen0,
    int Gen1,
    int Gen2,
    int Handles,
    int Threads,
    long OutstandingBuffers,
    long Received,
    double ReceivedFps,
    long CameraDropped,
    long TotalDropped,
    string Consumers,
    long Ok,
    long Ng,
    long SavedFiles)
{
    public const string CsvHeader =
        "ElapsedS,WorkingSetMB,PrivateMB,RetainedManagedMB,GcHeapMB,Gen0,Gen1,Gen2,Handles,Threads,OutstandingBuffers,Received,ReceivedFps,CameraDropped,TotalDropped,Consumers,Ok,Ng,SavedFiles";

    public string ToCsv() => string.Create(CultureInfo.InvariantCulture,
        $"{Elapsed.TotalSeconds:0},{WorkingSetMb:0.00},{PrivateMb:0.00},{RetainedManagedMb:0.000},{GcHeapMb:0.00},{Gen0},{Gen1},{Gen2},{Handles},{Threads},{OutstandingBuffers},{Received},{ReceivedFps:0.0},{CameraDropped},{TotalDropped},\"{Consumers}\",{Ok},{Ng},{SavedFiles}");
}

/// <summary>
/// Judges a run by growth rates after warm-up (least-squares slope per minute), not by
/// absolute values: caches, JIT and pools legitimately grow at first and must then level off.
/// </summary>
internal static class SoakReport
{
    /// <summary>
    /// Limits for a 30-minute run. A leak of one frame buffer per second, or of one handle per
    /// saved image, exceeds them many times over. Handle and thread counts wobble by a few as
    /// thread-pool threads come and go, which the limits tolerate.
    /// </summary>
    private const double MaxRetainedManagedMbPerMin = 0.1;
    private const double MaxPrivateMbPerMin = 1.0;
    private const double MaxHandlesPerMin = 5.0;
    private const double MaxThreadsPerMin = 0.5;

    public static bool Write(IReadOnlyList<SoakSample> samples, TimeSpan warmUp, long bufferBound, TextWriter output)
    {
        SoakSample[] steady = samples.Where(s => s.Elapsed >= warmUp).ToArray();
        if (steady.Length < 3)
        {
            output.WriteLine("Not enough samples after warm-up to judge the run.");
            return false;
        }

        SoakSample first = steady[0];
        SoakSample last = steady[^1];
        var checks = new List<(string Name, double Value, double Limit, string Unit)>
        {
            ("Retained managed heap", Slope(steady, s => s.RetainedManagedMb), MaxRetainedManagedMbPerMin, "MB/min"),
            ("Private bytes", Slope(steady, s => s.PrivateMb), MaxPrivateMbPerMin, "MB/min"),
            ("Handles", Slope(steady, s => s.Handles), MaxHandlesPerMin, "/min"),
            ("Threads", Slope(steady, s => s.Threads), MaxThreadsPerMin, "/min"),
        };

        output.WriteLine();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Steady state {first.Elapsed:hh\\:mm\\:ss} - {last.Elapsed:hh\\:mm\\:ss} ({steady.Length} samples)"));
        bool passed = true;
        foreach ((string name, double value, double limit, string unit) in checks)
        {
            bool ok = Math.Abs(value) <= limit;
            passed &= ok;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(ok ? "PASS" : "FAIL")}  {name,-22} {value,9:+0.000;-0.000} {unit} (limit {limit} {unit})"));
        }

        long maxOutstanding = steady.Max(s => s.OutstandingBuffers);
        bool buffersOk = maxOutstanding <= bufferBound;
        passed &= buffersOk;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {(buffersOk ? "PASS" : "FAIL")}  Outstanding buffers    max {maxOutstanding} (bound {bufferBound}: queues + live view slot + in flight)"));

        // The whole run counts here, warm-up included: a dropped frame is never acceptable. When they
        // happened tells a startup stall (first samples only) from ongoing loss on the link.
        bool noDrops = last.TotalDropped == 0;
        passed &= noDrops;
        string when = string.Empty;
        if (!noDrops)
        {
            SoakSample firstSeen = samples.First(s => s.TotalDropped > 0);
            SoakSample settled = samples.First(s => s.TotalDropped == last.TotalDropped);
            when = string.Create(CultureInfo.InvariantCulture,
                $", first seen at {firstSeen.Elapsed:hh\\:mm\\:ss}, no more after {settled.Elapsed:hh\\:mm\\:ss}");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {(noDrops ? "PASS" : "FAIL")}  Dropped frames         {last.TotalDropped} (camera side {last.CameraDropped}{when})"));

        if (last.Elapsed - first.Elapsed < TimeSpan.FromMinutes(10))
        {
            output.WriteLine("  note  Short steady window: the slopes above are mostly noise; judge memory on a run of 30 minutes.");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  info  Memory               working set {first.WorkingSetMb:0.0} -> {last.WorkingSetMb:0.0} MB, private {first.PrivateMb:0.0} -> {last.PrivateMb:0.0} MB, retained managed {first.RetainedManagedMb:0.00} -> {last.RetainedManagedMb:0.00} MB"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  info  GC                   gen0 {last.Gen0}, gen1 {last.Gen1}, gen2 {last.Gen2} (gen2 includes one forced collection per sample)"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  info  Frames               received {last.Received:N0}, camera dropped {last.CameraDropped}, total dropped {last.TotalDropped}, OK {last.Ok:N0} / NG {last.Ng:N0}, saved {last.SavedFiles:N0}"));
        output.WriteLine($"  info  Consumers            {last.Consumers}");
        return passed;
    }

    /// <summary>Least-squares slope of <paramref name="value"/> per minute of elapsed time.</summary>
    internal static double Slope(IReadOnlyList<SoakSample> samples, Func<SoakSample, double> value)
    {
        double n = samples.Count;
        double meanX = samples.Average(s => s.Elapsed.TotalMinutes);
        double meanY = samples.Average(value);
        double covariance = samples.Sum(s => (s.Elapsed.TotalMinutes - meanX) * (value(s) - meanY));
        double variance = samples.Sum(s => Math.Pow(s.Elapsed.TotalMinutes - meanX, 2));
        return variance == 0 || n < 2 ? 0 : covariance / variance;
    }
}
