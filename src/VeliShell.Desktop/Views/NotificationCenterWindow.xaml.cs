using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class NotificationCenterWindow : Window, ITransientPanel
{
    private readonly NotificationCenterService _notifications;
    private bool _closing;
    private bool _requestingWindowsAccess;
    private readonly ShellBackdropHost _backdrop;
    private FrameworkElement? _anchor;

    internal NotificationCenterWindow(NotificationCenterService notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        _backdrop = new ShellBackdropHost(this, GlassPlate, 24);
        if (Application.Current is App app) app.Themes.Changed += RefreshBackdrop;
        _notifications.Changed += NotificationsChanged;
        _notifications.WindowsAccessChanged += WindowsAccessChanged;
        Loaded += async (_, _) =>
        {
            RefreshBackdrop();
            DateLabel.Text = DateTime.Now.ToString("dddd, d. MMMM",
                LocalizationService.Current.ActiveCulture);
            RefreshItems();
            _notifications.MarkAllRead();
            if (_notifications.WindowsAccess.State == WindowsNotificationAccessState.Allowed)
                await _notifications.RefreshWindowsNotificationsAsync();
        };
        Deactivated += (_, _) =>
        {
            if (!_requestingWindowsAccess)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseOnce));
        };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            _notifications.Changed -= NotificationsChanged;
            _notifications.WindowsAccessChanged -= WindowsAccessChanged;
            if (Application.Current is App app) app.Themes.Changed -= RefreshBackdrop;
            _backdrop.Dispose();
        };
    }

    private void RefreshBackdrop() => _backdrop.ApplyMaterial("GlassPanelAcrylicSurface", "GlassPanelSurface");

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        _anchor = anchor;
        Owner = owner;
        Topmost = topmost;
        MenuPanelPlacement.PlaceBelow(anchor, this);
        Show();
        Activate();
    }

    private void NotificationsChanged() => DispatchRefresh();
    private void WindowsAccessChanged() => DispatchRefresh();

    private void DispatchRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshItems);
            return;
        }
        RefreshItems();
    }

    private void RefreshItems()
    {
        if (!IsInitialized) return;
        var items = _notifications.Snapshot();
        var hadGroups = NotificationItems.Children.Count > 0;
        var expandedGroups = NotificationItems.Children.OfType<Expander>()
            .Where(expander => expander.IsExpanded &&
                expander.Tag is ValueTuple<VeliShellNotificationSource, string>)
            .Select(expander => ((VeliShellNotificationSource, string))expander.Tag)
            .ToHashSet();
        NotificationItems.Children.Clear();
        var groups = items
                     .GroupBy(notification => new
                     {
                         notification.Source,
                         notification.AppDisplayName
                     })
                     .OrderByDescending(group => group.Max(notification => notification.CreatedAt))
                     .ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var ordered = group.OrderByDescending(notification => notification.CreatedAt).ToList();
            NotificationItems.Children.Add(CreateNotificationGroup(
                group.Key.AppDisplayName,
                group.Key.Source,
                ordered, expandedGroups.Contains((group.Key.Source, group.Key.AppDisplayName)) ||
                         !hadGroups && index == 0));
        }

        var hasItems = items.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        NotificationScroller.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = hasItems;
        MarkReadButton.IsEnabled = items.Any(notification => notification.IsUnread);

        var access = _notifications.WindowsAccess;
        var showAccessWarning = access.State is WindowsNotificationAccessState.Disabled
            or WindowsNotificationAccessState.NotRequested or WindowsNotificationAccessState.Requesting
            or WindowsNotificationAccessState.Unsupported
            or WindowsNotificationAccessState.Denied or WindowsNotificationAccessState.Revoked
            or WindowsNotificationAccessState.Error;
        WindowsAccessBanner.Visibility = showAccessWarning ? Visibility.Visible : Visibility.Collapsed;
        ConnectWindowsButton.Visibility = access.HasPackageIdentity &&
                                          access.State != WindowsNotificationAccessState.Unsupported &&
                                          showAccessWarning
            ? Visibility.Visible : Visibility.Collapsed;
        WindowsAccessText.Text = L(access.State switch
        {
            WindowsNotificationAccessState.Disabled => "Notifications.AccessDisabled",
            WindowsNotificationAccessState.NotRequested => "Notifications.AccessNotRequested",
            WindowsNotificationAccessState.Requesting => "Notifications.AccessRequesting",
            WindowsNotificationAccessState.Unsupported => "Notifications.AccessUnsupported",
            WindowsNotificationAccessState.Denied => "Notifications.AccessDenied",
            WindowsNotificationAccessState.Revoked => "Notifications.AccessRevoked",
            WindowsNotificationAccessState.Error => "Notifications.AccessError",
            _ => "Notifications.WindowsUnavailable"
        });
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reflow));
    }

    private UIElement CreateNotificationGroup(
        string appName,
        VeliShellNotificationSource source,
        IReadOnlyList<VeliShellNotification> notifications,
        bool initiallyExpanded)
    {
        var section = new StackPanel { Margin = new Thickness(2, 0, 2, 5) };
        var header = new Grid { Margin = new Thickness(5, 0, 5, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CreateAppIcon(source, notifications.FirstOrDefault()?.AppLogo));

        var appLabel = new TextBlock
        {
            Text = appName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 8, 0)
        };
        Grid.SetColumn(appLabel, 1);
        header.Children.Add(appLabel);

        var countLabel = new TextBlock
        {
            Text = notifications.Count.ToString(LocalizationService.Current.ActiveCulture),
            FontSize = 10
        };
        countLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var count = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = countLabel
        };
        count.SetResourceReference(Border.BackgroundProperty, "GlassControlSurface");
        count.SetResourceReference(Border.BorderBrushProperty, "GlassCardStroke");
        Grid.SetColumn(count, 2);
        header.Children.Add(count);
        foreach (var notification in notifications)
            section.Children.Add(CreateNotificationCard(notification));
        var expander = new Expander
        {
            Style = (Style)FindResource("GlassExpander"),
            Header = header,
            Content = section,
            Tag = (source, appName),
            IsExpanded = initiallyExpanded,
            Margin = new Thickness(0, 2, 0, 5)
        };
        expander.Expanded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reflow));
        expander.Collapsed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reflow));
        return expander;
    }

    private void Reflow()
    {
        if (_closing || !IsLoaded) return;
        NotificationItems.Measure(new Size(Math.Max(200, Width - 42), double.PositiveInfinity));
        var available = Math.Max(320, SystemParameters.WorkArea.Height - 48);
        Height = Math.Clamp(182 + NotificationItems.DesiredSize.Height +
                            (WindowsAccessBanner.IsVisible
                                ? ConnectWindowsButton.IsVisible ? 88 : 52 : 0), 320, available);
        if (_anchor is not null) MenuPanelPlacement.PlaceBelow(_anchor, this);
    }

    private UIElement CreateAppIcon(VeliShellNotificationSource source, byte[]? logoBytes)
    {
        var sourceImage = TryLoadImage(logoBytes);
        if (sourceImage is null && source == VeliShellNotificationSource.VeliShell)
            sourceImage = IconService.For("velishell", "");

        if (sourceImage is not null)
        {
            var image = new Image
            {
                Source = sourceImage,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var imagePlate = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Child = image
            };
            imagePlate.SetResourceReference(Border.BackgroundProperty, "GlassIconSurface");
            return imagePlate;
        }

        var symbol = new VeliSymbol
        {
            Symbol = VeliSymbolKind.Notification,
            Width = 13,
            Height = 13
        };
        symbol.SetResourceReference(ForegroundProperty, "TextPrimary");
        var symbolPlate = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(7),
            Child = symbol
        };
        symbolPlate.SetResourceReference(Border.BackgroundProperty, "GlassControlSurface");
        return symbolPlate;
    }

    private UIElement CreateNotificationCard(VeliShellNotification notification)
    {
        var dismissSymbol = new VeliSymbol
        {
            Symbol = VeliSymbolKind.Close,
            Width = 10,
            Height = 10
        };
        dismissSymbol.SetResourceReference(ForegroundProperty, "TextPrimary");
        var dismiss = new Button
        {
            Content = dismissSymbol,
            Style = (Style)FindResource("GlassCircleButton"),
            Width = 27,
            Height = 27,
            Padding = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = L("Notifications.Dismiss")
        };
        AutomationProperties.SetName(dismiss, L("Notifications.Dismiss"));
        dismiss.Click += (_, _) => _notifications.Dismiss(notification.Id);

        var title = new TextBlock
        {
            Text = notification.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 8, 0)
        };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (notification.IsUnread)
        {
            var unread = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Margin = new Thickness(0, 5, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            unread.SetResourceReference(Border.BackgroundProperty, "GlassActiveSurface");
            heading.Children.Add(unread);
        }
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        Grid.SetColumn(dismiss, 2);
        heading.Children.Add(dismiss);

        var content = new StackPanel();
        content.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(notification.Message))
        {
            var message = new TextBlock
            {
                Text = notification.Message,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 78,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(notification.IsUnread ? 15 : 0, 5, 2, 0)
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            content.Children.Add(message);
        }
        var timestamp = new TextBlock
        {
            Text = FormatTimestamp(notification.CreatedAt),
            FontSize = 10.5,
            Margin = new Thickness(notification.IsUnread ? 15 : 0, 7, 0, 0)
        };
        timestamp.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        content.Children.Add(timestamp);

        return new Border
        {
            Style = (Style)FindResource("GlassListRow"),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 10, 9, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content
        };
    }

    private static ImageSource? TryLoadImage(byte[]? data)
    {
        if (data is null or { Length: 0 }) return null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        var local = value.LocalDateTime;
        return local.Date == DateTime.Today
            ? local.ToString("t", LocalizationService.Current.ActiveCulture)
            : local.ToString("g", LocalizationService.Current.ActiveCulture);
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
    private async void ConnectWindows_Click(object sender, RoutedEventArgs e)
    {
        _requestingWindowsAccess = true;
        ConnectWindowsButton.IsEnabled = false;
        try
        {
            var access = await _notifications.RequestWindowsAccessAsync();
            if (Application.Current is App app)
                app.UpdatePreferences(settings => settings.WindowsNotificationsEnabled =
                    access.State == WindowsNotificationAccessState.Allowed);
        }
        finally
        {
            _requestingWindowsAccess = false;
            if (!_closing) ConnectWindowsButton.IsEnabled = true;
            if (!_closing) DispatchRefresh();
        }
    }
    private void MarkAllRead_Click(object sender, RoutedEventArgs e) => _notifications.MarkAllRead();
    private void Clear_Click(object sender, RoutedEventArgs e) => _notifications.Clear();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseOnce();
    }
}
