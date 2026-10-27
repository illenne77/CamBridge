using System.IO;
using System.Windows;
using System.Windows.Threading;
using CamBridge.App.Logging;
using CamBridge.App.ViewModels;
using CamBridge.Core;
using CamBridge.Sources.ImageFolder;
using CamBridge.Sources.Webcam;
using Microsoft.Extensions.Logging;

namespace CamBridge.App;

/// <summary>Composition root: logging, camera providers and the main window.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF never disposes the Application; owned resources are released in OnExit.")]
public partial class App : Application
{
    private const string SampleImagesFolder = "samples/part-inspection/images";

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

        var registry = new CameraRegistry(CreateProviders());
        var viewModel = new MainViewModel(registry, _loggerFactory, memoryLog);
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

    private static List<ICameraProvider> CreateProviders()
    {
        string? samples = FindUpwards(SampleImagesFolder);
        return
        [
            new ImageFolderProvider(samples is null ? [] : [samples]),
            new WebcamProvider(),
        ];
    }

    /// <summary>Finds a repository-relative folder from the build output directory (dev runs).</summary>
    private static string? FindUpwards(string relativePath)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (Directory.Exists(candidate))
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled UI exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
