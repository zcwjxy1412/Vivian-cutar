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
            if (_closeAfterStop || !viewModel.CanStopAll) return;
            e.Cancel = true;
            if (_stopping) return; // a second close click while the stop is in progress
            _stopping = true;
            try
            {
                if (viewModel.BatchRunning)
                {
                    if (!await viewModel.WaitForBatchShutdownAsync(ShutdownStopTimeout)) return;
                }
                else await viewModel.Queue.StopForShutdownAsync(ShutdownStopTimeout);
            }
            catch (Exception ex) { Serilog.Log.Error("Shutdown stop failed: {ErrorType}", ex.GetType().Name); }
            finally { _stopping = false; }
            _closeAfterStop = true;
            Close();
        };
        // F10 stops a running queue from anywhere, even while the game is full-screen.
        SourceInitialized += (_, _) =>
        {
            _hotkey = new StopHotkey(this, () =>
            {
                if (!viewModel.CanStopAll) return;
                Serilog.Log.Information("Emergency stop requested by F10");
                viewModel.StopAll();
            });
            // Held for a single run and for a whole multi-account batch (including the switching between accounts).
            void Update()
            {
                if (_hotkey is null) return;
                if (!viewModel.CanStopAll) { _hotkey.Unregister(); return; }
                if (_hotkey.IsRegistered) return;
                if (!_hotkey.Register())
                    Serilog.Log.Warning("F10 stop key unavailable: owned by another program");
                viewModel.StopKeyText = _hotkey.IsRegistered
                    ? "游戏全屏时也可以按 F10 立即停止。"
                    : "F10 已被其他程序占用（例如星铁助手窗口开着），请用 VIvian 里的“紧急停止”按钮。";
            }
            viewModel.Queue.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QueueViewModel.IsRunning)) Update(); };
            viewModel.BatchRunningChanged += () => Dispatcher.BeginInvoke(Update);
        };
        Closed += (_, _) =>
        {
            _hotkey?.Dispose();
            viewModel.GenshinConfiguration.Close();
        };
    }

    private StopHotkey? _hotkey;

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

    private void BrowseEngineFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EngineCard card) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"选择 {card.Engine} 的安装目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true) card.InstallPath = dialog.FolderName;
    }

    private AssistantWindow? _assistant;

    /// <summary>Clicking the character opens the floating assistant (one window, kept between clicks).</summary>
    private void OpenAssistant(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_assistant is null)
        {
            _assistant = new AssistantWindow(vm.Assistant) { Owner = this };
            _assistant.Closed += (_, _) => _assistant = null;
            _assistant.Left = Left + Math.Max(0, ActualWidth - _assistant.Width - 40);
            _assistant.Top = Top + 90;
        }
        _assistant.Show();
        _assistant.Activate();
    }

    private void OpenGameSettings(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedIndex = 1;
        GameTabs.SelectedIndex = (sender as FrameworkElement)?.Tag switch
        {
            VIvian.Core.GameId.StarRail => 0, VIvian.Core.GameId.Genshin => 1, _ => 2,
        };
    }

    private void GenshinSourceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.GenshinConfiguration.Source.SetVisible(e.NewValue is true);
    }

    private void OpenEngineSettings(object sender, RoutedEventArgs e) => Navigation.SelectedItem =
        Navigation.Items.OfType<System.Windows.Controls.TabItem>().First(item => Equals(item.Header, "引擎与环境"));

    private async void GenshinLoginVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is MainViewModel vm)
            await vm.GenshinConfiguration.DiscoverGameExecutableAsync();
    }

    private void BrowseGenshinExecutable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择原神国服 YuanShen.exe",
            Filter = "原神国服程序 (YuanShen.exe)|YuanShen.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) vm.GenshinConfiguration.GameExecutable = dialog.FileName;
    }

    /// <summary>Creates the assets folder and copies its path (VIvian starts no external processes outside the supervisor).</summary>
    private void CopyThemeFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try { System.IO.Directory.CreateDirectory(vm.Theme.Folder); Clipboard.SetText(vm.Theme.Folder); vm.ReportCopied(vm.Theme.Folder); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
    }

    private void CopyExportsPath(object sender, RoutedEventArgs e) => CopyFolder("exports");
    private void CopyLogsPath(object sender, RoutedEventArgs e) => CopyFolder("logs");

    private void CopyFolder(string which)
    {
        if (DataContext is not MainViewModel vm || vm.FolderPath(which) is not { } path) return;
        try { Clipboard.SetText(path); vm.ReportCopied(path); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: the path is still shown below */ }
    }

    private static readonly TimeSpan ShutdownStopTimeout = VIvian.Core.Engines.RealExecutionPolicy.BuildPermitsRealExecution
        ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
    private bool _stopping;
    private bool _closeAfterStop;
}
