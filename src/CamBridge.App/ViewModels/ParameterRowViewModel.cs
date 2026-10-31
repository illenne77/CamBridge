using CamBridge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamBridge.App.ViewModels;

public enum ParameterEditor
{
    ReadOnly,
    Text,
    Choice,
    Toggle,
    Command,
}

/// <summary>One row of the advanced parameter list. Edits are committed through the owning <see cref="ParametersViewModel"/>.</summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly ParametersViewModel _owner;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Editor), nameof(RangeText), nameof(IsAvailable), nameof(ToolTip))]
    private CameraParameter _parameter;

    [ObservableProperty]
    private string _valueText = string.Empty;

    [ObservableProperty]
    private string? _selectedEntry;

    [ObservableProperty]
    private bool _toggleValue;

    public ParameterRowViewModel(ParametersViewModel owner, CameraParameter parameter)
    {
        _owner = owner;
        _parameter = parameter;
    }

    public string Name => Parameter.Name;

    public bool IsAvailable => Parameter.IsAvailable;

    public ParameterEditor Editor => Parameter switch
    {
        { IsAvailable: false } => ParameterEditor.ReadOnly,
        { Type: ParameterType.Command } => ParameterEditor.Command,
        { IsWritable: false } => ParameterEditor.ReadOnly,
        { Type: ParameterType.Enumeration } => ParameterEditor.Choice,
        { Type: ParameterType.Boolean } => ParameterEditor.Toggle,
        _ => ParameterEditor.Text,
    };

    public string RangeText
    {
        get
        {
            string range = Parameter.Type == ParameterType.Enumeration ? string.Empty : ParameterValues.DescribeRange(Parameter);
            return string.IsNullOrEmpty(Parameter.Unit) ? range : $"{range} {Parameter.Unit}".Trim();
        }
    }

    public string ToolTip => $"{Parameter.Name} ({Parameter.Type}){(Parameter.Description is null ? string.Empty : Environment.NewLine + Parameter.Description)}";

    /// <summary>Updates description and value from a refresh without committing anything.</summary>
    internal void Update(CameraParameter parameter, object? value)
    {
        _syncing = true;
        try
        {
            Parameter = parameter;
            ValueText = parameter.IsAvailable ? ParameterValues.Format(value) : "—";
            SelectedEntry = value as string;
            ToggleValue = value is true;
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private Task CommitAsync() => _owner.SetFromTextAsync(Name, Parameter.Type, ValueText);

    [RelayCommand]
    private Task ExecuteAsync() => _owner.ExecuteAsync(Name);

    partial void OnSelectedEntryChanged(string? value)
    {
        if (!_syncing && value is not null)
        {
            _ = _owner.SetAsync(Name, value);
        }
    }

    partial void OnToggleValueChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _owner.SetAsync(Name, value);
        }
    }
}
