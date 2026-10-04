using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace VeliShell.Desktop.Services;

internal sealed record WindowsNotificationSnapshot(
    uint PlatformId,
    string AppDisplayName,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    byte[]? AppLogo);

/// <summary>
/// Thin, read-only-by-default adapter around the documented Windows notification
/// listener. Access is never requested by this type on construction. The caller
/// must invoke <see cref="RequestAccessAsync"/> from an explicit UI action.
/// </summary>
internal sealed class WindowsNotificationListenerBridge : IDisposable
{
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumNotifications = 40;
    private const uint MaximumLogoBytes = 2 * 1024 * 1024;
    private readonly UserNotificationListener _listener;
    private bool _subscribed;
    private bool _disposed;

    private WindowsNotificationListenerBridge(UserNotificationListener listener)
    {
        _listener = listener;
    }

    internal event Action? Changed;

    internal static bool IsRuntimeSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) &&
        ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener");

    internal static bool HasPackageIdentity
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result is 0 or ErrorInsufficientBuffer;
        }
    }

    internal static bool TryCreate(
        out WindowsNotificationListenerBridge? bridge,
        out string? diagnostic)
    {
        bridge = null;
        diagnostic = null;
        if (!IsRuntimeSupported)
        {
            diagnostic = "The Windows notification listener API is unavailable on this Windows version.";
            return false;
        }

        // UserNotificationListener is capability-gated. The unpackaged
        // MSI/portable build has no package identity and therefore cannot
        // declare the required userNotificationListener capability. Do not
        // instantiate the WinRT singleton and expose a toggle that can never
        // be granted in this distribution.
        if (!HasPackageIdentity)
        {
            diagnostic = "Windows notification access requires a packaged VeliShell build with the userNotificationListener capability.";
            return false;
        }

        try
        {
            bridge = new WindowsNotificationListenerBridge(UserNotificationListener.Current);
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            diagnostic = $"{exception.GetType().Name} (0x{exception.HResult:X8})";
            return false;
        }
    }

    internal UserNotificationListenerAccessStatus GetAccessStatus()
    {
        ThrowIfDisposed();
        return _listener.GetAccessStatus();
    }

    internal async Task<UserNotificationListenerAccessStatus> RequestAccessAsync()
    {
        ThrowIfDisposed();
        return await _listener.RequestAccessAsync();
    }

    internal async Task<IReadOnlyList<WindowsNotificationSnapshot>> ReadAsync()
    {
        ThrowIfDisposed();
        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var result = new List<WindowsNotificationSnapshot>(notifications.Count);
        foreach (var notification in notifications
                     .OrderByDescending(notification => notification.CreationTime)
                     .Take(MaximumNotifications))
        {
            try
            {
                var snapshot = await ConvertAsync(notification);
                if (snapshot is not null) result.Add(snapshot);
            }
            catch (Exception exception) when (exception is COMException or IOException
                                              or InvalidOperationException or UnauthorizedAccessException)
            {
                // One malformed or concurrently removed toast must not hide all
                // other notifications. Its private content is never written to logs.
                App.Log("A Windows notification could not be read", exception);
            }
        }
        return result;
    }

    internal void Subscribe()
    {
        ThrowIfDisposed();
        if (_subscribed) return;
        _listener.NotificationChanged += ListenerOnNotificationChanged;
        _subscribed = true;
    }

    internal void Unsubscribe()
    {
        if (!_subscribed) return;
        _listener.NotificationChanged -= ListenerOnNotificationChanged;
        _subscribed = false;
    }

    internal void Remove(uint platformId)
    {
        ThrowIfDisposed();
        _listener.RemoveNotification(platformId);
    }

    private void ListenerOnNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args) => Changed?.Invoke();

    private static async Task<WindowsNotificationSnapshot?> ConvertAsync(UserNotification notification)
    {
        var appName = Normalize(notification.AppInfo?.DisplayInfo?.DisplayName, 120);
        if (appName.Length == 0) appName = "Windows";

        var binding = notification.Notification?.Visual?
            .GetBinding(KnownNotificationBindings.ToastGeneric);
        var textElements = binding?.GetTextElements()
            .Select(element => Normalize(element.Text, 800))
            .Where(text => text.Length > 0)
            .ToArray() ?? [];

        var title = textElements.FirstOrDefault() ?? string.Empty;
        var message = Normalize(string.Join(Environment.NewLine, textElements.Skip(1)), 800);
        if (title.Length == 0)
            title = appName;
        else if (message.Length == 0)
        {
            message = title;
            title = appName;
        }

        byte[]? logo = null;
        try
        {
            var logoReference = notification.AppInfo?.DisplayInfo?.GetLogo(new Size(48, 48));
            if (logoReference is not null) logo = await ReadLogoAsync(logoReference);
        }
        catch (Exception exception) when (exception is COMException or IOException
                                          or InvalidOperationException or UnauthorizedAccessException)
        {
            App.Log("A Windows notification app logo could not be read", exception);
        }

        return new WindowsNotificationSnapshot(
            notification.Id,
            appName,
            Normalize(title, 120),
            message,
            notification.CreationTime,
            logo);
    }

    private static async Task<byte[]?> ReadLogoAsync(RandomAccessStreamReference reference)
    {
        using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
        if (stream.Size is 0 or > MaximumLogoBytes) return null;
        var length = checked((uint)stream.Size);
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var loaded = await reader.LoadAsync(length);
        if (loaded != length) return null;
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ').Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        Unsubscribe();
        _disposed = true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        char[]? packageFullName);
}
