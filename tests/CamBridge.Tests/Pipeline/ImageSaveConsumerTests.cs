using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Saving;
using CamBridge.Pipeline.Statistics;
using CamBridge.Tests.Fakes;
using OpenCvSharp;

namespace CamBridge.Tests.Pipeline;

public sealed class ImageSaveConsumerTests : IDisposable
{
    private static readonly DateTime Timestamp = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Frame GradientFrame(PixelFormat format, int width, int height, int stride = 0, long id = 1)
    {
        Frame frame = Frame.Rent(width, height, format, id, Timestamp, stride: stride);
        Span<byte> data = frame.GetWritableData();
        int bpp = format.BytesPerPixel();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width * bpp; x++)
            {
                data[(y * frame.Stride) + x] = (byte)((x * 7) + (y * 13));
            }
        }

        return frame;
    }

    private ImageSaveConsumer Consumer(ImageFileFormat format = ImageFileFormat.Png, string pattern = "{frameId:D4}", int everyNth = 1) =>
        new(new ImageSaveOptions { Directory = _root, Format = format, FileNamePattern = pattern, SaveEveryNth = everyNth });

    [Theory]
    [InlineData(PixelFormat.Mono8, 0)]
    [InlineData(PixelFormat.Bgr8, 0)]
    [InlineData(PixelFormat.Bgr8, 64)] // padded rows
    public async Task Png_RoundTripsPixelsExactly(PixelFormat format, int stride)
    {
        using Frame frame = GradientFrame(format, 17, 9, stride);
        ImageSaveConsumer consumer = Consumer();

        await consumer.ProcessAsync(frame, CancellationToken.None);

        using Mat decoded = Cv2.ImRead(consumer.LastSavedPath!, ImreadModes.Unchanged);
        Assert.Equal(17, decoded.Width);
        Assert.Equal(9, decoded.Height);
        Assert.Equal(format.BytesPerPixel(), decoded.Channels());
        for (int y = 0; y < frame.Height; y++)
        {
            byte[] row = new byte[frame.Width * format.BytesPerPixel()];
            System.Runtime.InteropServices.Marshal.Copy(decoded.Ptr(y), row, 0, row.Length);
            Assert.Equal(frame.Data.Slice(y * frame.Stride, row.Length).ToArray(), row);
        }
    }

    [Theory]
    [InlineData(ImageFileFormat.Jpeg, ".jpg")]
    [InlineData(ImageFileFormat.Bmp, ".bmp")]
    public async Task OtherFormats_WriteDecodableFiles(ImageFileFormat format, string extension)
    {
        using Frame frame = GradientFrame(PixelFormat.Bgr8, 32, 16);
        ImageSaveConsumer consumer = Consumer(format);

        await consumer.ProcessAsync(frame, CancellationToken.None);

        Assert.EndsWith(extension, consumer.LastSavedPath, StringComparison.Ordinal);
        using Mat decoded = Cv2.ImRead(consumer.LastSavedPath!);
        Assert.Equal(new Size(32, 16), decoded.Size());
    }

    [Fact]
    public async Task SaveEveryNth_SkipsFrames()
    {
        ImageSaveConsumer consumer = Consumer(everyNth: 3);

        for (int id = 1; id <= 7; id++)
        {
            using Frame frame = GradientFrame(PixelFormat.Mono8, 4, 4, id: id);
            if (consumer.Accepts(frame))
            {
                await consumer.ProcessAsync(frame, CancellationToken.None);
            }
        }

        string[] files = Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["0001.png", "0004.png", "0007.png"], files);
    }

    [Fact]
    public async Task SaveEveryNth_SlowStorage_DoesNotDropSkippedFrames()
    {
        // The first save stalls for the whole burst; frames it would skip must not overflow the queue behind it.
        using var release = new ManualResetEventSlim();
        var consumer = new ImageSaveConsumer(
            new ImageSaveOptions { Directory = _root, FileNamePattern = "{frameId:D4}", SaveEveryNth = 100 },
            new GatedEncoder(release));
        var camera = new FakeCamera(new CameraInfo("fake", "1", "Fake", "Model"));
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 4, OverflowPolicy = QueueOverflowPolicy.DropOldest });
        pipeline.Start();

        for (int id = 1; id <= 300; id++)
        {
            camera.Emit(GradientFrame(PixelFormat.Mono8, 4, 4, id: id));
        }

        ConsumerStatistics stalled = pipeline.GetStatistics().Consumers[0];
        Assert.Equal(0, stalled.DroppedFrames);

        release.Set();
        await pipeline.StopAsync(drain: true);

        ConsumerStatistics stats = pipeline.GetStatistics().Consumers[0];
        Assert.Equal(0, stats.DroppedFrames);
        Assert.Equal(3, stats.ProcessedFrames);
        string[] files = Directory.GetFiles(_root).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["0001.png", "0101.png", "0201.png"], files);
    }

    private sealed class GatedEncoder(ManualResetEventSlim release) : IFrameEncoder
    {
        public byte[] Encode(Frame frame, ImageFileFormat format, int jpegQuality)
        {
            release.Wait();
            return [1];
        }
    }

    [Fact]
    public void BuildPath_ExpandsTokens()
    {
        var consumer = new ImageSaveConsumer(new ImageSaveOptions
        {
            Directory = _root,
            CameraName = "cam0",
            FileNamePattern = "{camera}/{utc:yyyyMMdd}/{utc:HHmmss_fff}_{frameId:D6}_{width}x{height}",
            Format = ImageFileFormat.Bmp,
        });
        using Frame frame = GradientFrame(PixelFormat.Mono8, 20, 10, id: 42);

        string path = consumer.BuildPath(frame);

        Assert.Equal(Path.Combine(_root, "cam0", "20260102", "030405_678_000042_20x10.bmp"), path);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/{frameId}")]
    public void BuildPath_RejectsPatternsOutsideRoot(string pattern)
    {
        using Frame frame = GradientFrame(PixelFormat.Mono8, 4, 4);

        Assert.Throws<InvalidOperationException>(() => Consumer(pattern: pattern).BuildPath(frame));
    }

    [Fact]
    public void BuildPath_RejectsUnknownToken()
    {
        using Frame frame = GradientFrame(PixelFormat.Mono8, 4, 4);

        Assert.Throws<FormatException>(() => Consumer(pattern: "{nope}").BuildPath(frame));
    }
}
