using System.Windows;
namespace VIvian.App;
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        // Closing the window while a queue runs stops it immediately, then waits (bounded) for the queue to record
        // its final state before the window really closes.
        Closing += async (_, e) =>
        {
            if (_closeAfterStop || !viewModel.Queue.IsRunning) return;
            e.Cancel = true;
            if (_stopping) return; // a second close click while the stop is in progress
            _stopping = true;
            try { await viewModel.Queue.StopForShutdownAsync(ShutdownStopTimeout); }
            catch (Exception ex) { Serilog.Log.Error("Shutdown stop failed: {ErrorType}", ex.GetType().Name); }
            _closeAfterStop = true;
            Close();
        };
    }

    private void BrowseReleaseArchive(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择官方 March7thAssistant 发行包",
            Filter = "官方发行包 (*.zip;*.7z)|*.zip;*.7z|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true && DataContext is MainViewModel vm) vm.ReleaseArchivePath = dialog.FileName;
    }

    private static readonly TimeSpan ShutdownStopTimeout = TimeSpan.FromSeconds(30);
    private bool _stopping;
    private bool _closeAfterStop;
}
