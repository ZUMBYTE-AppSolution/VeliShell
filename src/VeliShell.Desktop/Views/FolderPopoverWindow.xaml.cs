using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;
using VeliShell.Core;

namespace VeliShell.Desktop.Views;

public partial class FolderPopoverWindow : Window, ITransientPanel
{
    private readonly string _rootPath;
    private FolderDisplayMode _mode;
    private readonly App? _app;
    private readonly string? _virtualFolderId;
    private string _currentPath;
    private CancellationTokenSource? _navigation;
    private int _navigationGeneration;
    private readonly List<Func<Button>> _pageItems = [];
    private int _pageIndex;
    private Point? _touchStart;
    private bool _closing;

    internal FolderPopoverWindow(string rootPath, FolderDisplayMode mode = FolderDisplayMode.List)
    {
        if (!FolderBrowserService.TryCreateRoot(rootPath, out _rootPath))
            throw new ArgumentException("The folder pin does not resolve to a readable directory.", nameof(rootPath));
        _currentPath = _rootPath;
        _mode = mode;
        InitializeComponent();
        RegisterCloseGuard();
        Loaded += async (_, _) => await NavigateAsync(_rootPath);
        Closed += (_, _) => CancelNavigation();
    }

    internal FolderPopoverWindow(App app, Pin folder)
    {
        if (folder.Kind != PinKind.VirtualFolder)
            throw new ArgumentException("A virtual folder pin is required.", nameof(folder));
        _app = app;
        _virtualFolderId = folder.Id;
        _rootPath = "";
        _currentPath = "";
        _mode = folder.FolderMode is FolderDisplayMode.CompactAppLauncher
            ? FolderDisplayMode.CompactAppLauncher : FolderDisplayMode.AppLauncher;
        InitializeComponent();
        RegisterCloseGuard();
        BackButton.Visibility = Visibility.Collapsed;
        BreadcrumbPanel.Visibility = Visibility.Collapsed;
        OpenExplorerButton.Visibility = Visibility.Collapsed;
        app.PreferencesChanged += RenderVirtual;
        Loaded += (_, _) => RenderVirtual();
        Closed += (_, _) => app.PreferencesChanged -= RenderVirtual;
    }

    private void RegisterCloseGuard()
    {
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseOnce));
        Closing += (_, _) => _closing = true;
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
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
        _pageIndex = 0;
        _pageItems.Clear();
        PageControls.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        UpdateNavigationChrome();
        ItemsPanel.Children.Clear();
        GridItemsPanel.Children.Clear();
        ItemsPanel.Visibility = _mode == FolderDisplayMode.List ? Visibility.Visible : Visibility.Collapsed;
        GridItemsPanel.Visibility = _mode == FolderDisplayMode.List ? Visibility.Collapsed : Visibility.Visible;
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

        var entries = IsPagedMode
            ? result.Entries.Where(entry => entry.IsDirectory || IsLaunchableApp(entry.Path)).ToArray()
            : result.Entries.ToArray();
        ConfigureGrid();
        foreach (var entry in entries)
        {
            if (_mode == FolderDisplayMode.List) ItemsPanel.Children.Add(CreateEntryButton(entry));
            else if (IsPagedMode) _pageItems.Add(() => CreateGridEntryButton(entry, paged: true));
            else GridItemsPanel.Children.Add(CreateGridEntryButton(entry, paged: false));
        }
        if (IsPagedMode) RenderPage();
        var hasEntries = entries.Length > 0;
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
                L("FolderPopover.Count"), entries.Length);
    }

    private static bool IsLaunchableApp(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    private void RenderVirtual()
    {
        if (!IsInitialized || _app is null || _virtualFolderId is null) return;
        var folder = _app.Preferences.Pins.FirstOrDefault(pin => pin.Kind == PinKind.VirtualFolder &&
            string.Equals(pin.Id, _virtualFolderId, StringComparison.OrdinalIgnoreCase));
        if (folder is null) { CloseOnce(); return; }
        PathTitle.Text = folder.Name;
        var newMode = folder.FolderMode is FolderDisplayMode.CompactAppLauncher
            ? FolderDisplayMode.CompactAppLauncher : FolderDisplayMode.AppLauncher;
        if (_mode != newMode) _pageIndex = 0;
        _mode = newMode;
        ItemsPanel.Visibility = Visibility.Collapsed;
        GridItemsPanel.Visibility = Visibility.Visible;
        GridItemsPanel.Children.Clear();
        _pageItems.Clear();
        ConfigureGrid();
        var entries = folder.VirtualItems ?? [];
        foreach (var entry in entries) _pageItems.Add(() => CreateVirtualEntryButton(entry));
        RenderPage();
        ItemsScroller.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0)
        {
            StateTitle.Text = L("FolderPopover.VirtualEmptyTitle");
            StateDescription.Text = L("FolderPopover.VirtualEmptyDescription");
            StateSymbol.Symbol = VeliSymbolKind.Apps;
        }
        StatusText.Text = string.Format(LocalizationService.Current.ActiveCulture,
            L("FolderPopover.Count"), entries.Count);
    }

    private bool IsPagedMode => _mode is FolderDisplayMode.AppLauncher or FolderDisplayMode.CompactAppLauncher;

    private void ConfigureGrid()
    {
        if (!IsPagedMode)
        {
            GridItemsPanel.Width = double.NaN;
            GridItemsPanel.ItemWidth = GridItemsPanel.ItemHeight = double.NaN;
            ItemsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            return;
        }
        var compact = _mode == FolderDisplayMode.CompactAppLauncher;
        GridItemsPanel.ItemWidth = compact ? 87 : 112;
        GridItemsPanel.ItemHeight = compact ? 80 : 104;
        GridItemsPanel.Width = (compact ? 4 : 3) * GridItemsPanel.ItemWidth;
        GridItemsPanel.HorizontalAlignment = HorizontalAlignment.Center;
        ItemsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void RenderPage()
    {
        GridItemsPanel.Children.Clear();
        var pageSize = _mode == FolderDisplayMode.CompactAppLauncher ? 16 : 9;
        var pages = Math.Max(1, (_pageItems.Count + pageSize - 1) / pageSize);
        _pageIndex = Math.Clamp(_pageIndex, 0, pages - 1);
        foreach (var create in _pageItems.Skip(_pageIndex * pageSize).Take(pageSize))
            GridItemsPanel.Children.Add(create());
        PageControls.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = pages > 1 ? Visibility.Collapsed : Visibility.Visible;
        PreviousPageButton.IsEnabled = _pageIndex > 0;
        NextPageButton.IsEnabled = _pageIndex < pages - 1;
        PageText.Text = string.Format(LocalizationService.Current.ActiveCulture,
            L("FolderPopover.PageStatus"), _pageIndex + 1, pages);
        ItemsScroller.ScrollToTop();
    }

    private void MovePage(int delta)
    {
        if (PageControls.Visibility != Visibility.Visible) return;
        _pageIndex += delta;
        RenderPage();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => MovePage(-1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PageControls.Visibility != Visibility.Visible) return;
        MovePage(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void Window_PreviewTouchDown(object sender, TouchEventArgs e) =>
        _touchStart = e.GetTouchPoint(this).Position;

    private void Window_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (_touchStart is not { } start) return;
        _touchStart = null;
        var end = e.GetTouchPoint(this).Position;
        var horizontal = end.X - start.X;
        if (PageControls.Visibility != Visibility.Visible ||
            Math.Abs(horizontal) < 45 || Math.Abs(horizontal) < Math.Abs(end.Y - start.Y) * 1.2) return;
        MovePage(horizontal < 0 ? 1 : -1);
        e.Handled = true;
    }

    private Button CreateVirtualEntryButton(VirtualFolderEntry entry)
    {
        var compact = _mode == FolderDisplayMode.CompactAppLauncher;
        var cellWidth = compact ? 87d : 112d;
        var cellHeight = compact ? 80d : 104d;
        var image = new AppIconSurface(IconService.For("app", entry.Target, entry.Icon), compact ? 40 : 56);
        var button = new Button
        {
            Content = new StackPanel
            {
                Children =
                {
                    image,
                    new TextBlock { Text = entry.Name, FontSize = compact ? 10.5 : 11.5, FontWeight = FontWeights.Medium,
                        TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = cellWidth - 8, Margin = new Thickness(0, compact ? 4 : 7, 0, 0) }
                }
            },
            Style = (Style)FindResource("FolderAppButton"),
            Width = cellWidth, Height = cellHeight, Padding = new Thickness(3),
            ToolTip = entry.Name
        };
        AutomationProperties.SetName(button, entry.Name);
        button.Click += (_, _) => { CloseOnce(); LaunchService.Open(entry.Target); };
        var menu = new ContextMenu();
        var toDock = new MenuItem { Header = L("FolderPopover.MoveToDock") };
        toDock.Click += (_, _) => _app?.UpdatePreferences(settings =>
            settings.RemoveFromVirtualFolder(_virtualFolderId!, entry.Id, moveToDock: true));
        menu.Items.Add(toDock);
        var remove = new MenuItem { Header = L("FolderPopover.RemoveVirtualItem") };
        remove.Click += (_, _) => _app?.UpdatePreferences(settings =>
            settings.RemoveFromVirtualFolder(_virtualFolderId!, entry.Id, moveToDock: false));
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        return button;
    }

    private void VirtualFolder_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            e.Effects = _virtualFolderId is not null &&
                        e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
                        paths.Take(Settings.MaximumVirtualFolderItems).Any(path =>
                            path is { Length: > 0 and <= 32767 } && File.Exists(path) && IsLaunchableApp(path))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException
                                        or IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log("Could not inspect a folder drop", exception);
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void VirtualFolder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_virtualFolderId is null || _app is null) return;
        string[] paths;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] values) return;
            paths = values;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException
                                        or IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log("Could not read a folder drop", exception);
            return;
        }
        var candidates = paths.Take(Settings.MaximumVirtualFolderItems)
            .Where(path => File.Exists(path) && IsLaunchableApp(path))
            .Select(LaunchService.PinFromPath).OfType<Pin>().ToArray();
        if (candidates.Length == 0) return;
        _app.UpdatePreferences(settings =>
        {
            foreach (var candidate in candidates)
                settings.AddToVirtualFolder(_virtualFolderId, candidate, removeDockPin: false);
        });
    }

    private Button CreateGridEntryButton(FolderBrowserEntry entry, bool paged)
    {
        var compact = paged && _mode == FolderDisplayMode.CompactAppLauncher;
        var cellWidth = compact ? 87d : paged ? 112d : 111d;
        var cellHeight = compact ? 80d : paged ? 104d : 102d;
        var image = new AppIconSurface(IconService.For("app", entry.Path),
            compact ? 40 : paged ? 56 : 44,
            entry.IsDirectory || !IsLaunchableApp(entry.Path));
        var label = new TextBlock
        {
            Text = entry.Name, FontSize = compact ? 10.5 : 11.5, FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = cellWidth - 8, Margin = new Thickness(0, compact ? 4 : 7, 0, 0)
        };
        var button = new Button
        {
            Content = new StackPanel { Children = { image, label } },
            Style = (Style)FindResource(paged ? "FolderAppButton" : "GlassTileButton"),
            Width = cellWidth, Height = cellHeight, Padding = new Thickness(paged ? 3 : 6),
            Margin = paged ? new Thickness(0) : new Thickness(0, 0, 7, 7),
            IsEnabled = entry.IsOpenable && (!entry.IsDirectory || entry.IsNavigable),
            ToolTip = entry.Name
        };
        AutomationProperties.SetName(button, entry.Name);
        button.Click += async (_, _) => await OpenEntryAsync(entry);
        return button;
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
        CloseOnce();
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
        CloseOnce();
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
            CloseOnce();
            e.Handled = true;
        }
        else if (PageControls.Visibility == Visibility.Visible && e.Key is Key.Left or Key.PageUp)
        {
            MovePage(-1);
            e.Handled = true;
        }
        else if (PageControls.Visibility == Visibility.Visible && e.Key is Key.Right or Key.PageDown)
        {
            MovePage(1);
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
