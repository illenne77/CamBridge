using System.Collections.Concurrent;
using CamBridge.Core;
using CamBridge.Sources.ImageFolder;
using CamBridge.Tests.Fakes;
using OpenCvSharp;

namespace CamBridge.Tests.Sources;

public sealed class ImageFolderCameraTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));
    private readonly TrackingArrayPool _pool = new();
    private readonly List<ImageFolderCamera> _cameras = [];

    public async ValueTask DisposeAsync()
    {
        foreach (ImageFolderCamera camera in _cameras)
        {
            await camera.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Creates a folder whose images are uniform gray levels 10, 20, 30, ... in file order.</summary>
    private string Folder(string name = "parts", int count = 3, int width = 16, int height = 8, bool color = false)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        for (int i = 0; i < count; i++)
        {
            using var image = new Mat(height, width, color ? MatType.CV_8UC3 : MatType.CV_8UC1, Scalar.All((i + 1) * 10));
            Cv2.ImEncode(".png", image, out byte[] png); // Cv2.ImWrite cannot reach every path the tests use
            File.WriteAllBytes(Path.Combine(folder, $"img{i:D2}.png"), png);
        }

        return folder;
    }

    private ImageFolderCamera Camera(string folder, ImageFolderOptions? options = null)
    {
        var camera = new ImageFolderCamera(ImageFolderProvider.CreateInfo(folder), options ?? new ImageFolderOptions { BufferPool = _pool });
        _cameras.Add(camera);
        return camera;
    }

    /// <summary>Collects (FrameId, first pixel) from received frames.</summary>
    private static ConcurrentQueue<(long Id, byte Value, PixelFormat Format)> Collect(ICamera camera)
    {
        var frames = new ConcurrentQueue<(long, byte, PixelFormat)>();
        camera.FrameReceived += (_, e) => frames.Enqueue((e.Frame.FrameId, e.Frame.Data[0], e.Frame.PixelFormat));
        return frames;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    [Fact]
    public async Task Provider_EnumeratesOnlyFoldersWithImages()
    {
        string withImages = Folder("a");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var provider = new ImageFolderProvider([withImages, Path.Combine(_root, "empty"), Path.Combine(_root, "missing")]);

        IReadOnlyList<CameraInfo> cameras = await provider.EnumerateAsync();

        CameraInfo info = Assert.Single(cameras);
        Assert.Equal(ImageFolderProvider.Id, info.ProviderId);
        Assert.Equal(Path.GetFullPath(withImages), info.DeviceId);
        Assert.Equal("a", info.UserDefinedName);
        Assert.Equal(CameraInterface.File, info.Interface);
        await using ICamera camera = provider.Create(info);
        Assert.IsType<ImageFolderCamera>(camera);
    }

    [Fact]
    public void Provider_RejectsForeignCameraInfo()
    {
        var provider = new ImageFolderProvider([]);

        Assert.Throws<ArgumentException>(() => provider.Create(new CameraInfo("pylon", "x", "Basler", "acA")));
    }

    [Fact]
    public async Task AddFolder_KeepsItThroughEnumerationAndCreatesIt()
    {
        string folder = Folder("added");
        var provider = new ImageFolderProvider([]);

        CameraInfo added = provider.AddFolder(folder);

        CameraInfo enumerated = Assert.Single(await provider.EnumerateAsync());
        Assert.Equal(added, enumerated);
        Assert.Equal(Path.GetFullPath(folder), added.DeviceId);
        await using ICamera camera = provider.Create(added);
        await camera.OpenAsync();
        Assert.Equal(CameraState.Open, camera.State);
    }

    [Fact]
    public async Task AddFolder_KnownFolder_KeepsOneEntry()
    {
        string folder = Folder("known");
        var provider = new ImageFolderProvider([folder]);

        CameraInfo sameCase = provider.AddFolder(folder);
        CameraInfo trailingSeparator = provider.AddFolder(folder + Path.DirectorySeparatorChar);

        CameraInfo enumerated = Assert.Single(await provider.EnumerateAsync());
        Assert.Equal(enumerated, sameCase);
        Assert.Equal(enumerated, trailingSeparator);
        if (OperatingSystem.IsWindows()) // the other spelling only exists on a case-insensitive file system
        {
            Assert.Equal(enumerated, provider.AddFolder(folder.Replace("known", "KNOWN", StringComparison.Ordinal)));
            Assert.Single(await provider.EnumerateAsync());
        }
    }

    [Fact]
    public async Task AddFolder_CannotPlay_ThrowsAndAddsNothing()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "notes.txt"), "not an image");
        var provider = new ImageFolderProvider([]);

        Assert.Throws<InvalidDataException>(() => provider.AddFolder(empty));
        Assert.Throws<DirectoryNotFoundException>(() => provider.AddFolder(Path.Combine(_root, "missing")));

        Assert.Empty(await provider.EnumerateAsync());
    }

    [Fact]
    public void CreateInfo_DriveRoot_IsNamedByItsPath()
    {
        string root = Path.GetPathRoot(_root)!;

        CameraInfo info = ImageFolderProvider.CreateInfo(root);

        Assert.Equal(root, info.DeviceId);
        Assert.Equal(root, info.UserDefinedName);
    }

    [Fact]
    public async Task Open_LoadsImagesAndReportsGeometry()
    {
        ImageFolderCamera camera = Camera(Folder(width: 20, height: 10));
        var states = new List<CameraState>();
        camera.StateChanged += (_, e) => states.Add(e.NewState);

        await camera.OpenAsync();

        Assert.Equal(CameraState.Open, camera.State);
        Assert.Equal(3, camera.ImageCount);
        Assert.Equal(20L, camera.GetParameter(ParameterNames.Width));
        Assert.Equal(10L, camera.GetParameter(ParameterNames.Height));
        Assert.Equal("Mono8", camera.GetParameter(ParameterNames.PixelFormat));
        Assert.Equal([CameraState.Open], states);
    }

    [Fact]
    public async Task Open_ColorImages_DefaultToBgr8()
    {
        ImageFolderCamera camera = Camera(Folder(color: true));

        await camera.OpenAsync();

        Assert.Equal("Bgr8", camera.GetParameter(ParameterNames.PixelFormat));
    }

    [Fact]
    public async Task Open_MixedSizes_Throws()
    {
        string folder = Folder(count: 2);
        using (var odd = new Mat(9, 9, MatType.CV_8UC1, Scalar.All(0)))
        {
            Cv2.ImWrite(Path.Combine(folder, "img99.png"), odd);
        }

        ImageFolderCamera camera = Camera(folder);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => camera.OpenAsync());
        Assert.Contains("img99.png", error.Message, StringComparison.Ordinal);
        Assert.Equal(CameraState.Closed, camera.State);
    }

    [Theory]
    [InlineData("한글")]
    [InlineData("tiếng-việt")] // outside CP949
    [InlineData("emoji-😀")] // outside every ANSI code page
    public async Task Open_FolderNameInAnyScript_LoadsImages(string name)
    {
        ImageFolderCamera camera = Camera(Folder(name));

        await camera.OpenAsync();

        Assert.Equal(3, camera.ImageCount);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public async Task Open_UndecodableFile_ThrowsNamingIt(byte[] content)
    {
        string folder = Folder(count: 1);
        File.WriteAllBytes(Path.Combine(folder, "img99.png"), content);
        ImageFolderCamera camera = Camera(folder);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => camera.OpenAsync());

        Assert.Contains("img99.png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_OverMemoryLimit_Throws()
    {
        ImageFolderCamera camera = Camera(Folder(), new ImageFolderOptions { MaxMemoryBytes = 200 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.OpenAsync());
    }

    [Fact]
    public async Task FreeRun_EmitsImagesInOrderAndLoops()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();
        camera.SetParameter(ParameterNames.AcquisitionFrameRate, 500.0);

        await camera.StartAcquisitionAsync();
        await WaitForAsync(() => frames.Count >= 7);
        await camera.StopAcquisitionAsync();

        (long Id, byte Value, PixelFormat _)[] received = frames.Take(7).ToArray();
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], received.Select(f => f.Id));
        Assert.Equal(new byte[] { 10, 20, 30, 10, 20, 30, 10 }, received.Select(f => f.Value));
        Assert.Equal(CameraState.Open, camera.State);
    }

    [Fact]
    public async Task FreeRun_FollowsFrameRate()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();
        camera.SetParameter(ParameterNames.AcquisitionFrameRate, 50.0);

        await camera.StartAcquisitionAsync();
        await Task.Delay(1000);
        await camera.StopAcquisitionAsync();

        // 50 fps for 1 s; generous bounds for loaded CI machines.
        Assert.InRange(frames.Count, 35, 56);
    }

    [Fact]
    public async Task SoftwareTrigger_EmitsOneImagePerTrigger()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();
        camera.TriggerMode = TriggerMode.Software;
        await camera.StartAcquisitionAsync();

        await Task.Delay(150);
        Assert.Empty(frames);

        camera.ExecuteSoftwareTrigger();
        camera.ExecuteCommand(ParameterNames.TriggerSoftware);
        await WaitForAsync(() => frames.Count == 2);
        await Task.Delay(100);
        await camera.StopAcquisitionAsync();

        Assert.Equal([(1L, (byte)10), (2L, (byte)20)], frames.Select(f => (f.Id, f.Value)));
    }

    [Fact]
    public async Task SoftwareTrigger_InFreeRun_Throws()
    {
        ImageFolderCamera camera = Camera(Folder());
        await camera.OpenAsync();
        await camera.StartAcquisitionAsync();

        Assert.Throws<InvalidOperationException>(camera.ExecuteSoftwareTrigger);
    }

    [Fact]
    public async Task LoopOff_StopsAtLastImageAndReportsOnce()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        var errors = new ConcurrentQueue<CameraErrorEventArgs>();
        camera.ErrorOccurred += (_, e) => errors.Enqueue(e);
        await camera.OpenAsync();
        camera.SetParameter(ImageFolderCamera.LoopParameter, false);
        camera.SetParameter(ParameterNames.AcquisitionFrameRate, 500.0);

        await camera.StartAcquisitionAsync();
        await WaitForAsync(() => !errors.IsEmpty);
        await Task.Delay(50);
        await camera.StopAcquisitionAsync();

        Assert.Equal(3, frames.Count);
        Assert.Single(errors);
    }

    [Fact]
    public async Task ExposureAndGain_ScalePixelValues()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();
        camera.TriggerMode = TriggerMode.Software;
        camera.ExposureTimeUs = 20_000;          // x2
        camera.Gain = 20 * Math.Log10(3);        // x3
        await camera.StartAcquisitionAsync();

        camera.ExecuteSoftwareTrigger();          // 10 -> 60
        await WaitForAsync(() => frames.Count == 1);
        camera.Gain = 0;
        camera.ExposureTimeUs = 1_000_000;        // x100, saturates
        camera.ExecuteSoftwareTrigger();          // 20 -> 255
        await WaitForAsync(() => frames.Count == 2);
        await camera.StopAcquisitionAsync();

        Assert.Equal(new byte[] { 60, 255 }, frames.Select(f => f.Value));
    }

    [Fact]
    public async Task PixelFormat_CanSwitchWhileOpen_NotWhileAcquiring()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();

        camera.SetParameter(ParameterNames.PixelFormat, "Bgr8");
        camera.TriggerMode = TriggerMode.Software;
        await camera.StartAcquisitionAsync();
        camera.ExecuteSoftwareTrigger();
        await WaitForAsync(() => frames.Count == 1);

        Assert.Equal(PixelFormat.Bgr8, frames.Single().Format);
        Assert.Throws<InvalidOperationException>(() => camera.SetParameter(ParameterNames.PixelFormat, "Mono8"));
        Assert.False(camera.FindParameter(ParameterNames.PixelFormat)!.IsWritable);
    }

    [Fact]
    public async Task Parameters_ValidateNamesRangesAndValues()
    {
        ImageFolderCamera camera = Camera(Folder());

        Assert.Throws<KeyNotFoundException>(() => camera.GetParameter("NoSuchFeature"));
        Assert.Throws<InvalidOperationException>(() => camera.GetParameter(ParameterNames.Width)); // not open yet
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.ExposureTimeUs = 5);
        Assert.Throws<ArgumentException>(() => camera.TriggerMode = TriggerMode.Hardware);

        await camera.OpenAsync();
        Assert.Throws<InvalidOperationException>(() => camera.SetParameter(ParameterNames.Width, 10L));

        camera.SetParameter(ParameterNames.Gain, 6L); // integer accepted for a float feature
        Assert.Equal(6.0, camera.Gain);
        Assert.Equal(
            ["ExposureTime", "Gain", "TriggerMode", "TriggerSoftware", "AcquisitionFrameRate", "PixelFormat", "Width", "Height", "Loop"],
            camera.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public async Task Restart_ResetsFrameIdAndReturnsAllBuffers()
    {
        ImageFolderCamera camera = Camera(Folder());
        var frames = Collect(camera);
        await camera.OpenAsync();
        camera.SetParameter(ParameterNames.AcquisitionFrameRate, 500.0);

        await camera.StartAcquisitionAsync();
        await WaitForAsync(() => frames.Count >= 2);
        await camera.StopAcquisitionAsync();
        int firstRun = frames.Count;

        await camera.StartAcquisitionAsync();
        await WaitForAsync(() => frames.Count >= firstRun + 1);
        await camera.CloseAsync();

        Assert.Equal(1, frames.ElementAt(firstRun).Id);
        Assert.Equal(CameraState.Closed, camera.State);
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public async Task DisposeWhileAcquiring_StopsThread()
    {
        ImageFolderCamera camera = Camera(Folder());
        await camera.OpenAsync();
        await camera.StartAcquisitionAsync();

        await camera.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        _cameras.Remove(camera);

        Assert.Equal(CameraState.Closed, camera.State);
        Assert.Equal(0, _pool.Outstanding);
    }
}
