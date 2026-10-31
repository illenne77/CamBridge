using System.ComponentModel;
using System.Windows;
using CamBridge.App.ViewModels;

namespace CamBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shutdownDone;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_shutdownDone)
        {
            return;
        }

        // Release the camera before the window goes away; close again once done.
        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownDone = true;
            // ShutdownAsync may complete synchronously; Close() inside OnClosing is ignored, so defer it.
            await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
