using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;

namespace VIvian.App;

/// <summary>Movable floating assistant. Closing only hides it, so the conversation is kept.</summary>
public partial class AssistantWindow : Window
{
    public AssistantWindow(AssistantViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd);
        };
        Loaded += (_, _) => InputBox.Focus();
        IsVisibleChanged += (_, _) => { if (!IsVisible) model.CancelRequest(); };
        Closed += (_, _) => model.CancelRequest();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.NewApiKey) && model.NewApiKey.Length == 0)
                KeyBox.Clear();
        };
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
