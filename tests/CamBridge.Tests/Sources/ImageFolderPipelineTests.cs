using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Statistics;
using CamBridge.SampleGenerator;
using CamBridge.Sources.ImageFolder;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Sources;

/// <summary>
/// The committed demo set (samples/part-inspection) played through the image folder camera,
/// the acquisition pipeline and the inspection consumer: the same chain the app runs.
/// </summary>
public class ImageFolderPipelineTests
{
    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CamBridge.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (CamBridge.sln) not found.");
    }

    [Fact]
    public async Task CommittedDemoSamples_GiveVerdictsMatchingFileNames()
    {
        string sampleDir = Path.Combine(RepositoryRoot(), "samples", "part-inspection");
        string imagesDir = Path.Combine(sampleDir, DemoSampleSet.ImagesFolder);
        string[] names = Directory.GetFiles(imagesDir, "*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(20, names.Length);

        var pool = new TrackingArrayPool();
        await using var camera = new ImageFolderCamera(ImageFolderProvider.CreateInfo(imagesDir), new ImageFolderOptions { BufferPool = pool });
        using InspectionEngine engine = InspectionEngine.Load(Path.Combine(sampleDir, DemoSampleSet.RecipeFile));
        var consumer = new InspectionConsumer(engine);
        var results = new List<InspectionResult>();
        consumer.ResultProduced += (_, e) => results.Add(e.Result);

        await camera.OpenAsync();
        camera.TriggerMode = TriggerMode.Software;
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = names.Length, OverflowPolicy = QueueOverflowPolicy.Wait });
        pipeline.Start();
        await camera.StartAcquisitionAsync();

        for (int i = 0; i < names.Length; i++)
        {
            camera.ExecuteSoftwareTrigger();
        }

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (pipeline.GetStatistics().ReceivedFrames < names.Length)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        await camera.StopAcquisitionAsync();
        await pipeline.StopAsync(drain: true);
        pipeline.LiveView.TryTake()?.Dispose();

        Assert.Equal(names.Length, results.Count);
        for (int i = 0; i < names.Length; i++)
        {
            Verdict expected = names[i].Contains("_ng", StringComparison.Ordinal) ? Verdict.Ng : Verdict.Ok;
            Assert.True(expected == results[i].Verdict,
                $"{names[i]}: expected {expected}, got {results[i].Verdict} ({string.Join("; ", results[i].Reasons)})");
        }

        PipelineStatistics stats = pipeline.GetStatistics();
        Assert.Equal(0, stats.TotalDroppedFrames);
        Assert.Equal(15, consumer.OkCount);
        Assert.Equal(5, consumer.NgCount);
        Assert.Equal(0, pool.Outstanding);
    }
}
