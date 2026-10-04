using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class FolderPopoverWindow : Window
{
    private readonly string _rootPath;
    private string _currentPath;
    private CancellationTokenSource? _navigation;
    private int _navigationGeneration;

    internal FolderPopoverWindow(string rootPath)
    {
        if (!FolderBrowserService.TryCreateRoot(rootPath, out _rootPath))
            throw new ArgumentException("The folder pin does not resolve to a readable directory.", nameof(rootPath));
        _currentPath = _rootPath;
        InitializeComponent();
        Loaded += async (_, _) => await NavigateAsync(_rootPath);
        Deactivated += (_, _) => Close();
        Closed += (_, _) => CancelNavigation();
    }

    internal static bool CanOpen(string? target) => FolderBrowserService.TryCreateRoot(target, out _);

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner;
        Topmost = topmost;
        PlaceAbove(anchor);
        Show();
        Activate();
    }

    private async Task NavigateAsync(string candidate)
    {
        if (!FolderBrowserService.TryResolveLocation(_rootPath, candidate, out var location))
        {
            ShowState("FolderPopover.UnavailableTitle", "FolderPopover.Unavailable");
            return;
        }

        CancelNavigation();
        var cancellation = new CancellationTokenSource();
        _navigation = cancellation;
        var generation = ++_navigationGeneration;
        _currentPath = location;
        UpdateNavigationChrome();
        ItemsPanel.Children.Clear();
        ItemsScroller.Visibility = Visibility.Collapsed;
        ShowState("FolderPopover.Loading", "FolderPopover.LoadingDescription");
        StatusText.Text = L("FolderPopover.Loading");

        FolderBrowserResult result;
        try
        {
            result = await FolderBrowserService.EnumerateAsync(_rootPath, location, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellation.IsCancellationRequested || generation != _navigationGeneration || !IsLoaded) return;

        if (result.ErrorKey is { } errorKey)
        {
            StatusText.Text = L(errorKey);
            ShowState("FolderPopover.UnavailableTitle", errorKey);
            return;
        }

        foreach (var entry in result.Entries) ItemsPanel.Children.Add(CreateEntryButton(entry));
        var hasEntries = result.Entries.Count > 0;
        ItemsScroller.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        if (!hasEntries)
        {
            StateTitle.Text = L("FolderPopover.EmptyTitle");
            StateDescription.Text = L("FolderPopover.EmptyDescription");
            StateSymbol.Symbol = VeliSymbolKind.Folder;
        }
        StatusText.Text = result.IsTruncated
            ? string.Format(LocalizationService.Current.ActiveCulture,
                L("FolderPopover.Truncated"), FolderBrowserService.MaximumEntries)
            : string.Format(LocalizationService.Current.ActiveCulture,
                L("FolderPopover.Count"), result.Entries.Count);
    }

    private Button CreateEntryButton(FolderBrowserEntry entry)
    {
        var icon = new VeliSymbol
        {
            Symbol = entry.IsDirectory ? VeliSymbolKind.Folder : VeliSymbolKind.Document,
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(ForegroundProperty,
            entry.IsDirectory ? "Accent" : "TextSecondary");

        var labels = new StackPanel { Margin = new Thickness(10, 0, 8, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontWeight = entry.IsDirectory ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = L(!entry.IsOpenable
                ? "FolderPopover.LinkBlocked"
                : entry.IsDirectory ? "FolderPopover.Folder" : "FolderPopover.File"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        if (entry.IsDirectory && entry.IsNavigable)
        {
            var chevron = new VeliSymbol
            {
                Symbol = VeliSymbolKind.ChevronRight,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0)
            };
            chevron.SetResourceReference(ForegroundProperty, "TextSecondary");
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var button = new Button
        {
            Content = grid,
            Style = (Style)FindResource("GlassTileButton"),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsEnabled = entry.IsOpenable && (!entry.IsDirectory || entry.IsNavigable),
            ToolTip = entry.Name
        };
        AutomationProperties.SetName(button, entry.Name);
        button.Click += async (_, _) => await OpenEntryAsync(entry);
        return button;
    }

    private async Task OpenEntryAsync(FolderBrowserEntry entry)
    {
        if (!FolderBrowserService.CanOpenEntry(_rootPath, _currentPath, entry))
        {
            StatusText.Text = L("FolderPopover.Changed");
            await NavigateAsync(_currentPath);
            return;
        }

        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Path);
            return;
        }

        LaunchService.Open(entry.Path);
        Close();
    }

    private void UpdateNavigationChrome()
    {
        PathTitle.Text = DisplayName(_currentPath);
        BackButton.IsEnabled = !string.Equals(_rootPath, _currentPath, StringComparison.OrdinalIgnoreCase);
        BreadcrumbPanel.Children.Clear();

        AddBreadcrumb(DisplayName(_rootPath), _rootPath);
        var relative = Path.GetRelativePath(_rootPath, _currentPath);
        if (relative == ".") return;
        var cursor = _rootPath;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            AddBreadcrumbSeparator();
            cursor = Path.Combine(cursor, segment);
            AddBreadcrumb(segment, cursor);
        }
    }

    private void AddBreadcrumb(string label, string path)
    {
        var button = new Button
        {
            Content = label,
            Tag = path,
            Style = (Style)FindResource("GlassTileButton"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0),
            MaxWidth = 170,
            ToolTip = label
        };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await NavigateAsync((string)button.Tag);
        BreadcrumbPanel.Children.Add(button);
    }

    private void AddBreadcrumbSeparator()
    {
        var separator = new VeliSymbol
        {
            Symbol = VeliSymbolKind.ChevronRight,
            Width = 11,
            Height = 11,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        separator.SetResourceReference(ForegroundProperty, "TextSecondary");
        BreadcrumbPanel.Children.Add(separator);
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_currentPath);
        if (parent is not null && FolderBrowserService.IsWithinRoot(_rootPath, parent))
            await NavigateAsync(parent);
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!FolderBrowserService.TryResolveLocation(_rootPath, _currentPath, out var safeLocation)) return;
        LaunchService.Open(safeLocation);
        Close();
    }

    private void ShowState(string titleKey, string descriptionKey)
    {
        ItemsScroller.Visibility = Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;
        StateTitle.Text = L(titleKey);
        StateDescription.Text = L(descriptionKey);
        StateSymbol.Symbol = VeliSymbolKind.Folder;
    }

    private void CancelNavigation()
    {
        var cancellation = _navigation;
        _navigation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void PlaceAbove(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null) return;
        var anchorTopLeft = anchor.PointToScreen(new Point(0, 0));
        var anchorBottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var monitorPoint = new NativeMethods.Point
        {
            X = checked((int)Math.Round((anchorTopLeft.X + anchorBottomRight.X) / 2)),
            Y = checked((int)Math.Round(anchorTopLeft.Y))
        };
        var monitor = NativeMethods.MonitorFromPoint(monitorPoint, 2);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var workTopLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var workBottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        var anchorDipTopLeft = fromDevice.Transform(anchorTopLeft);
        var anchorDipBottomRight = fromDevice.Transform(anchorBottomRight);
        var availableWidth = Math.Max(MinWidth, workBottomRight.X - workTopLeft.X - 20);
        var availableHeight = Math.Max(MinHeight, workBottomRight.Y - workTopLeft.Y - 20);
        Width = Math.Min(420, availableWidth);
        Height = Math.Min(480, availableHeight);
        Left = Math.Clamp(
            (anchorDipTopLeft.X + anchorDipBottomRight.X - Width) / 2,
            workTopLeft.X + 10,
            Math.Max(workTopLeft.X + 10, workBottomRight.X - Width - 10));
        Top = Math.Clamp(
            anchorDipTopLeft.Y - Height - 10,
            workTopLeft.Y + 10,
            Math.Max(workTopLeft.Y + 10, workBottomRight.Y - Height - 10));
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && BackButton.IsEnabled)
        {
            var parent = Path.GetDirectoryName(_currentPath);
            if (parent is not null && FolderBrowserService.IsWithinRoot(_rootPath, parent))
                await NavigateAsync(parent);
            e.Handled = true;
        }
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
}
