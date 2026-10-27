using System.Collections.ObjectModel;
using System.Windows.Threading;
using CamBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.ViewModels;

/// <summary>
/// Exposure, gain and trigger controls plus the generic (GenICam-style) parameter list.
/// </summary>
/// <remarks>
/// <para>
/// Every camera call runs on the thread pool behind one gate: a webcam parameter access waits
/// up to a frame on its device thread and a GigE access is a network round trip, neither of
/// which may block the UI. Slider moves are coalesced so the camera sees only the latest value.
/// </para>
/// <para>
/// The list is read on attach, after each write and on state changes, not polled: describing
/// all parameters costs one device access per parameter. Only while an auto mode is on are the
/// exposure and gain values polled (1 Hz) to follow the camera.
/// </para>
/// </remarks>
public sealed partial class ParametersViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AutoPollInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _autoPoll;
    private readonly CoalescingApplier<double> _exposureApplier;
    private readonly CoalescingApplier<double> _gainApplier;
    private readonly Dictionary<string, ParameterRowViewModel> _rowsByName = new(StringComparer.Ordinal);
    private ICamera? _camera;
    private bool _syncing;

    [ObservableProperty]
    private bool _hasCamera;

    [ObservableProperty]
    private bool _exposureAvailable;

    [ObservableProperty]
    private bool _exposureWritable;

    [ObservableProperty]
    private double _exposureSliderMin;

    [ObservableProperty]
    private double _exposureSliderMax = 1;

    /// <summary>log10 of the exposure in microseconds: exposure ranges span several decades.</summary>
    [ObservableProperty]
    private double _exposureSlider;

    [ObservableProperty]
    private string _exposureText = string.Empty;

    [ObservableProperty]
    private string _exposureRange = string.Empty;

    [ObservableProperty]
    private bool _hasExposureAuto;

    [ObservableProperty]
    private bool _exposureAuto;

    [ObservableProperty]
    private bool _gainAvailable;

    [ObservableProperty]
    private bool _gainWritable;

    [ObservableProperty]
    private double _gainMin;

    [ObservableProperty]
    private double _gainMax = 1;

    [ObservableProperty]
    private double _gainSlider;

    [ObservableProperty]
    private string _gainText = string.Empty;

    [ObservableProperty]
    private string _gainUnit = string.Empty;

    [ObservableProperty]
    private bool _hasGainAuto;

    [ObservableProperty]
    private bool _gainAuto;

    [ObservableProperty]
    private IReadOnlyList<string> _triggerModes = [];

    [ObservableProperty]
    private string? _triggerMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SoftwareTriggerCommand))]
    private bool _canSoftwareTrigger;

    [ObservableProperty]
    private string? _message;

    public ParametersViewModel(ILogger<ParametersViewModel> logger)
    {
        _logger = logger;
        // While dragging, only the value is written; the full refresh runs once the drag settles.
        _exposureApplier = new CoalescingApplier<double>(async us =>
        {
            await SetAsync(ParameterNames.ExposureTime, us, refresh: false);
            if (!_exposureApplier!.HasPending)
            {
                await RefreshAsync();
            }
        });
        _gainApplier = new CoalescingApplier<double>(async gain =>
        {
            await SetAsync(ParameterNames.Gain, gain, refresh: false);
            if (!_gainApplier!.HasPending)
            {
                await RefreshAsync();
            }
        });
        _autoPoll = new DispatcherTimer(AutoPollInterval, DispatcherPriority.Background, async (_, _) => await PollAutoValuesAsync(), Dispatcher.CurrentDispatcher);
    }

    public ObservableCollection<ParameterRowViewModel> Rows { get; } = [];

    public void Dispose()
    {
        _autoPoll.Stop();
        _camera = null;
        _gate.Dispose();
    }

    /// <summary>Switches to another camera (or none) and reads its parameters.</summary>
    public async Task AttachAsync(ICamera? camera)
    {
        _camera = camera;
        HasCamera = camera is not null;
        Message = null;
        _autoPoll.Stop();
        if (camera is null)
        {
            Rows.Clear();
            _rowsByName.Clear();
            ApplySnapshot([], new Dictionary<string, object?>());
            return;
        }

        await RefreshAsync();
    }

    /// <summary>Re-reads all parameters; availability and writability depend on the camera state.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        ICamera? camera = _camera;
        if (camera is null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            (IReadOnlyList<CameraParameter> parameters, Dictionary<string, object?> values) = await Task.Run(() => ReadAll(camera));
            if (ReferenceEquals(camera, _camera))
            {
                ApplySnapshot(parameters, values);
            }
        }
#pragma warning disable CA1031 // Shown to the user; the panel stays usable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError("파라미터 읽기 실패", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes one parameter, then refreshes because other parameters may depend on it.</summary>
    public async Task SetAsync(string name, object value, bool refresh = true)
    {
        ICamera? camera = _camera;
        if (camera is null)
        {
            return;
        }

        bool written = false;
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() => camera.SetParameter(name, value));
            written = true;
            Message = null;
        }
#pragma warning disable CA1031 // Rejected values are expected (range, auto mode); show why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError($"{name} 설정 실패", ex);
        }
        finally
        {
            _gate.Release();
        }

        // Refresh after a rejected write too, so the UI snaps back to the camera's value.
        if (refresh || !written)
        {
            await RefreshAsync();
        }

        if (written)
        {
            LogParameterSet(_logger, name, ParameterValues.Format(value));
        }
    }

    public Task SetFromTextAsync(string name, ParameterType type, string text)
    {
        object value;
        try
        {
            value = ParameterValues.Parse(type, text);
        }
        catch (FormatException)
        {
            Message = $"{name}: '{text}'은(는) {type} 값이 아닙니다.";
            return Task.CompletedTask;
        }

        return SetAsync(name, value);
    }

    public async Task ExecuteAsync(string name)
    {
        ICamera? camera = _camera;
        if (camera is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => camera.ExecuteCommand(name));
            Message = null;
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError($"{name} 실행 실패", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSoftwareTrigger))]
    private Task SoftwareTriggerAsync() => ExecuteAsync(ParameterNames.TriggerSoftware);

    [RelayCommand]
    private Task ApplyExposureTextAsync() => SetFromTextAsync(ParameterNames.ExposureTime, ParameterType.Float, ExposureText);

    [RelayCommand]
    private Task ApplyGainTextAsync() => SetFromTextAsync(ParameterNames.Gain, ParameterType.Float, GainText);

    partial void OnExposureSliderChanged(double value)
    {
        if (!_syncing)
        {
            double microseconds = Math.Pow(10, value);
            ExposureText = ParameterValues.Format(Math.Round(microseconds, microseconds < 100 ? 1 : 0));
            _exposureApplier.Post(microseconds);
        }
    }

    partial void OnGainSliderChanged(double value)
    {
        if (!_syncing)
        {
            GainText = ParameterValues.Format(value);
            _gainApplier.Post(value);
        }
    }

    partial void OnExposureAutoChanged(bool value)
    {
        if (!_syncing)
        {
            _ = SetAsync(ParameterNames.ExposureAuto, value ? ParameterValues.AutoContinuous : ParameterValues.AutoOff);
        }
    }

    partial void OnGainAutoChanged(bool value)
    {
        if (!_syncing)
        {
            _ = SetAsync(ParameterNames.GainAuto, value ? ParameterValues.AutoContinuous : ParameterValues.AutoOff);
        }
    }

    partial void OnTriggerModeChanged(string? value)
    {
        if (!_syncing && value is not null)
        {
            _ = SetAsync(ParameterNames.TriggerMode, value);
        }
    }

    private static (IReadOnlyList<CameraParameter> Parameters, Dictionary<string, object?> Values) ReadAll(ICamera camera)
    {
        IReadOnlyList<CameraParameter> parameters = camera.GetParameters();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (CameraParameter parameter in parameters)
        {
            if (parameter is { IsAvailable: true, IsReadable: true } && parameter.Type != ParameterType.Command)
            {
                values[parameter.Name] = TryGet(camera, parameter.Name);
            }
        }

        return (parameters, values);
    }

    private static object? TryGet(ICamera camera, string name)
    {
        try
        {
            return camera.GetParameter(name);
        }
        catch (InvalidOperationException)
        {
            return null; // became unavailable between describe and read
        }
    }

    private void ApplySnapshot(IReadOnlyList<CameraParameter> parameters, Dictionary<string, object?> values)
    {
        var byName = parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _syncing = true;
        try
        {
            CameraParameter? exposure = byName.GetValueOrDefault(ParameterNames.ExposureTime);
            ExposureAvailable = exposure is { IsAvailable: true };
            ExposureWritable = exposure is { IsAvailable: true, IsWritable: true };
            if (exposure is { Minimum: double min, Maximum: double max } && min > 0)
            {
                ExposureSliderMin = Math.Log10(min);
                ExposureSliderMax = Math.Log10(max);
                ExposureRange = $"{ParameterValues.Format(min)} .. {ParameterValues.Format(max)} {exposure.Unit}";
            }

            if (values.GetValueOrDefault(ParameterNames.ExposureTime) is double us && us > 0)
            {
                ExposureSlider = Math.Log10(us);
                ExposureText = ParameterValues.Format(Math.Round(us, us < 100 ? 1 : 0));
            }

            HasExposureAuto = byName.ContainsKey(ParameterNames.ExposureAuto);
            ExposureAuto = values.GetValueOrDefault(ParameterNames.ExposureAuto) is ParameterValues.AutoContinuous;

            CameraParameter? gain = byName.GetValueOrDefault(ParameterNames.Gain);
            GainAvailable = gain is { IsAvailable: true };
            GainWritable = gain is { IsAvailable: true, IsWritable: true };
            GainMin = gain?.Minimum ?? 0;
            GainMax = gain?.Maximum ?? 1;
            GainUnit = gain?.Unit ?? string.Empty;
            if (values.GetValueOrDefault(ParameterNames.Gain) is double g)
            {
                GainSlider = g;
                GainText = ParameterValues.Format(g);
            }
            else
            {
                GainText = gain is { IsAvailable: false } ? "지원 안 함" : string.Empty;
            }

            HasGainAuto = byName.ContainsKey(ParameterNames.GainAuto);
            GainAuto = values.GetValueOrDefault(ParameterNames.GainAuto) is ParameterValues.AutoContinuous;

            TriggerModes = byName.GetValueOrDefault(ParameterNames.TriggerMode)?.EnumEntries ?? [];
            TriggerMode = values.GetValueOrDefault(ParameterNames.TriggerMode) as string;
            CanSoftwareTrigger = byName.GetValueOrDefault(ParameterNames.TriggerSoftware) is { IsAvailable: true };
        }
        finally
        {
            _syncing = false;
        }

        UpdateRows(parameters, values);

        if (ExposureAuto || GainAuto)
        {
            _autoPoll.Start();
        }
        else
        {
            _autoPoll.Stop();
        }
    }

    /// <summary>Updates rows in place so focus and scroll position survive a refresh.</summary>
    private void UpdateRows(IReadOnlyList<CameraParameter> parameters, Dictionary<string, object?> values)
    {
        var names = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.Ordinal);
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(Rows[i].Name))
            {
                _rowsByName.Remove(Rows[i].Name);
                Rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            CameraParameter parameter = parameters[i];
            if (!_rowsByName.TryGetValue(parameter.Name, out ParameterRowViewModel? row))
            {
                row = new ParameterRowViewModel(this, parameter);
                _rowsByName[parameter.Name] = row;
                Rows.Insert(Math.Min(i, Rows.Count), row);
            }

            row.Update(parameter, values.GetValueOrDefault(parameter.Name));
        }
    }

    /// <summary>Follows values the camera changes itself while an auto mode is on.</summary>
    private async Task PollAutoValuesAsync()
    {
        ICamera? camera = _camera;
        if (camera is null || !await _gate.WaitAsync(0))
        {
            return; // a write or refresh is in progress; it will show fresh values
        }

        try
        {
            (object? exposure, object? gain) = await Task.Run(() => (
                ExposureAuto ? TryGet(camera, ParameterNames.ExposureTime) : null,
                GainAuto ? TryGet(camera, ParameterNames.Gain) : null));
            if (!ReferenceEquals(camera, _camera))
            {
                return;
            }

            _syncing = true;
            if (exposure is double us && us > 0)
            {
                ExposureSlider = Math.Log10(us);
                ExposureText = ParameterValues.Format(Math.Round(us, us < 100 ? 1 : 0));
                if (_rowsByName.TryGetValue(ParameterNames.ExposureTime, out ParameterRowViewModel? row))
                {
                    row.Update(row.Parameter, us);
                }
            }

            if (gain is double g)
            {
                GainSlider = g;
                GainText = ParameterValues.Format(g);
            }
        }
#pragma warning disable CA1031 // Polling is best effort; the next refresh reports real failures.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _syncing = false;
            _gate.Release();
        }
    }

    private void ShowError(string context, Exception ex)
    {
        Message = $"{context}: {ex.Message}";
        LogParameterFailed(_logger, ex, context);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Set {Name} = {Value}")]
    private static partial void LogParameterSet(ILogger logger, string name, string value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Context}")]
    private static partial void LogParameterFailed(ILogger logger, Exception exception, string context);
}
