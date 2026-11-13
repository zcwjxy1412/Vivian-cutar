using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using VIvian.Core.Execution;

namespace VIvian.App;

/// <summary>Owned floating window. Passive showing/activation never grants keyboard input.</summary>
public partial class AssistantWindow : Window
{
    public AssistantInputSession Interaction { get; }
    public MainViewModel? StopSource { get; }
    private readonly WindowWorkArea _workArea;
    private bool _opened;

    public AssistantWindow(AssistantViewModel model, IAssistantInputGate? gate = null, MainViewModel? host = null)
    {
        Interaction = new AssistantInputSession(gate);
        StopSource = host;
        InitializeComponent();
        _workArea = new WindowWorkArea(this, new Size(360, 460));
        DataContext = model;
        model.Messages.CollectionChanged += OnMessagesChanged;
        model.PropertyChanged += OnModelChanged;
        model.ExclusiveActionStarting += ReleaseForExclusiveAction;
        if (host is not null)
        {
            host.PropertyChanged += OnHostStateChanged;
            host.Queue.PropertyChanged += OnHostStateChanged;
            host.Queue.Unresolved.CollectionChanged += OnRecoveryChanged;
        }
        Deactivated += (_, _) => LockInput();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { LockInput(); model.CancelRequest(); }
        };
        Closed += (_, _) =>
        {
            LockInput();
            model.CancelRequest();
            model.Messages.CollectionChanged -= OnMessagesChanged;
            model.PropertyChanged -= OnModelChanged;
            model.ExclusiveActionStarting -= ReleaseForExclusiveAction;
            if (host is not null)
            {
                host.PropertyChanged -= OnHostStateChanged;
                host.Queue.PropertyChanged -= OnHostStateChanged;
                host.Queue.Unresolved.CollectionChanged -= OnRecoveryChanged;
            }
        };
        // No Loaded/Activated focus handler: Alt-Tab and incoming messages remain read-only.
    }

    public bool TryOpenForUser()
    {
        if (!Interaction.TryEnter()) return false;
        try
        {
            Show();
            _workArea.Recover(preferOwner: !_opened);
            _opened = true;
            if (Activate()) InputBox.Focus();
            else LockInput();
            return Interaction.CanEdit;
        }
        catch { LockInput(); throw; }
    }

    public void LockInput()
    {
        // Local IsEnabled values take effect before the desktop lease is released.
        EditingSurface.SetCurrentValue(IsEnabledProperty, false);
        Interaction.Dispose();

    }

    private void ReleaseForExclusiveAction()
    {
        if (Dispatcher.CheckAccess()) LockInput();
        else Dispatcher.Invoke(LockInput);
    }

    private void EnterChat(object sender, RoutedEventArgs e) => TryOpenForUser();
    private void StopLocally(object sender, RoutedEventArgs e)
    {
        LockInput();
        if (StopSource?.CanStopAll == true) StopSource.StopAll();
    }

    private void OnHostStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RecheckInput();
    private void OnRecoveryChanged(object? sender, NotifyCollectionChangedEventArgs e) => RecheckInput();
    private void RecheckInput()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RecheckInput); return; }
        if (Interaction.CanEdit && StopSource?.AssistantInputBlocked == true) LockInput();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd);
    }
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is AssistantViewModel model && e.PropertyName == nameof(model.NewApiKey) && model.NewApiKey.Length == 0)
            KeyBox.Clear();
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Hide();
    private void KeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantViewModel vm) vm.NewApiKey = KeyBox.Password;
    }
}
