using System.IO;
using System.Windows;
using System.Windows.Threading;
using CamBridge.App.Logging;
using CamBridge.App.ViewModels;
using CamBridge.Core;
using CamBridge.Sources.ImageFolder;
using CamBridge.Sources.Pylon;
using CamBridge.Sources.Webcam;
using Microsoft.Extensions.Logging;

namespace CamBridge.App;

/// <summary>Composition root: logging, camera providers and the main window.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF never disposes the Application; owned resources are released in OnExit.")]
public partial class App : Application
{
    private const string SampleImagesFolder = "samples/part-inspection/images";
    private const string SampleRecipe = "samples/part-inspection/part.recipe.json";

    private FileLoggerProvider? _fileLog;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var memoryLog = new MemoryLogProvider();
        _fileLog = new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs"));
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ")
            .AddProvider(_fileLog)
            .AddProvider(memoryLog));
        _logger = _loggerFactory.CreateLogger<App>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        string? samples = FindUpwards(SampleImagesFolder);
        var imageFolders = new ImageFolderProvider(samples is null ? [] : [samples]);
        var registry = new CameraRegistry(CreateProviders(imageFolders));
        var viewModel = new MainViewModel(registry, _loggerFactory, memoryLog, imageFolders.AddFolder, FindUpwards(SampleRecipe));
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        viewModel.RefreshCamerasCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _loggerFactory?.Dispose();
        _fileLog?.Dispose(); // instances passed to AddProvider are not disposed by the factory
        base.OnExit(e);
    }

    private List<ICameraProvider> CreateProviders(ImageFolderProvider imageFolders)
    {
        List<ICameraProvider> providers =
        [
            imageFolders,
            new WebcamProvider(),
        ];

        // Optional vendor SDK: registered only when available, nothing else changes (design principle 6).
        if (PylonProviderFactory.TryCreate(new PylonOptions(), out string? reason) is { } pylon)
        {
            providers.Add(pylon);
        }
        else
        {
            LogProviderUnavailable(_logger!, PylonProviderFactory.ProviderId, reason ?? "unknown reason");
        }

        return providers;
    }

    /// <summary>Finds a repository-relative folder from the build output directory (dev runs).</summary>
    private static string? FindUpwards(string relativePath)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_logger is not null)
        {
            LogUnhandled(_logger, e.Exception);
        }

        MessageBox.Show(MainWindow, e.Exception.Message, "CamBridge 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera provider '{Provider}' not available: {Reason}")]
    private static partial void LogProviderUnavailable(ILogger logger, string provider, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled UI exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
