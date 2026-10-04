namespace VeliShell.Desktop.Services;

internal enum VeliShellNotificationKind
{
    Information,
    Success,
    Warning,
    Update
}

internal sealed record VeliShellNotification(
    Guid Id,
    string Key,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    VeliShellNotificationKind Kind,
    bool IsUnread);

/// <summary>
/// Small, process-local notification store for VeliShell events. It neither
/// reads Windows notifications nor registers a system notification listener.
/// </summary>
internal sealed class NotificationCenterService
{
    private const int MaximumNotifications = 50;
    private const int MaximumTitleLength = 120;
    private const int MaximumMessageLength = 800;
    private readonly object _gate = new();
    private readonly List<VeliShellNotification> _notifications = [];

    internal static NotificationCenterService Current { get; } = new();

    internal event Action? Changed;

    internal IReadOnlyList<VeliShellNotification> Snapshot()
    {
        lock (_gate) return _notifications.ToArray();
    }

    internal int UnreadCount
    {
        get
        {
            lock (_gate) return _notifications.Count(notification => notification.IsUnread);
        }
    }

    internal void Publish(
        string key,
        string title,
        string message,
        VeliShellNotificationKind kind = VeliShellNotificationKind.Information)
    {
        key = Normalize(key, 160);
        title = Normalize(title, MaximumTitleLength);
        message = Normalize(message, MaximumMessageLength);
        if (key.Length == 0 || title.Length == 0 || message.Length == 0) return;

        lock (_gate)
        {
            _notifications.RemoveAll(notification =>
                string.Equals(notification.Key, key, StringComparison.Ordinal));
            _notifications.Insert(0, new VeliShellNotification(
                Guid.NewGuid(), key, title, message, DateTimeOffset.Now, kind, true));
            if (_notifications.Count > MaximumNotifications)
                _notifications.RemoveRange(MaximumNotifications, _notifications.Count - MaximumNotifications);
        }
        Changed?.Invoke();
    }

    internal void MarkAllRead()
    {
        var changed = false;
        lock (_gate)
        {
            for (var index = 0; index < _notifications.Count; index++)
            {
                var notification = _notifications[index];
                if (!notification.IsUnread) continue;
                _notifications[index] = notification with { IsUnread = false };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    internal void Dismiss(Guid id)
    {
        bool changed;
        lock (_gate) changed = _notifications.RemoveAll(notification => notification.Id == id) > 0;
        if (changed) Changed?.Invoke();
    }

    internal void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _notifications.Count > 0;
            _notifications.Clear();
        }
        if (changed) Changed?.Invoke();
    }

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ').Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
