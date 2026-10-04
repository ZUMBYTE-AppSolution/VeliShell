using System.IO;
using Windows.UI.Notifications.Management;

namespace VeliShell.Desktop.Services;

internal enum VeliShellNotificationKind
{
    Information,
    Success,
    Warning,
    Update
}

internal enum VeliShellNotificationSource
{
    VeliShell,
    Windows
}

internal enum WindowsNotificationAccessState
{
    Disabled,
    NotRequested,
    Requesting,
    Allowed,
    Denied,
    Revoked,
    Unsupported,
    Error
}

internal sealed record WindowsNotificationAccessInfo(
    WindowsNotificationAccessState State,
    bool AppEnabled,
    bool HasPackageIdentity,
    string? Diagnostic = null);

internal sealed record VeliShellNotification(
    Guid Id,
    string Key,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    VeliShellNotificationKind Kind,
    bool IsUnread,
    VeliShellNotificationSource Source,
    string AppDisplayName,
    uint? WindowsPlatformId = null,
    byte[]? AppLogo = null);

/// <summary>
/// Bounded notification model shared by VeliShell events and, after explicit
/// opt-in, a synchronized view of the current Windows toast notifications.
/// Windows access is never requested from the constructor or at startup.
/// </summary>
internal sealed class NotificationCenterService : IDisposable
{
    private const int MaximumNotifications = 50;
    private const int MaximumTitleLength = 120;
    private const int MaximumMessageLength = 800;
    private readonly object _gate = new();
    private readonly List<VeliShellNotification> _notifications = [];
    private readonly SemaphoreSlim _windowsSyncGate = new(1, 1);
    private readonly WindowsNotificationListenerBridge? _windows;
    private bool _windowsEnabled;
    private bool _disposed;
    private WindowsNotificationAccessInfo _windowsAccess;

    private NotificationCenterService()
    {
        if (WindowsNotificationListenerBridge.TryCreate(out _windows, out var diagnostic))
        {
            _windows!.Changed += WindowsNotificationsChanged;
            _windowsAccess = new WindowsNotificationAccessInfo(
                WindowsNotificationAccessState.Disabled,
                AppEnabled: false,
                WindowsNotificationListenerBridge.HasPackageIdentity);
        }
        else
        {
            _windowsAccess = new WindowsNotificationAccessInfo(
                WindowsNotificationAccessState.Unsupported,
                AppEnabled: false,
                WindowsNotificationListenerBridge.HasPackageIdentity,
                diagnostic);
        }
    }

    internal static NotificationCenterService Current { get; } = new();

    internal event Action? Changed;
    internal event Action? WindowsAccessChanged;

    internal WindowsNotificationAccessInfo WindowsAccess
    {
        get
        {
            lock (_gate) return _windowsAccess;
        }
    }

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
                notification.Source == VeliShellNotificationSource.VeliShell &&
                string.Equals(notification.Key, key, StringComparison.Ordinal));
            _notifications.Insert(0, new VeliShellNotification(
                Guid.NewGuid(), key, title, message, DateTimeOffset.Now, kind, true,
                VeliShellNotificationSource.VeliShell, "VeliShell"));
            TrimToLimit();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Checks an already persisted VeliShell opt-in. This never presents the
    /// Windows privacy prompt; only RequestWindowsAccessAsync may do that.
    /// </summary>
    internal async Task ConfigureWindowsNotificationsAsync(bool enabled)
    {
        ThrowIfDisposed();
        _windowsEnabled = enabled;
        if (!enabled)
        {
            DisableWindowsNotifications();
            return;
        }

        if (_windows is null)
        {
            SetWindowsAccess(WindowsNotificationAccessState.Unsupported, false,
                WindowsAccess.Diagnostic);
            return;
        }

        try
        {
            if (_windows.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                _windows.Unsubscribe();
                RemoveWindowsSnapshots();
                SetWindowsAccess(WindowsNotificationAccessState.Revoked, true);
                return;
            }

            _windows.Subscribe();
            SetWindowsAccess(WindowsNotificationAccessState.Allowed, true);
            await SyncWindowsNotificationsAsync(waitForTurn: true);
        }
        catch (Exception exception) when (IsWindowsNotificationException(exception))
        {
            HandleWindowsFailure(exception, enabled);
        }
    }

    /// <summary>
    /// Must be called from an explicit user action on the UI thread. It is the
    /// only path that calls Windows RequestAccessAsync.
    /// </summary>
    internal async Task<WindowsNotificationAccessInfo> RequestWindowsAccessAsync()
    {
        ThrowIfDisposed();
        if (_windows is null)
        {
            SetWindowsAccess(WindowsNotificationAccessState.Unsupported, false,
                WindowsAccess.Diagnostic);
            return WindowsAccess;
        }

        SetWindowsAccess(WindowsNotificationAccessState.Requesting, false);
        try
        {
            var access = await _windows.RequestAccessAsync();
            switch (access)
            {
                case UserNotificationListenerAccessStatus.Allowed:
                    _windowsEnabled = true;
                    _windows.Subscribe();
                    SetWindowsAccess(WindowsNotificationAccessState.Allowed, true);
                    await SyncWindowsNotificationsAsync(waitForTurn: true);
                    break;
                case UserNotificationListenerAccessStatus.Denied:
                    _windowsEnabled = false;
                    _windows.Unsubscribe();
                    RemoveWindowsSnapshots();
                    SetWindowsAccess(WindowsNotificationAccessState.Denied, false);
                    break;
                default:
                    _windowsEnabled = false;
                    _windows.Unsubscribe();
                    RemoveWindowsSnapshots();
                    SetWindowsAccess(WindowsNotificationAccessState.NotRequested, false);
                    break;
            }
        }
        catch (Exception exception) when (IsWindowsNotificationException(exception))
        {
            HandleWindowsFailure(exception, enabled: false);
        }
        return WindowsAccess;
    }

    internal void DisableWindowsNotifications()
    {
        if (_disposed) return;
        _windowsEnabled = false;
        _windows?.Unsubscribe();
        RemoveWindowsSnapshots();
        SetWindowsAccess(_windows is null
            ? WindowsNotificationAccessState.Unsupported
            : WindowsNotificationAccessState.Disabled, false, WindowsAccess.Diagnostic);
    }

    internal Task RefreshWindowsNotificationsAsync()
    {
        ThrowIfDisposed();
        return SyncWindowsNotificationsAsync(waitForTurn: true);
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
        VeliShellNotification? removed = null;
        lock (_gate)
        {
            var index = _notifications.FindIndex(notification => notification.Id == id);
            if (index >= 0)
            {
                removed = _notifications[index];
                _notifications.RemoveAt(index);
            }
        }
        if (removed is null) return;

        if (removed.WindowsPlatformId is { } platformId)
        {
            RemoveWindowsNotification(platformId);
            _ = SyncWindowsNotificationsAsync(waitForTurn: true);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Explicitly clears everything currently displayed by VeliShell. Windows
    /// toasts are removed one-by-one by displayed ID; ClearNotifications is
    /// deliberately not used, so hidden/unreadable system toasts are untouched.
    /// </summary>
    internal void Clear()
    {
        uint[] windowsIds;
        bool changed;
        lock (_gate)
        {
            changed = _notifications.Count > 0;
            windowsIds = _notifications
                .Where(notification => notification.WindowsPlatformId.HasValue)
                .Select(notification => notification.WindowsPlatformId!.Value)
                .Distinct()
                .ToArray();
            _notifications.Clear();
        }
        foreach (var platformId in windowsIds) RemoveWindowsNotification(platformId);
        if (windowsIds.Length > 0)
            _ = SyncWindowsNotificationsAsync(waitForTurn: true);
        if (changed) Changed?.Invoke();
    }

    private async Task SyncWindowsNotificationsAsync(bool waitForTurn = false)
    {
        if (!_windowsEnabled || _windows is null || _disposed) return;
        if (waitForTurn)
            await _windowsSyncGate.WaitAsync();
        else if (!await _windowsSyncGate.WaitAsync(0))
            return;
        try
        {
            if (!_windowsEnabled || _disposed) return;
            if (_windows.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                _windows.Unsubscribe();
                RemoveWindowsSnapshots();
                SetWindowsAccess(WindowsNotificationAccessState.Revoked, true);
                return;
            }

            var snapshots = await _windows.ReadAsync();
            if (_disposed) return;
            ApplyWindowsSnapshot(snapshots);
            SetWindowsAccess(WindowsNotificationAccessState.Allowed, true);
        }
        catch (Exception exception) when (IsWindowsNotificationException(exception))
        {
            HandleWindowsFailure(exception, enabled: true);
        }
        finally
        {
            _windowsSyncGate.Release();
        }
    }

    internal void ApplyWindowsSnapshot(IReadOnlyList<WindowsNotificationSnapshot> snapshots)
    {
        var changed = false;
        lock (_gate)
        {
            var incomingIds = snapshots.Select(snapshot => snapshot.PlatformId).ToHashSet();
            changed |= _notifications.RemoveAll(notification =>
                notification.Source == VeliShellNotificationSource.Windows &&
                notification.WindowsPlatformId is { } platformId &&
                !incomingIds.Contains(platformId)) > 0;

            foreach (var snapshot in snapshots)
            {
                var index = _notifications.FindIndex(notification =>
                    notification.Source == VeliShellNotificationSource.Windows &&
                    notification.WindowsPlatformId == snapshot.PlatformId);
                var existing = index >= 0 ? _notifications[index] : null;
                var title = Normalize(snapshot.Title, MaximumTitleLength);
                var message = Normalize(snapshot.Message, MaximumMessageLength);
                var appName = Normalize(snapshot.AppDisplayName, MaximumTitleLength);
                if (appName.Length == 0) appName = "Windows";
                if (title.Length == 0) title = appName;

                var contentChanged = existing is null ||
                    !string.Equals(existing.Title, title, StringComparison.Ordinal) ||
                    !string.Equals(existing.Message, message, StringComparison.Ordinal) ||
                    !string.Equals(existing.AppDisplayName, appName, StringComparison.Ordinal) ||
                    existing.CreatedAt != snapshot.CreatedAt ||
                    !LogoEquals(existing.AppLogo, snapshot.AppLogo);
                if (!contentChanged) continue;

                var converted = new VeliShellNotification(
                    existing?.Id ?? Guid.NewGuid(),
                    $"windows-{snapshot.PlatformId}",
                    title,
                    message,
                    snapshot.CreatedAt,
                    VeliShellNotificationKind.Information,
                    IsUnread: existing?.IsUnread != false,
                    VeliShellNotificationSource.Windows,
                    appName,
                    snapshot.PlatformId,
                    snapshot.AppLogo);
                if (index >= 0) _notifications[index] = converted;
                else _notifications.Add(converted);
                changed = true;
            }

            if (changed)
            {
                _notifications.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
                TrimToLimit();
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void WindowsNotificationsChanged() => _ = SyncWindowsNotificationsAsync();

    private void RemoveWindowsNotification(uint platformId)
    {
        if (!_windowsEnabled || _windows is null ||
            WindowsAccess.State != WindowsNotificationAccessState.Allowed) return;
        try
        {
            _windows.Remove(platformId);
        }
        catch (Exception exception) when (IsWindowsNotificationException(exception))
        {
            App.Log("A displayed Windows notification could not be dismissed", exception);
        }
    }

    private void RemoveWindowsSnapshots()
    {
        bool changed;
        lock (_gate)
            changed = _notifications.RemoveAll(notification =>
                notification.Source == VeliShellNotificationSource.Windows) > 0;
        if (changed) Changed?.Invoke();
    }

    private void HandleWindowsFailure(Exception exception, bool enabled)
    {
        if (_disposed) return;
        App.Log("Windows notification synchronization failed", exception);
        _windows?.Unsubscribe();
        RemoveWindowsSnapshots();
        SetWindowsAccess(WindowsNotificationAccessState.Error, enabled,
            $"{exception.GetType().Name} (0x{exception.HResult:X8})");
    }

    private void SetWindowsAccess(
        WindowsNotificationAccessState state,
        bool enabled,
        string? diagnostic = null)
    {
        var updated = new WindowsNotificationAccessInfo(
            state,
            enabled,
            WindowsNotificationListenerBridge.HasPackageIdentity,
            diagnostic);
        bool changed;
        lock (_gate)
        {
            changed = _windowsAccess != updated;
            _windowsAccess = updated;
        }
        if (changed) WindowsAccessChanged?.Invoke();
    }

    private void TrimToLimit()
    {
        if (_notifications.Count > MaximumNotifications)
            _notifications.RemoveRange(MaximumNotifications, _notifications.Count - MaximumNotifications);
    }

    private static bool LogoEquals(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right) ||
        left is not null && right is not null && left.AsSpan().SequenceEqual(right);

    private static bool IsWindowsNotificationException(Exception exception) =>
        exception is System.Runtime.InteropServices.COMException or InvalidOperationException
            or UnauthorizedAccessException or IOException or ObjectDisposedException;

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ').Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowsEnabled = false;
        if (_windows is not null)
        {
            _windows.Changed -= WindowsNotificationsChanged;
            _windows.Dispose();
        }
        // A WinRT read already in flight may still release this gate. It is a
        // tiny process-lifetime object, so deliberately do not dispose it here.
    }
}
