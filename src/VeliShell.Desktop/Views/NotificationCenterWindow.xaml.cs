using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class NotificationCenterWindow : Window
{
    private readonly NotificationCenterService _notifications;

    internal NotificationCenterWindow(NotificationCenterService notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        _notifications.Changed += NotificationsChanged;
        Loaded += (_, _) =>
        {
            RefreshItems();
            _notifications.MarkAllRead();
        };
        Deactivated += (_, _) => Close();
        Closed += (_, _) => _notifications.Changed -= NotificationsChanged;
    }

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner;
        Topmost = topmost;
        MenuPanelPlacement.PlaceBelow(anchor, this);
        Show();
        Activate();
    }

    private void NotificationsChanged()
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
        var items = _notifications.Snapshot();
        NotificationItems.Children.Clear();
        foreach (var notification in items)
            NotificationItems.Children.Add(CreateNotificationCard(notification));

        var hasItems = items.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        NotificationScroller.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = hasItems;
        MarkReadButton.IsEnabled = items.Any(notification => notification.IsUnread);
    }

    private UIElement CreateNotificationCard(VeliShellNotification notification)
    {
        var dismissSymbol = new VeliSymbol
        {
            Symbol = VeliSymbolKind.Close,
            Width = 11,
            Height = 11
        };
        dismissSymbol.SetResourceReference(ForegroundProperty, "TextPrimary");
        var dismiss = new Button
        {
            Content = dismissSymbol,
            Padding = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = L("Notifications.Dismiss")
        };
        dismiss.Click += (_, _) => _notifications.Dismiss(notification.Id);

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = notification.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 8, 0)
        });
        Grid.SetColumn(dismiss, 1);
        heading.Children.Add(dismiss);

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(new TextBlock
        {
            Text = notification.Message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 6, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = notification.CreatedAt.LocalDateTime.ToString("g", LocalizationService.Current.ActiveCulture),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 8, 0, 0)
        });

        var accent = notification.Kind switch
        {
            VeliShellNotificationKind.Success => "#FF35B96D",
            VeliShellNotificationKind.Warning => "#FFF0A234",
            VeliShellNotificationKind.Update => "#FF7B68EE",
            _ => "#FF399DEB"
        };
        return new Border
        {
            Background = (Brush)FindResource("CardSurfaceGradient"),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)),
            BorderThickness = new Thickness(notification.IsUnread ? 2 : 1, 1, 1, 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 10, 11),
            Margin = new Thickness(2, 4, 2, 7),
            Child = content
        };
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
    private void MarkAllRead_Click(object sender, RoutedEventArgs e) => _notifications.MarkAllRead();
    private void Clear_Click(object sender, RoutedEventArgs e) => _notifications.Clear();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
