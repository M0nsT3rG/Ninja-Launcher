using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NinjaLauncher.Platform;

namespace NinjaLauncher.Desktop.Services;

/// <summary>
/// Desktop notifications for things that finish while you are elsewhere (downloads, the game closing or crashing).
/// Only shown when the setting is on and the launcher window is not the active window; otherwise the in-app toast
/// is enough.
/// </summary>
public sealed class NotificationService(SettingsHolder settings) : IDisposable
{
    private readonly DesktopNotifier _notifier = new();

    public void Notify(string title, string body) => Dispatcher.UIThread.Post(() =>
    {
        if (!settings.Current.DesktopNotifications) return;
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null || window.IsActive) return;
        _notifier.Show(title, body, window.TryGetPlatformHandle()?.Handle ?? 0);
    });

    public void Dispose() => _notifier.Dispose();
}
