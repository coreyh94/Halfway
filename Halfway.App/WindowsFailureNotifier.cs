using Halfway.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Halfway.App;

public sealed class WindowsFailureNotifier : IDisposable
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly Action _activated;
    private bool _registered;

    public bool IsAvailable => _registered;

    public WindowsFailureNotifier(Action activated)
    {
        _activated = activated;
        try
        {
            _manager.NotificationInvoked += Manager_NotificationInvoked;
            _manager.Register();
            _registered = true;
        }
        catch
        {
            _manager.NotificationInvoked -= Manager_NotificationInvoked;
        }
    }

    public void Show(FailureNotification notification)
    {
        if (!_registered) return;
        try
        {
            var appNotification = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Message)
                .BuildNotification();
            _manager.Show(appNotification);
        }
        catch
        {
            // Notifications are optional presentation and must never affect session lifecycle handling.
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        _manager.NotificationInvoked -= Manager_NotificationInvoked;
        try { _manager.Unregister(); } catch { }
        _registered = false;
    }

    private void Manager_NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => _activated();
}
