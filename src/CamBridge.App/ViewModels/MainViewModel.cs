using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CamBridge.App.Logging;
using CamBridge.App.Session;
using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Statistics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.ViewModels;

/// <summary>A camera row in the camera panel, grouped by provider in the view.</summary>
public sealed record CameraItem(CameraInfo Info, string VendorName)
{
    public string DisplayName => Info.DisplayName;
}

/// <summary>
/// Camera selection, connection and acquisition control, and the always-visible statistics.
/// Statistics and the log strip are pulled on a 4 Hz dispatcher timer; nothing on the camera
/// or pipeline threads posts to the dispatcher per frame.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int LogLinesShown = 100;
    private static readonly TimeSpan StatisticsInterval = TimeSpan.FromMilliseconds(250);

    private readonly CameraRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MemoryLogProvider _memoryLog;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private long _logVersion = -1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private CameraItem? _selectedCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(StartCommand), nameof(StopCommand))]
    private CameraSession? _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private CameraState _state = CameraState.Closed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(StartCommand), nameof(StopCommand), nameof(RefreshCamerasCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private LatestFrameSlot? _liveFrames;

    [ObservableProperty]
    private double _displayFps;

    [ObservableProperty]
    private string _receivedText = "-";

    [ObservableProperty]
    private string _droppedText = "-";

    [ObservableProperty]
    private string _consumersText = "-";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _enumerationMessage;

    public MainViewModel(CameraRegistry registry, ILoggerFactory loggerFactory, MemoryLogProvider memoryLog, string? recipePath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(memoryLog);

        _registry = registry;
        _loggerFactory = loggerFactory;
        _memoryLog = memoryLog;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        Parameters = new ParametersViewModel(loggerFactory.CreateLogger<ParametersViewModel>());
        Inspection = new InspectionViewModel(loggerFactory.CreateLogger<InspectionViewModel>(), recipePath);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(StatisticsInterval, DispatcherPriority.Background, (_, _) => OnTick(), _dispatcher);
        _timer.Start();
    }

    public ObservableCollection<CameraItem> Cameras { get; } = [];

    public ParametersViewModel Parameters { get; }

    public InspectionViewModel Inspection { get; }

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public bool IsConnected => Session is not null;

    public string StateText => State switch
    {
        CameraState.Closed => "연결 안 됨",
        CameraState.Open => "연결됨",
        CameraState.Acquiring => "획득 중",
        CameraState.Faulted => "오류 — 연결 해제 후 다시 연결하세요",
        _ => State.ToString(),
    };

    public string ConnectedCameraName => Session?.Camera.Info.DisplayName ?? "-";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshCamerasAsync()
    {
        IsBusy = true;
        try
        {
            CameraEnumerationResult result = await _registry.EnumerateAsync();
            string? previous = SelectedCamera?.Info.DeviceId;
            Cameras.Clear();
            foreach (CameraInfo info in result.Cameras)
            {
                string vendor = _registry.Providers.First(p => string.Equals(p.ProviderId, info.ProviderId, StringComparison.OrdinalIgnoreCase)).VendorName;
                Cameras.Add(new CameraItem(info, vendor));
            }

            SelectedCamera = Cameras.FirstOrDefault(c => c.Info.DeviceId == previous) ?? Cameras.FirstOrDefault();
            EnumerationMessage = result.Failures.Count == 0
                ? null
                : string.Join(Environment.NewLine, result.Failures.Select(f => $"{f.ProviderId}: {f.Exception.Message}"));
            foreach (ProviderFailure failure in result.Failures)
            {
                LogEnumerationFailed(_logger, failure.Exception, failure.ProviderId);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        CameraInfo info = SelectedCamera!.Info;
        await RunAsync($"{info.DisplayName} 연결 실패", async () =>
        {
            // Without a usable recipe the camera still connects, just without inspection.
            InspectionEngine? engine = null;
            try
            {
                engine = await Task.Run(Inspection.CreateEngine);
            }
#pragma warning disable CA1031 // Reported in the inspection panel; acquisition works without it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Inspection.Message = $"검사 비활성: {ex.Message}";
            }

            CameraSession session = await CameraSession.OpenAsync(_registry, info, engine, _loggerFactory);
            session.StateChanged += OnSessionStateChanged;
            Inspection.Attach(session.Inspection);
            Session = session;
            LiveFrames = session.Pipeline.LiveView;
            State = session.State;
            OnPropertyChanged(nameof(ConnectedCameraName));
            await Parameters.AttachAsync(session.Camera);
        });
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => RunAsync("연결 해제 실패", CloseSessionAsync);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync("획득 시작 실패", () => Session!.StartAsync());

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunAsync("획득 정지 실패", () => Session!.StopAsync());

    /// <summary>Stops the timer and releases the camera; called when the window closes.</summary>
    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await CloseSessionAsync();
        Parameters.Dispose();
        Inspection.Dispose();
    }

    private bool CanRefresh() => !IsBusy;

    private bool CanConnect() => SelectedCamera is not null && Session is null && !IsBusy;

    private bool CanDisconnect() => Session is not null && !IsBusy;

    private bool CanStart() => Session is not null && State == CameraState.Open && !IsBusy;

    private bool CanStop() => Session is not null && State == CameraState.Acquiring && !IsBusy;

    private async Task RunAsync(string failureMessage, Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
        }
#pragma warning disable CA1031 // Any failure is shown to the user and logged; the UI stays usable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"{failureMessage}: {ex.Message}";
            LogCommandFailed(_logger, ex, failureMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CloseSessionAsync()
    {
        CameraSession? session = Session;
        if (session is null)
        {
            return;
        }

        session.StateChanged -= OnSessionStateChanged;
        await Parameters.AttachAsync(null);
        Inspection.Attach(null);
        LiveFrames = null;
        Session = null;
        State = CameraState.Closed;
        OnPropertyChanged(nameof(ConnectedCameraName));
        ClearStatistics();
        await session.DisposeAsync();
    }

    private void OnSessionStateChanged(object? sender, CameraStateChangedEventArgs e)
    {
        // Rare event (not per frame), raised on camera threads.
        _dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(sender, Session))
            {
                State = e.NewState;
                _ = Parameters.RefreshAsync(); // writability and trigger availability depend on the state
            }
        });
    }

    private void OnTick()
    {
        if (Session is { } session)
        {
            ShowStatistics(session.Pipeline.GetStatistics());
        }

        long version = _memoryLog.Version;
        if (version != _logVersion)
        {
            _logVersion = version;
            IReadOnlyList<LogEntry> entries = _memoryLog.Snapshot();
            LogEntries.Clear();
            foreach (LogEntry entry in entries.Skip(Math.Max(0, entries.Count - LogLinesShown)).Reverse())
            {
                LogEntries.Add(entry);
            }
        }
    }

    private void ShowStatistics(PipelineStatistics stats)
    {
        ReceivedText = string.Create(CultureInfo.InvariantCulture,
            $"수신 {stats.ReceivedFps:0.0} fps ({stats.ReceivedFrames:N0})   표시 {DisplayFps:0.0} fps (건너뜀 {stats.LiveViewSkippedFrames:N0})");
        DroppedText = string.Create(CultureInfo.InvariantCulture,
            $"드롭 {stats.TotalDroppedFrames:N0} (카메라 {stats.CameraDroppedFrames:N0})");
        ConsumersText = stats.Consumers.Count == 0
            ? "소비자 없음"
            : string.Join("   ", stats.Consumers.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Name} {c.ProcessedFps:0.0} fps 큐 {c.QueueLength}/{c.QueueCapacity} 드롭 {c.DroppedFrames:N0}{(c.Faults > 0 ? $" 오류 {c.Faults}" : string.Empty)}")));
    }

    private void ClearStatistics()
    {
        ReceivedText = "-";
        DroppedText = "-";
        ConsumersText = "-";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera enumeration failed for provider {Provider}")]
    private static partial void LogEnumerationFailed(ILogger logger, Exception exception, string provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Action}")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception, string action);
}
