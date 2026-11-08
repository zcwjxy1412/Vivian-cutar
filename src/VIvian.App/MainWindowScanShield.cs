using System.Windows;
using VIvian.Infrastructure.Accounts;

namespace VIvian.App;

/// <summary>Minimises every visible VIvian window for the duration of a scan and restores them afterwards.</summary>
public sealed class MainWindowScanShield : IScanWindowShield
{
    public async Task<IAsyncDisposable> HideAsync(CancellationToken cancellationToken)
    {
        var hidden = new List<(Window Window, WindowState State)>();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (!window.IsVisible || window.WindowState == WindowState.Minimized) continue;
                hidden.Add((window, window.WindowState));
                window.WindowState = WindowState.Minimized;
            }
        });
        // Give the shell time to finish the minimise animation before pixels are read.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        return new Restore(hidden);
    }

    private sealed class Restore(List<(Window Window, WindowState State)> hidden) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var (window, state) in hidden)
                {
                    window.WindowState = state;
                    window.Activate();
                }
            });
        }
    }
}
