using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

/// <summary>An optional app launcher; the Windows Start menu remains untouched.</summary>
public partial class StartLauncherWindow : Window, ITransientPanel
{
    private readonly App _app;
    private readonly ShellBackdropHost _backdrop;
    private readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private IReadOnlyList<SearchEntry> _catalog = [];
    private SearchEntry[] _filtered = [];
    private bool _closed, _closing, _dragging, _allApps;
    private Point _dragStart;
    private SearchEntry? _pressedApp;
    private ContextMenu? _context;

    internal StartLauncherWindow(App app)
    {
        _app = app;
        _catalog = app.ProgramIndex.Snapshot;
        InitializeComponent();
        _backdrop = new ShellBackdropHost(this, GlassPlate, 24);
        UserLabel.Text = Environment.UserName;
        CreateListTemplate();
        foreach (var action in StartSessionActions.Commands)
        {
            var button = new Button { Tag = action.Id, Content = L(action.LabelKey),
                Style = (Style)FindResource("FolderAppButton"), HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 2, 0, 2) };
            button.Click += SessionAction_Click;
            PowerActionsPanel.Children.Add(button);
        }
        RenderPinned();
        Render();
        app.ProgramIndex.Changed += IndexChanged;
        app.PreferencesChanged += PreferencesChanged;
        if (app.Themes is not null) app.Themes.Changed += RefreshBackdrop;
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); Render(); };
        Loaded += (_, _) => { RefreshBackdrop(); QueryBox.Focus(); };
        Deactivated += (_, _) => QueueCloseIfInactive();
        PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (PowerPanel.Visibility == Visibility.Visible && !Inside(args.OriginalSource, PowerPanel) && !Inside(args.OriginalSource, PowerButton))
                PowerPanel.Visibility = Visibility.Collapsed;
        };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            _closed = _closing = true;
            _queryTimer.Stop();
            app.ProgramIndex.Changed -= IndexChanged;
            app.PreferencesChanged -= PreferencesChanged;
            if (app.Themes is not null) app.Themes.Changed -= RefreshBackdrop;
            if (_context is not null) _context.IsOpen = false;
            _backdrop.Dispose();
        };
    }

    private void RefreshBackdrop()
    {
        if (_closing || _closed) return;
        if (!IsLoaded || !IsVisible) { GlassPlate.SetResourceReference(Border.BackgroundProperty, "GlassPanelSurface"); return; }
        _backdrop.ApplyMaterial("GlassPanelAcrylicSurface", "GlassPanelSurface");
    }

    private void PreferencesChanged()
    {
        if (_closing || _closed) return;
        CreateListTemplate(); RenderPinned(); Render(); RefreshBackdrop();
    }

    private void QueueCloseIfInactive() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    {
        if (!_closed && !_closing && !IsActive && !_dragging && _context?.IsOpen != true) CloseOnce();
    }));

    public void CloseOnce()
    {
        if (_closing || _closed) return;
        _closing = true;
        Close();
    }

    internal void ShowAbove(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner; Topmost = topmost;
        PlaceAbove(anchor); Show(); Activate(); QueryBox.Focus();
    }

    private void PlaceAbove(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null) return;
        var topLeft = anchor.PointToScreen(new Point());
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point
        {
            X = checked((int)Math.Round((topLeft.X + bottomRight.X) / 2)),
            Y = checked((int)Math.Round(topLeft.Y))
        }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var work = new Rect(toDip.Transform(new Point(info.Work.Left, info.Work.Top)), toDip.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        var bounds = CalculateBounds(new Rect(toDip.Transform(topLeft), toDip.Transform(bottomRight)), work, new Size(640, 670));
        MinWidth = Math.Min(MinWidth, bounds.Width); MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
    }

    private static Rect CalculateBounds(Rect anchor, Rect workArea, Size desired)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 ||
            !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            desired.Width <= 0 || desired.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        const double margin = 10;
        var width = Math.Min(desired.Width, Math.Max(1, workArea.Width - margin * 2));
        var height = Math.Min(desired.Height, Math.Max(1, workArea.Height - margin * 2));
        var minLeft = workArea.Left + Math.Min(margin, Math.Max(0, workArea.Width - width));
        var maxLeft = Math.Max(minLeft, workArea.Right - width - margin);
        var minTop = workArea.Top + Math.Min(margin, Math.Max(0, workArea.Height - height));
        var maxTop = Math.Max(minTop, workArea.Bottom - height - margin);
        return new Rect(Math.Clamp(anchor.Left + (anchor.Width - width) / 2, minLeft, maxLeft),
            Math.Clamp(anchor.Top - height - margin, minTop, maxTop), width, height);
    }

    private void IndexChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing || _closed) return;
        _catalog = _app.ProgramIndex.Snapshot; RenderPinned(); Render();
    });

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AppsList is null) return;
        QueryHint.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _queryTimer.Stop(); _queryTimer.Start();
    }

    private void Render()
    {
        if (AppsList is null) return;
        var query = QueryBox.Text.Trim();
        var list = _allApps || query.Length > 0;
        QueryHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PinnedSection.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        AppsList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        AllAppsButton.Content = L(list ? "Start.Back" : "Start.AllApps");
        ListHeading.Text = L(query.Length > 0 ? "Start.Results" : list ? "Start.AllApps" : "Start.Pinned");
        _filtered = _catalog.Where(entry => entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => query.Length > 0 && entry.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        AppsList.ItemsSource = list ? _filtered : Array.Empty<SearchEntry>();
        ResultCount.Text = list ? _filtered.Length.ToString(LocalizationService.Current.ActiveCulture) : "";
        EmptyResults.Text = L(_catalog.Count == 0 && !_app.ProgramIndex.IsReady ? "Start.Loading" : "Start.Empty");
        EmptyResults.Visibility = list && _filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderPinned()
    {
        PinnedPanel.Children.Clear();
        foreach (var pin in _app.Preferences.Pins.Take(18))
        {
            var name = LocalizationService.Current.DisplayPinName(pin);
            var entry = new SearchEntry(name, pin.Target, "pin");
            var icon = new DeferredAppIcon(44) { Target = pin.Target, Icon = pin.Icon,
                IconId = pin.Kind == PinKind.VirtualFolder ? "virtual-folder" : pin.Id,
                IconStyle = _app.Preferences.IconStyle };
            var button = new Button
            {
                Style = (Style)FindResource("FolderAppButton"), Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, Width = 88, Height = 90, Padding = new Thickness(4),
                Margin = new Thickness(4), ToolTip = name,
                Content = new StackPanel { Children =
                {
                    icon,
                    new TextBlock { Text = name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 7, 0, 0), MaxWidth = 80, Foreground = (Brush)FindResource("TextPrimary") }
                }}
            };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) =>
            {
                if (pin.Kind == PinKind.VirtualFolder) { CloseOnce(); _app.Dock.OpenVirtualFolder(pin); }
                else Open(pin.Target);
            };
            WireDrag(button, entry);
            PinnedPanel.Children.Add(button);
        }
        PinnedEmpty.Visibility = PinnedPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsPanel.Children.Clear();
        var pinnedTargets = _app.Preferences.Pins.Select(pin => pin.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _catalog.Where(entry => !pinnedTargets.Contains(entry.Target)).DistinctBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).Take(6))
        {
            var content = new DockPanel();
            var icon = new DeferredAppIcon(30) { Target = entry.Target, IconStyle = _app.Preferences.IconStyle, Margin = new Thickness(0,0,10,0) };
            DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Style = (Style)FindResource("FolderAppButton"), Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8), Margin = new Thickness(2), Height = 48, ToolTip = entry.Name };
            AutomationProperties.SetName(button, entry.Name);
            button.Click += (_, _) => Open(entry.Target); WireDrag(button, entry); SuggestionsPanel.Children.Add(button);
        }
        SuggestionsHeading.Visibility = SuggestionsPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CreateListTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(DeferredAppIcon));
        icon.SetBinding(DeferredAppIcon.TargetProperty, new Binding(nameof(SearchEntry.Target)));
        icon.SetValue(DeferredAppIcon.IconStyleProperty, _app.Preferences.IconStyle);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        row.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SearchEntry.Name)));
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(TextBlock.FontSizeProperty, 13d);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        row.AppendChild(label);
        AppsList.ItemTemplate = new DataTemplate { VisualTree = row };
    }

    private void AllApps_Click(object sender, RoutedEventArgs e)
    {
        PowerPanel.Visibility = Visibility.Collapsed;
        if (_allApps || QueryBox.Text.Length > 0) { _allApps = false; QueryBox.Text = ""; }
        else _allApps = true;
        Render();
    }

    private void Power_Click(object sender, RoutedEventArgs e) =>
        PowerPanel.Visibility = PowerPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void SessionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var owner = Owner;
        CloseOnce();
        await StartSessionActions.ExecuteAsync(id, command =>
        {
            var label = L(command.LabelKey).TrimEnd(' ', '…');
            var question = string.Format(LocalizationService.Current.ActiveCulture, L("MenuBar.SystemActionConfirm"), label);
            return owner is { IsVisible: true }
                ? MessageBox.Show(owner, question, L("Product.Name"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes
                : MessageBox.Show(question, L("Product.Name"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }, _app.PerformSessionActionAsync, () =>
        {
            if (!NativeMethods.LockWorkStation())
                MessageBox.Show(L("MenuBar.SystemActionFailed"), L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void WireDrag(Button button, SearchEntry entry)
    {
        Point start = default;
        button.PreviewMouseLeftButtonDown += (_, args) => start = args.GetPosition(button);
        button.PreviewMouseMove += (_, args) =>
        {
            if (_dragging || args.LeftButton != MouseButtonState.Pressed) return;
            if (FarEnough(start, args.GetPosition(button))) DragEntry(button, entry);
        };
    }

    private static bool FarEnough(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void DragEntry(DependencyObject source, SearchEntry entry)
    {
        if (!File.Exists(entry.Target)) return;
        try { _dragging = true; DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, new[] { entry.Target }), DragDropEffects.Copy); }
        finally { _dragging = false; _pressedApp = null; CloseOnce(); }
    }

    private static SearchEntry? EntryAt(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ListBoxItem { Content: SearchEntry entry }) return entry;
        return null;
    }

    private static bool Inside(object? source, DependencyObject ancestor)
    {
        for (var current = source as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void AppList_MouseDown(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(AppsList); _pressedApp = EntryAt(e.OriginalSource); }
    private void AppList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging && e.LeftButton == MouseButtonState.Pressed && _pressedApp is { } entry && FarEnough(_dragStart, e.GetPosition(AppsList))) DragEntry(AppsList, entry);
    }
    private void AppList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedApp; _pressedApp = null;
        if (!_dragging && pressed is not null && pressed == EntryAt(e.OriginalSource) && !FarEnough(_dragStart, e.GetPosition(AppsList))) Open(pressed.Target);
    }
    private void AppList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryAt(e.OriginalSource) is not { } entry) return;
        e.Handled = true;
        _context = new ContextMenu { PlacementTarget = AppsList };
        var pin = new MenuItem { Header = L("Start.PinToDock") };
        pin.Click += (_, _) => _app.Dock.AddPaths([entry.Target]);
        _context.Items.Add(pin);
        _context.Closed += (_, _) => QueueCloseIfInactive();
        _context.IsOpen = true;
    }

    private void Open(string target) { CloseOnce(); LaunchService.Open(target); }
    private void Documents_Click(object sender, RoutedEventArgs e) => Open("shell:Personal");
    private void Downloads_Click(object sender, RoutedEventArgs e) => Open("shell:Downloads");
    private void Explorer_Click(object sender, RoutedEventArgs e) => Open("explorer.exe");
    private void Settings_Click(object sender, RoutedEventArgs e) { CloseOnce(); _app.ShowPreferences(); }
    private void WindowsMenu_Click(object sender, RoutedEventArgs e) { CloseOnce(); WindowsStartService.Open(); }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PowerPanel.Visibility == Visibility.Visible) { PowerPanel.Visibility = Visibility.Collapsed; PowerButton.Focus(); }
            else if (QueryBox.Text.Length > 0) QueryBox.Clear();
            else if (_allApps) { _allApps = false; Render(); }
            else CloseOnce();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && (Keyboard.FocusedElement == QueryBox || AppsList.IsKeyboardFocusWithin))
        {
            if (!_allApps && QueryBox.Text.Length == 0) { _allApps = true; Render(); }
            if (_filtered.Length > 0)
            {
                AppsList.SelectedIndex = Math.Clamp(AppsList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _filtered.Length - 1);
                AppsList.ScrollIntoView(AppsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.FocusedElement == QueryBox || AppsList.IsKeyboardFocusWithin))
        {
            var selected = AppsList.SelectedItem as SearchEntry;
            _queryTimer.Stop(); Render();
            var entry = selected is not null && _filtered.Contains(selected) ? selected : _filtered.FirstOrDefault();
            if (entry is not null) Open(entry.Target);
            e.Handled = true;
        }
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
}
