using System.ComponentModel;
using CamBridge.Inspection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CamBridge.App.ViewModels;

/// <summary>
/// Editable copy of one tool's settings. Any change notifies the owner, which rebuilds the
/// inspection engine from <see cref="ToSettings"/>; the settings records themselves stay immutable.
/// </summary>
public sealed partial class ToolViewModel : ObservableObject
{
    private static readonly HashSet<string> DerivedProperties = [nameof(RoiText)];

    private readonly Action<ToolViewModel> _changed;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiText))]
    private RoiRect _roi;

    // Blob
    [ObservableProperty]
    private ThresholdMode _thresholdMode;

    [ObservableProperty]
    private int _threshold;

    [ObservableProperty]
    private BlobPolarity _polarity;

    [ObservableProperty]
    private int _minBlobArea;

    [ObservableProperty]
    private double? _countMin;

    [ObservableProperty]
    private double? _countMax;

    [ObservableProperty]
    private double? _totalAreaMin;

    [ObservableProperty]
    private double? _totalAreaMax;

    [ObservableProperty]
    private double? _maxAreaMin;

    [ObservableProperty]
    private double? _maxAreaMax;

    // Template match
    [ObservableProperty]
    private double _minScore;

    [ObservableProperty]
    private string _templatePath = string.Empty;

    public ToolViewModel(ToolSettings settings, Action<ToolViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _changed = changed;
        Name = settings.Name;
        IsBlob = settings is BlobToolSettings;
        _enabled = settings.Enabled;
        _roi = settings.Roi;

        switch (settings)
        {
            case BlobToolSettings blob:
                _thresholdMode = blob.ThresholdMode;
                _threshold = blob.Threshold;
                _polarity = blob.Polarity;
                _minBlobArea = blob.MinBlobArea;
                (_countMin, _countMax) = (blob.Count?.Min, blob.Count?.Max);
                (_totalAreaMin, _totalAreaMax) = (blob.TotalArea?.Min, blob.TotalArea?.Max);
                (_maxAreaMin, _maxAreaMax) = (blob.MaxArea?.Min, blob.MaxArea?.Max);
                break;

            case TemplateMatchToolSettings match:
                _minScore = match.MinScore;
                _templatePath = match.TemplatePath;
                break;

            default:
                throw new NotSupportedException($"Tool type {settings.GetType().Name} cannot be edited.");
        }
    }

    public string Name { get; }

    public bool IsBlob { get; }

    public bool IsTemplate => !IsBlob;

    public string Kind => IsBlob ? "Blob" : "Template";

    public string RoiText => Roi.ToString();

    public static IReadOnlyList<ThresholdMode> ThresholdModes { get; } = Enum.GetValues<ThresholdMode>();

    public static IReadOnlyList<BlobPolarity> Polarities { get; } = Enum.GetValues<BlobPolarity>();

    public ToolSettings ToSettings() => IsBlob
        ? new BlobToolSettings
        {
            Name = Name,
            Roi = Roi,
            Enabled = Enabled,
            ThresholdMode = ThresholdMode,
            Threshold = Math.Clamp(Threshold, 0, 255),
            Polarity = Polarity,
            MinBlobArea = Math.Max(1, MinBlobArea),
            Count = Range(CountMin, CountMax),
            TotalArea = Range(TotalAreaMin, TotalAreaMax),
            MaxArea = Range(MaxAreaMin, MaxAreaMax),
        }
        : new TemplateMatchToolSettings
        {
            Name = Name,
            Roi = Roi,
            Enabled = Enabled,
            TemplatePath = TemplatePath,
            MinScore = Math.Clamp(MinScore, 0, 1),
        };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && !DerivedProperties.Contains(e.PropertyName))
        {
            _changed(this);
        }
    }

    private static ValueRange? Range(double? min, double? max) => min is null && max is null ? null : new ValueRange(min, max);
}
