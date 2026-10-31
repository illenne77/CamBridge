using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CamBridge.App.Imaging;
using CamBridge.Inspection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CamBridge.App.ViewModels;

/// <summary>
/// The inspection recipe being edited, the latest result and OK/NG counters, and the overlay
/// the live view draws and edits.
/// </summary>
/// <remarks>
/// <para>
/// Results are pulled: counters and the verdict text on a 4 Hz timer, overlay shapes from the
/// render tick through <see cref="IInspectionOverlay"/>. The inspection worker never posts to
/// the dispatcher.
/// </para>
/// <para>
/// Every edit rebuilds the engine off the UI thread (templates are read from disk) and hands it
/// to <see cref="InspectionConsumer.ReplaceEngine"/>; rebuilds are coalesced, so a burst of edits
/// produces one engine. ROI drags only preview until the mouse is released.
/// </para>
/// </remarks>
public sealed partial class InspectionViewModel : ObservableObject, IInspectionOverlay, IDisposable
{
    private const string RecipeFilter = "검사 레시피 (*.recipe.json)|*.recipe.json|JSON (*.json)|*.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly RoiRect NewToolRoi = new(20, 20, 80, 60);

    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly CoalescingApplier<InspectionRecipe> _rebuild;
    private readonly string? _sampleRecipePath;
    private InspectionConsumer? _consumer;
    private InspectionResult? _shownResult;
    private string _baseDirectory;
    private long _version;
    private int _previewIndex = -1;
    private RoiRect _previewRoi;
    private bool _loading;

    [ObservableProperty]
    private string _recipeName = "새 레시피";

    [ObservableProperty]
    private string? _recipePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveToolCommand))]
    private ToolViewModel? _selectedTool;

    [ObservableProperty]
    private long _okCount;

    [ObservableProperty]
    private long _ngCount;

    [ObservableProperty]
    private string _yieldText = "-";

    [ObservableProperty]
    private Verdict? _lastVerdict;

    [ObservableProperty]
    private string _lastResultText = "검사 결과 없음";

    [ObservableProperty]
    private string _reasonsText = string.Empty;

    [ObservableProperty]
    private string? _message;

    /// <param name="logger">Receives recipe load, save and rebuild events.</param>
    /// <param name="sampleRecipePath">
    /// Bundled demo recipe to start with. It is a starting point only: saving asks for another file
    /// instead of overwriting it, so edits made for other images stay out of the samples tree.
    /// </param>
    public InspectionViewModel(ILogger<InspectionViewModel> logger, string? sampleRecipePath)
    {
        _logger = logger;
        _baseDirectory = AppContext.BaseDirectory;
        _rebuild = new CoalescingApplier<InspectionRecipe>(RebuildAsync);
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => RefreshResult(), Dispatcher.CurrentDispatcher);
        _timer.Start();

        if (sampleRecipePath is not null && File.Exists(sampleRecipePath))
        {
            _sampleRecipePath = Path.GetFullPath(sampleRecipePath);
            TryLoad(_sampleRecipePath);
        }
    }

    public ObservableCollection<ToolViewModel> Tools { get; } = [];

    /// <summary>Folder the recipe dialogs start in while no recipe of the user's own is open.</summary>
    public string? WorkingFolder { get; set; }

    // ---- IInspectionOverlay -----------------------------------------------------------

    public long Version
    {
        get
        {
            InspectionResult? latest = _consumer?.LatestResult;
            if (!ReferenceEquals(latest, _shownResult))
            {
                _shownResult = latest;
                _version++;
            }

            return _version;
        }
    }

    public IReadOnlyList<RoiRect> EditableRois => Tools.Select(t => t.Roi).ToList();

    public int SelectedIndex => SelectedTool is null ? -1 : Tools.IndexOf(SelectedTool);

    public OverlaySnapshot GetSnapshot()
    {
        InspectionResult? result = _shownResult;
        var shapes = new List<OverlayShape>();
        for (int i = 0; i < Tools.Count; i++)
        {
            ToolViewModel tool = Tools[i];
            ToolResult? toolResult = result?.Tools.FirstOrDefault(t => t.ToolName == tool.Name);
            OverlayStyle style = !tool.Enabled || toolResult is null ? OverlayStyle.Roi
                : toolResult.Passed ? OverlayStyle.ToolPassed
                : OverlayStyle.ToolFailed;
            RoiRect rect = i == _previewIndex ? _previewRoi : tool.Roi;
            shapes.Add(new OverlayShape(rect, style, tool.Enabled ? tool.Name : $"{tool.Name} (꺼짐)", ReferenceEquals(tool, SelectedTool)));
        }

        foreach (OverlayRect overlay in result?.Overlays ?? [])
        {
            if (overlay.Kind is OverlayKind.Blob or OverlayKind.Match)
            {
                shapes.Add(new OverlayShape(overlay.Rect, overlay.Kind == OverlayKind.Blob ? OverlayStyle.Blob : OverlayStyle.Match));
            }
        }

        return new OverlaySnapshot(shapes, result?.Verdict, result?.FrameId ?? 0);
    }

    public void SelectRoi(int index)
    {
        if (index >= 0 && index < Tools.Count)
        {
            SelectedTool = Tools[index];
        }
    }

    public void PreviewRoi(int index, RoiRect roi)
    {
        _previewIndex = index;
        _previewRoi = roi;
        _version++;
    }

    public void CommitRoi(int index, RoiRect roi)
    {
        _previewIndex = -1;
        _version++;
        if (index >= 0 && index < Tools.Count)
        {
            Tools[index].Roi = roi; // triggers a rebuild through OnToolChanged
        }
    }

    // ---- Session ----------------------------------------------------------------------

    /// <summary>Builds an engine for a new session. Throws when a template image is missing.</summary>
    public InspectionEngine CreateEngine() => InspectionEngine.Create(BuildRecipe(), _baseDirectory);

    /// <summary>Connects to the consumer of the current session (or none).</summary>
    public void Attach(InspectionConsumer? consumer)
    {
        _consumer = consumer;
        _shownResult = null;
        _version++;
        RefreshResult();
    }

    public void Dispose() => _timer.Stop();

    // ---- Commands ---------------------------------------------------------------------

    [RelayCommand]
    private void LoadRecipe()
    {
        var dialog = new OpenFileDialog { Filter = RecipeFilter, InitialDirectory = RecipeDirectory() };
        if (dialog.ShowDialog() == true)
        {
            TryLoad(dialog.FileName);
        }
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        if (!HasOwnRecipeFile)
        {
            SaveRecipeAs();
            return;
        }

        Save(RecipePath!);
    }

    [RelayCommand]
    private void SaveRecipeAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = RecipeFilter,
            InitialDirectory = RecipeDirectory(),
            FileName = RecipePath is null ? "recipe.recipe.json" : Path.GetFileName(RecipePath),
        };
        if (dialog.ShowDialog() == true)
        {
            Save(dialog.FileName);
        }
    }

    [RelayCommand]
    private void AddBlobTool()
    {
        string name = Enumerable.Range(1, 1000).Select(i => $"Blob{i}").First(n => Tools.All(t => t.Name != n));
        var tool = new ToolViewModel(
            new BlobToolSettings { Name = name, Roi = NewToolRoi, Count = new ValueRange(Max: 0), MinBlobArea = 5 },
            OnToolChanged);
        Tools.Add(tool);
        SelectedTool = tool;
        OnToolChanged(tool);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTool))]
    private void RemoveTool()
    {
        if (SelectedTool is { } tool)
        {
            int index = Tools.IndexOf(tool);
            Tools.Remove(tool);
            SelectedTool = Tools.Count == 0 ? null : Tools[Math.Min(index, Tools.Count - 1)];
            OnToolChanged(tool);
        }
    }

    [RelayCommand]
    private void ResetCounters()
    {
        _consumer?.ResetCounters();
        RefreshResult();
    }

    private bool CanRemoveTool() => SelectedTool is not null;

    partial void OnSelectedToolChanged(ToolViewModel? value) => _version++;

    // ---- Recipe -----------------------------------------------------------------------

    private InspectionRecipe BuildRecipe() => new() { Name = RecipeName, Tools = Tools.Select(t => t.ToSettings()).ToList() };

    private void OnToolChanged(ToolViewModel tool)
    {
        _version++;
        if (!_loading)
        {
            _rebuild.Post(BuildRecipe());
        }
    }

    private async Task RebuildAsync(InspectionRecipe recipe)
    {
        string baseDirectory = _baseDirectory;
        try
        {
            InspectionEngine engine = await Task.Run(() => InspectionEngine.Create(recipe, baseDirectory));
            if (_consumer is { } consumer)
            {
                consumer.ReplaceEngine(engine); // the consumer owns it from here
                LogRebuilt(_logger, recipe.Name, recipe.Tools.Count);
            }
            else
            {
                engine.Dispose(); // no session: the build only validated the recipe
            }

            Message = null;
        }
#pragma warning disable CA1031 // Shown to the user; the previous engine keeps running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Message = $"레시피 적용 실패: {ex.Message}";
            LogRebuildFailed(_logger, ex);
        }
    }

    private void TryLoad(string path)
    {
        try
        {
            InspectionRecipe recipe = RecipeSerializer.Load(path);
            _loading = true;
            try
            {
                Tools.Clear();
                foreach (ToolSettings settings in recipe.Tools)
                {
                    Tools.Add(new ToolViewModel(settings, OnToolChanged));
                }
            }
            finally
            {
                _loading = false;
            }

            RecipeName = recipe.Name;
            RecipePath = Path.GetFullPath(path);
            _baseDirectory = Path.GetDirectoryName(RecipePath)!;
            SelectedTool = Tools.FirstOrDefault();
            Message = null;
            _version++;
            _rebuild.Post(BuildRecipe()); // also validates templates
            LogLoaded(_logger, RecipePath);
        }
#pragma warning disable CA1031 // Shown to the user; the current recipe stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Message = $"레시피 불러오기 실패: {ex.Message}";
            LogLoadFailed(_logger, ex, path);
        }
    }

    private void Save(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)!;
            if (!string.Equals(directory, _baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // Keep template paths relative to the recipe file, which is how they are resolved.
                foreach (ToolViewModel tool in Tools.Where(t => t.IsTemplate))
                {
                    string template = Path.GetFullPath(Path.Combine(_baseDirectory, tool.TemplatePath));
                    _loading = true;
                    tool.TemplatePath = Path.GetRelativePath(directory, template);
                    _loading = false;
                }

                _baseDirectory = directory;
            }

            RecipeSerializer.Save(BuildRecipe(), fullPath);
            RecipePath = fullPath;
            Message = null;
            LogSaved(_logger, fullPath);
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Message = $"레시피 저장 실패: {ex.Message}";
            LogSaveFailed(_logger, ex, path);
        }
    }

    private bool HasOwnRecipeFile =>
        RecipePath is not null && !string.Equals(RecipePath, _sampleRecipePath, StringComparison.OrdinalIgnoreCase);

    private string RecipeDirectory() => HasOwnRecipeFile ? Path.GetDirectoryName(RecipePath)! : WorkingFolder ?? _baseDirectory;

    private void RefreshResult()
    {
        InspectionConsumer? consumer = _consumer;
        OkCount = consumer?.OkCount ?? 0;
        NgCount = consumer?.NgCount ?? 0;
        long total = OkCount + NgCount;
        YieldText = total == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * OkCount / total:0.0}%");

        InspectionResult? result = consumer?.LatestResult;
        LastVerdict = result?.Verdict;
        LastResultText = result is null
            ? "검사 결과 없음"
            : string.Create(CultureInfo.InvariantCulture, $"#{result.FrameId}  {result.Duration.TotalMilliseconds:0.0} ms");
        ReasonsText = result is null ? string.Empty : string.Join(Environment.NewLine, result.Reasons);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recipe '{Name}' applied ({Tools} tools)")]
    private static partial void LogRebuilt(ILogger logger, string name, int tools);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe could not be applied")]
    private static partial void LogRebuildFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recipe loaded from {Path}")]
    private static partial void LogLoaded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe could not be loaded from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recipe saved to {Path}")]
    private static partial void LogSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe could not be saved to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
