using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Models;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;
using Microsoft.Win32;

namespace VeliShell.Desktop.Views;

public partial class DockWindow : Window
{
    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);
    private const string DockPinFormat = "VeliShell.DockPin.v1";
    private const int MaximumDropItems = Settings.MaximumPins;
    private const int MaximumDropPathLength = 32767;
    private readonly App _app;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly List<DockTile> _tiles = [];
    private readonly Dictionary<string, long> _launchDeadlines = new(StringComparer.Ordinal);
    private List<NativeWindow> _windows = [];
    private HwndSource? _source;
    private nint _handle;
    private nint _previousForeground;
    private Point _dragStart;
    private DockTile? _dragSource;
    private int _dropInsertionIndex = -1;
    private long _suppressActivationUntil;
    private bool _refreshing, _closed, _hidden, _dragInProgress;
    private ContextMenu? _menu;
    private DragGhostWindow? _dragGhost;
    private string? _dragGhostKey;
    private bool _dragPayloadCached;
    private string _cachedDragPinId = "";
    private IReadOnlyList<string> _cachedDragPaths = Array.Empty<string>();
    private WindowThumbnailPreview? _windowPreview;
    private FolderPopoverWindow? _folderPopover;
    private FrameworkElement? _dropPlaceholder;
    private int _dropPlaceholderInsertionIndex = -1;
    private string? _dropPlaceholderKey;
    private bool _dropPlaceholderAddsWidth;
    private DockTile? _dropSourceTile;
    private double _baseDockWidth;
    internal bool TaskbarRecoveryHotkeyAvailable { get; private set; }

    public DockWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _app.PreferencesChanged += ApplyPreferences;
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => { ApplyPreferences(); _pollTimer.Start(); await RefreshWindows(); };
        _pollTimer.Tick += async (_, _) => await RefreshWindows();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_app.Preferences.AutoHide && !_dragInProgress && !IsMouseOver &&
                _menu?.IsOpen != true && _folderPopover?.IsVisible != true) SetHidden(true);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _pollTimer.Stop(); _hideTimer.Stop();
            _app.PreferencesChanged -= ApplyPreferences;
            _source?.RemoveHook(WindowHook);
            ClearDropSlot();
            CloseDragGhost();
            CloseWindowPreview();
            CloseFolderPopover();
            NativeMethods.UnregisterHotKey(_handle, 1);
            NativeMethods.UnregisterHotKey(_handle, 2);
            NativeMethods.UnregisterHotKey(_handle, 3);
            NativeMethods.UnregisterHotKey(_handle, 4);
            NativeMethods.UnregisterHotKey(_handle, 5);
            _app.RequestExit();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExToolWindow));
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowHook);
        var open = NativeMethods.RegisterHotKey(_handle, 1, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 0x56);
        var exit = NativeMethods.RegisterHotKey(_handle, 2, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x51);
        TaskbarRecoveryHotkeyAvailable = NativeMethods.RegisterHotKey(_handle, 3, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x7A);
        var winSpace = NativeMethods.RegisterHotKey(_handle, 4, NativeMethods.ModWin | NativeMethods.ModNoRepeat, 0x20);
        var fallbackSearch = NativeMethods.RegisterHotKey(_handle, 5,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 0x20);
        _app.SearchHotkeyStatus = L(winSpace ? "Search.WinSpaceReady" : fallbackSearch
            ? "Search.FallbackReady" : "Search.HotkeyUnavailable");
        _app.HotkeyStatus = open && exit && TaskbarRecoveryHotkeyAvailable
            ? L("Dock.HotkeysReady")
            : L("Dock.HotkeysUnavailable");
        if (!open || !exit || !TaskbarRecoveryHotkeyAvailable) App.Log(_app.HotkeyStatus);
        if (_app.Preferences.HideTaskbar)
            Dispatcher.BeginInvoke(new Action(async () =>
                await _app.SetTaskbarHiddenAsync(
                    true,
                    showError: false,
                    recoverExistingHiddenTaskbars: true)));
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: clicking the dock must not steal focus first.
        {
            _previousForeground = NativeMethods.GetForegroundWindow();
            handled = true;
            return 3; // MA_NOACTIVATE; the click is still delivered.
        }
        if (message == 0x0312)
        {
            if (wParam == 1) _app.ShowPreferences();
            if (wParam == 2) _app.RequestExit();
            if (wParam == 3) _app.RestoreTaskbarFromEmergencyHotkey();
            if (wParam is 4 or 5) _app.ShowSearch();
            handled = true;
        }
        if (message is 0x007E or 0x02E0 or 0x001A)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                if (_app.Preferences.HideTaskbar) _app.Taskbars.Reconcile();
                if (_app.Preferences.HideDesktopIcons) _app.DesktopIcons.ReconcileHidden();
                Rebuild();
                PositionDock();
            }));
        return 0;
    }

    private void ApplyPreferences()
    {
        if (_closed) return;
        Topmost = _app.Preferences.AlwaysOnTop;
        Rebuild();
        SetHidden(false);
        if (_app.Preferences.AutoHide && _folderPopover?.IsVisible != true) _hideTimer.Start();
    }

    private async Task RefreshWindows()
    {
        if (_refreshing || _closed || _dragInProgress || _dragPayloadCached) return;
        _refreshing = true;
        try
        {
            var windows = await Task.Run(WindowCatalog.Read);
            if (_closed || _dragInProgress || _dragPayloadCached) return;
            _windows = windows;
            if (_app.Preferences.HideTaskbar) _app.Taskbars.Reconcile();
            if (_app.Preferences.HideDesktopIcons) _app.DesktopIcons.ReconcileHidden();
            Rebuild();
            StopCompletedLaunches();
            var fullscreen = WindowCatalog.ForegroundIsFullscreenOnPrimary();
            Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
            PositionDock();
            // Polling is a fallback for system theme changes that don't raise a preference event.
            _app.Themes.Apply(_app.Preferences.Appearance);
        }
        catch (Exception ex) { App.Log("Window enumeration failed", ex); }
        finally { _refreshing = false; }
    }

    private void Rebuild()
    {
        var preferences = _app.Preferences;
        var items = new List<DockItem>();
        foreach (var pin in preferences.Pins)
            items.Add(new DockItem { Key = "pin:" + pin.Id, Name = LocalizationService.Current.DisplayPinName(pin), Target = pin.Target,
                IconId = pin.Id, Icon = pin.Icon, Attribution = OnlineIconService.TryGetAttribution(pin.Icon)?.Text,
                Pin = pin, Windows = _windows.Where(w => WindowCatalog.Matches(w, pin)).ToList() });
        if (preferences.ShowRunningApps)
        {
            var running = _windows.Where(w => !preferences.Pins.Any(p => WindowCatalog.Matches(w, p)))
                .GroupBy(w => string.IsNullOrEmpty(w.Executable) ? w.ProcessName : w.Executable, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in running)
            {
                var first = group.First();
                var runningId = Settings.RunningDockIconKey(first.Executable, first.ProcessName);
                var onlineIcon = preferences.GetDockIconOverride(runningId);
                items.Add(new DockItem { Key = runningId,
                    Name = first.ProcessName.StartsWith("pid-", StringComparison.Ordinal) ? first.Title : first.ProcessName,
                    Target = first.Executable,
                    Icon = onlineIcon,
                    Attribution = OnlineIconService.TryGetAttribution(onlineIcon)?.Text,
                    Windows = group.ToList() });
            }
        }
        var monitor = NativeMethods.PrimaryMonitor();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var capacity = DockMath.VisibleCapacity((monitor.Work.Right - monitor.Work.Left) / dpi, preferences.IconSize);
        var utilityCount = (preferences.ShowVeliShellDockItem ? 1 : 0) +
                           (preferences.ShowWindowsStartDockItem ? 1 : 0) + 1;
        if (items.Count + utilityCount > capacity)
        {
            var keep = Math.Max(0, capacity - utilityCount - 1); // reserve one slot for overflow
            var overflow = items.Skip(keep).ToList();
            items = items.Take(keep).ToList();
            items.Add(new DockItem { Key = "overflow", Name = L("Dock.MoreApps"), IconId = "overflow",
                Icon = preferences.GetDockIconOverride("overflow"), Overflow = overflow });
        }
        if (preferences.ShowWindowsStartDockItem)
            items.Insert(0, new DockItem { Key = "start", Name = L("Dock.WindowsStart"), IconId = "start",
                Icon = preferences.GetDockIconOverride("start") });
        if (preferences.ShowVeliShellDockItem)
            items.Add(new DockItem { Key = "velishell", Name = L("Dock.Settings"), IconId = "velishell",
                Icon = preferences.GetDockIconOverride("velishell") });
        var recycle = RecycleBinService.Query();
        var recycleName = recycle.Available
            ? recycle.ItemCount == 0 ? L("Dock.RecycleEmpty") : LF("Dock.RecycleCount", recycle.ItemCount)
            : L("Dock.RecycleOpen");
        var recycleIcon = recycle.FillState == RecycleBinFillState.Full ? "trash-full" : "trash";
        var recycleOverrideKey = recycle.FillState == RecycleBinFillState.Full ? "trash-full" : "trash-empty";
        items.Add(new DockItem { Key = "trash", Name = recycleName, IconId = recycleIcon,
            Icon = preferences.GetDockIconOverride(recycleOverrideKey), Target = "shell:RecycleBinFolder" });

        if (_tiles.Count == items.Count && _tiles.Select(t => t.Item.Key).SequenceEqual(items.Select(i => i.Key))
            && _tiles.All(t => Math.Abs(t.IconSize - preferences.IconSize) < 0.01))
        {
            for (var i = 0; i < items.Count; i++) _tiles[i].UpdateItem(items[i]);
            StopCompletedLaunches();
            return;
        }
        foreach (var tile in _tiles) tile.StopLaunchBounce();
        ItemsPanel.Children.Clear();
        _tiles.Clear();
        var previousKey = "";
        foreach (var item in items)
        {
            if (item.Key == "velishell" || previousKey == "start")
            {
                var divider = new Border { Width = 1, Height = preferences.IconSize - 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7,0,7,16) };
                divider.SetResourceReference(Border.BackgroundProperty, "Divider");
                ItemsPanel.Children.Add(divider);
            }
            var tile = new DockTile(item, preferences.IconSize);
            tile.Click += (_, _) =>
            {
                if (Environment.TickCount64 >= _suppressActivationUntil) ActivateItem(tile.Item, tile);
            };
            tile.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
            tile.PreviewMouseLeftButtonUp += Tile_PreviewMouseLeftButtonUp;
            tile.LostMouseCapture += Tile_LostMouseCapture;
            tile.PreviewMouseMove += Tile_PreviewMouseMove;
            tile.PreviewMouseRightButtonUp += (_, e) => { ShowItemMenu(tile); e.Handled = true; };
            ItemsPanel.Children.Add(tile);
            _tiles.Add(tile);
            if (_launchDeadlines.TryGetValue(item.Key, out var deadline) &&
                Environment.TickCount64 < deadline && item.Windows.Count == 0)
                tile.StartLaunchBounce(preferences.ReducedMotion);
            previousKey = item.Key;
        }
        _baseDockWidth = items.Count * (preferences.IconSize + 22) + 78 +
                         (preferences.ShowWindowsStartDockItem ? 16 : 0);
        Width = _baseDockWidth + (_dropPlaceholderAddsWidth ? preferences.IconSize + 22 : 0);
        Height = preferences.IconSize * 1.62 + 62;
        DockPlate.Height = preferences.IconSize + 32;
        PositionDock();
    }

    internal void RefreshOnlineIcons()
    {
        if (!_closed && !_dragInProgress && !_dragPayloadCached) Rebuild();
    }

    internal IReadOnlyList<Pin> GetConfigurableRunningApps()
    {
        var preferences = _app.Preferences;
        return _windows
            .Where(window => !preferences.Pins.Any(pin => WindowCatalog.Matches(window, pin)))
            .GroupBy(window => string.IsNullOrEmpty(window.Executable) ? window.ProcessName : window.Executable,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var name = first.ProcessName.StartsWith("pid-", StringComparison.Ordinal)
                    ? first.Title
                    : first.ProcessName;
                var iconKey = Settings.RunningDockIconKey(first.Executable, first.ProcessName);
                return new Pin(
                    iconKey,
                    name,
                    first.Executable,
                    first.ProcessName,
                    preferences.GetDockIconOverride(iconKey));
            })
            .OrderBy(pin => pin.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Settings.MaximumPins)
            .ToList();
    }

    private void PositionDock()
    {
        if (_handle == 0 || _closed) return;
        var monitor = NativeMethods.PrimaryMonitor();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(Width * dpi.DpiScaleX);
        var height = (int)Math.Round(Height * dpi.DpiScaleY);
        var area = _app.Taskbars.IsHidden ? monitor.Monitor : monitor.Work;
        NativeMethods.SetWindowPos(_handle, 0,
            area.Left + (area.Right - area.Left - width) / 2,
            area.Bottom - height, width, height, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    private void ActivateItem(DockItem item, FrameworkElement anchor)
    {
        if (item.Key == "start") { WindowsStartService.Open(); return; }
        if (item.Key == "velishell") { _app.ShowPreferences(); return; }
        if (item.Key == "overflow") { ShowOverflow(item, anchor); return; }
        if (item.Pin is not null && FolderPopoverWindow.CanOpen(item.Target))
        {
            ShowFolderPopover(item.Target, anchor);
            return;
        }
        var windows = item.Windows.Where(w => NativeMethods.IsWindow(w.Handle)).ToList();
        if (windows.Count > 0)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var index = windows.FindIndex(w => w.Handle == foreground);
            if (index < 0) index = windows.FindIndex(w => w.Handle == _previousForeground);
            var next = windows[(index + 1) % windows.Count];
            if (!WindowCatalog.Activate(next.Handle)) App.Log("Windows declined foreground activation.");
        }
        else if (!string.IsNullOrEmpty(item.Target) && LaunchService.Open(item.Target) &&
                 anchor is DockTile tile && item.Pin is not null)
        {
            _launchDeadlines[item.Key] = Environment.TickCount64 + 8000;
            tile.StartLaunchBounce(_app.Preferences.ReducedMotion);
        }
    }

    private void StopCompletedLaunches()
    {
        foreach (var tile in _tiles)
        {
            if (!_launchDeadlines.TryGetValue(tile.Item.Key, out var deadline)) continue;
            if (Environment.TickCount64 < deadline && tile.Item.Windows.Count == 0 &&
                !_app.Preferences.ReducedMotion) continue;
            tile.StopLaunchBounce();
            _launchDeadlines.Remove(tile.Item.Key);
        }
        foreach (var key in _launchDeadlines
                     .Where(entry => Environment.TickCount64 >= entry.Value)
                     .Select(entry => entry.Key).ToArray())
            _launchDeadlines.Remove(key);
    }

    private static MenuItem AddMenuItem(ContextMenu menu, string text, Action action)
    {
        var entry = new MenuItem { Header = text };
        entry.Click += (_, _) => action();
        menu.Items.Add(entry);
        return entry;
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement anchor)
    {
        _hideTimer.Stop();
        SetHidden(false);
        _menu = menu;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.Closed += (_, _) =>
        {
            CloseWindowPreview();
            _menu = null;
            if (_app.Preferences.AutoHide) _hideTimer.Start();
        };
        menu.IsOpen = true;
    }

    private void ShowItemMenu(DockTile tile)
    {
        var item = tile.Item;
        if (item.Key == "overflow") { ShowOverflow(item, tile); return; }
        var menu = new ContextMenu();
        if (!item.IsUtility)
        {
            AddMenuItem(menu, item.Windows.Count > 0 ? L("Dock.ShowWindows") : L("Dock.Open"), () => ActivateItem(item, tile));
            if (item.Target.Length > 0) AddMenuItem(menu, L("Dock.Reopen"), () => LaunchService.Open(item.Target));
            var showWindowPreviews = item.Windows.Count > 1;
            if (showWindowPreviews) PrepareWindowPreview();
            foreach (var window in item.Windows.Take(10))
            {
                var title = window.Title.Length > 65 ? window.Title[..62] + "…" : window.Title;
                var windowEntry = AddMenuItem(menu, title, () => WindowCatalog.Activate(window.Handle));
                if (showWindowPreviews)
                {
                    windowEntry.MouseEnter += (_, _) => ShowWindowPreview(window, windowEntry, item);
                    windowEntry.MouseLeave += (_, _) => _windowPreview?.HidePreview();
                }
            }
            menu.Items.Add(new Separator());
            if (item.Pin is { } pin)
            {
                AddMenuItem(menu, L("Dock.MoveLeft"), () => MovePin(pin.Id, -1));
                AddMenuItem(menu, L("Dock.MoveRight"), () => MovePin(pin.Id, 1));
                AddMenuItem(menu, L("Dock.Remove"), () => _app.UpdatePreferences(s => s.Pins.RemoveAll(p => p.Id == pin.Id)));
            }
            else if (File.Exists(item.Target))
                AddMenuItem(menu, L("Dock.Keep"), () => AddPaths([item.Target]));
            menu.Items.Add(new Separator());
        }
        else if (item.Key == "trash")
        {
            AddMenuItem(menu, L("Dock.RecycleOpen"), () => LaunchService.Open(item.Target));
            var recycle = RecycleBinService.Query();
            var empty = AddMenuItem(menu,
                recycle.Available && recycle.ItemCount > 0 ? LF("Dock.EmptyRecycleCount", recycle.ItemCount) : L("Dock.EmptyRecycle"),
                EmptyRecycleBin);
            empty.IsEnabled = recycle.Available && recycle.ItemCount > 0;
            menu.Items.Add(new Separator());
        }
        else if (item.Key == "velishell")
        {
            AddMenuItem(menu, L("Dock.RemoveVeliShell"), () =>
                _app.UpdatePreferences(settings => settings.ShowVeliShellDockItem = false));
            menu.Items.Add(new Separator());
        }
        else if (item.Key == "start")
        {
            AddMenuItem(menu, L("Dock.WindowsStart"), () => WindowsStartService.Open());
            AddMenuItem(menu, L("Dock.HideWindowsStart"), () =>
                _app.UpdatePreferences(settings => settings.ShowWindowsStartDockItem = false));
            menu.Items.Add(new Separator());
        }
        if (OnlineIconService.TryGetAttribution(item.Icon) is { } attribution)
        {
            AddMenuItem(menu, attribution.Text, () => LaunchService.Open(
                Uri.TryCreate(attribution.SourceUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps
                    ? uri.AbsoluteUri
                    : "https://apps.apple.com/"));
            menu.Items.Add(new Separator());
        }
        AddCommonMenu(menu);
        OpenMenu(menu, tile);
    }

    private void ShowFolderPopover(string path, FrameworkElement anchor)
    {
        CloseFolderPopover();
        try
        {
            var popover = new FolderPopoverWindow(path);
            _folderPopover = popover;
            popover.Closed += (_, _) =>
            {
                if (ReferenceEquals(_folderPopover, popover)) _folderPopover = null;
                if (_app.Preferences.AutoHide && !IsMouseOver) _hideTimer.Start();
            };
            _hideTimer.Stop();
            SetHidden(false);
            popover.ShowRelativeTo(anchor, this, _app.Preferences.AlwaysOnTop);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            App.Log("Could not open the pinned-folder popover", exception);
            LaunchService.Open(path);
        }
    }

    private void CloseFolderPopover()
    {
        var popover = _folderPopover;
        _folderPopover = null;
        popover?.Close();
    }

    private void ShowWindowPreview(NativeWindow window, FrameworkElement anchor, DockItem item)
    {
        if (!NativeMethods.IsWindow(window.Handle)) return;

        try
        {
            var target = string.IsNullOrWhiteSpace(item.Target) ? window.Executable : item.Target;
            var preview = _windowPreview;
            if (preview is null) return;
            preview.ShowFor(window.Handle, anchor, window.Title,
                IconService.For(item.IconId, target, item.Icon));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            App.Log("Could not show a window thumbnail", exception);
            CloseWindowPreview();
        }
    }

    private void PrepareWindowPreview()
    {
        CloseWindowPreview();
        try
        {
            _windowPreview = new WindowThumbnailPreview(this);
            _windowPreview.Prepare();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            App.Log("Could not prepare a window thumbnail", exception);
            CloseWindowPreview();
        }
    }

    private void CloseWindowPreview()
    {
        var preview = _windowPreview;
        _windowPreview = null;
        preview?.Dispose();
    }

    private void ShowOverflow(DockItem item, FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        foreach (var entry in item.Overflow)
            AddMenuItem(menu, entry.Name + (entry.Windows.Count > 0 ? "  •" : ""), () => ActivateItem(entry, anchor));
        menu.Items.Add(new Separator());
        AddCommonMenu(menu);
        OpenMenu(menu, anchor);
    }

    private void AddCommonMenu(ContextMenu menu)
    {
        AddMenuItem(menu, L("Dock.PinProgram"), AddPrograms);
        AddMenuItem(menu, L("Dock.PinFolder"), AddFolder);
        AddMenuItem(menu, L("Search.Open"), _app.ShowSearch);
        AddMenuItem(menu, L("Dock.Settings"), _app.ShowPreferences);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, L("Dock.Quit"), () => _app.RequestExit());
    }

    public void AddPrograms()
    {
        var picker = new OpenFileDialog { Title = L("Dock.ProgramPicker"), Multiselect = true, CheckFileExists = true,
            Filter = L("Dock.ProgramFilter") };
        if (picker.ShowDialog() == true) AddPaths(picker.FileNames);
    }

    public void AddFolder()
    {
        var picker = new OpenFolderDialog { Title = L("Dock.FolderPicker") };
        if (picker.ShowDialog() == true) AddPaths([picker.FolderName]);
    }

    internal void AddPaths(IEnumerable<string> paths, int? insertionIndex = null)
    {
        var boundedPaths = new List<string>(MaximumDropItems);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputTruncated = false;
        try
        {
            foreach (var path in paths)
            {
                if (boundedPaths.Count >= MaximumDropItems)
                {
                    inputTruncated = true;
                    break;
                }
                if (IsSafeDropPath(path) && seen.Add(path)) boundedPaths.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            App.Log("Could not read all dropped paths", ex);
        }

        var pins = boundedPaths.Select(LaunchService.PinFromPath).OfType<Pin>().ToList();
        if (pins.Count == 0) return;
        var skippedAtLimit = 0;
        _app.UpdatePreferences(s =>
        {
            var next = Math.Clamp(insertionIndex ?? s.Pins.Count, 0, s.Pins.Count);
            foreach (var pin in pins)
            {
                if (s.Pins.Any(p => string.Equals(p.Target, pin.Target, StringComparison.OrdinalIgnoreCase))) continue;
                if (s.Pins.Count >= Settings.MaximumPins)
                {
                    skippedAtLimit++;
                    continue;
                }
                s.Pins.Insert(next++, pin);
            }
        });
        if (skippedAtLimit > 0 || inputTruncated)
            MessageBox.Show(this,
                LF("Dock.PinLimit", Settings.MaximumPins,
                    skippedAtLimit > 0 ? LF("Dock.PinLimitSkipped", skippedAtLimit) : L("Dock.PinLimitTruncated")),
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void MovePin(string id, int delta) => _app.UpdatePreferences(s =>
    {
        var index = s.Pins.FindIndex(p => p.Id == id);
        if (index < 0 || delta == 0) return;
        var insertion = delta > 0 ? index + 2 : index - 1;
        PinOrder.MoveToInsertionIndex(s.Pins, id, insertion);
    });

    private void EmptyRecycleBin()
    {
        try
        {
            RecycleBinService.EmptyWithWindowsConfirmation(_handle);
            Rebuild();
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x800704C7))
        {
            // The Windows confirmation was cancelled; no VeliShell warning is needed.
        }
        catch (Exception ex)
        {
            App.Log("Could not empty the Recycle Bin", ex);
            MessageBox.Show(this, L("Dock.EmptyRecycleFailed") + "\n\n" + ex.Message,
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragSource = sender as DockTile;
        _dragStart = e.GetPosition(this);
    }

    private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragInProgress && ReferenceEquals(sender, _dragSource)) _dragSource = null;
    }

    private void Tile_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragInProgress && ReferenceEquals(sender, _dragSource)) _dragSource = null;
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragInProgress || !ReferenceEquals(sender, _dragSource)
            || e.LeftButton != MouseButtonState.Pressed || _dragSource?.Item.Pin is not { } pin) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var removePinAfterDrag = false;
        var dragSource = _dragSource!;
        QueryContinueDragEventHandler? queryContinueDrag = null;
        try
        {
            _dragInProgress = true;
            ResetDragPayloadCache();
            _pollTimer.Stop();
            _hideTimer.Stop();
            SetHidden(false);
            dragSource.Opacity = 0.42;
            dragSource.GiveFeedback += DragSource_GiveFeedback;
            queryContinueDrag = (_, args) =>
            {
                var leftButtonReleased = (args.KeyStates & DragDropKeyStates.LeftMouseButton) == 0;
                removePinAfterDrag = ShouldRemovePinAfterDrag(
                    args.EscapePressed,
                    leftButtonReleased,
                    IsCursorOverDockPlate());
            };
            dragSource.QueryContinueDrag += queryContinueDrag;
            ShowDragGhost("pin:" + pin.Id, IconService.For(pin.Id, pin.Target, pin.Icon));
            System.Windows.DragDrop.DoDragDrop(
                dragSource,
                CreateDockPinDragData(pin.Id),
                DragDropEffects.Move);
        }
        finally
        {
            dragSource.GiveFeedback -= DragSource_GiveFeedback;
            if (queryContinueDrag is not null) dragSource.QueryContinueDrag -= queryContinueDrag;
            ClearDropSlot();
            dragSource.Opacity = 1;
            dragSource.Visibility = Visibility.Visible;
            CloseDragGhost();
            _dragInProgress = false;
            _dragSource = null;
            ResetDragPayloadCache();
            _suppressActivationUntil = Environment.TickCount64 + 300;
            HideDropMarker();
            if (!_closed) _pollTimer.Start();
            if (_app.Preferences.AutoHide && !IsMouseOver) _hideTimer.Start();
        }
        if (removePinAfterDrag && !_closed)
            _app.UpdatePreferences(settings => settings.Pins.RemoveAll(candidate =>
                string.Equals(candidate.Id, pin.Id, StringComparison.OrdinalIgnoreCase)));
    }

    private void DragSource_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (IsCursorOverDockPlate()) _dragGhost?.MoveToCursor();
        else CloseDragGhost();
    }

    private static DataObject CreateDockPinDragData(string pinId)
    {
        if (string.IsNullOrWhiteSpace(pinId) || pinId.Length > 64)
            throw new ArgumentException("Invalid dock pin identifier.", nameof(pinId));
        var data = new DataObject();
        data.SetData(DockPinFormat, pinId, autoConvert: false);
        return data;
    }

    private static bool ShouldRemovePinAfterDrag(
        bool escapePressed,
        bool leftButtonReleased,
        bool cursorInsideDock) =>
        !escapePressed && leftButtonReleased && !cursorInsideDock;

    private bool IsCursorOverDockPlate()
    {
        try
        {
            if (!DockPlate.IsVisible || DockPlate.ActualWidth <= 0 || DockPlate.ActualHeight <= 0 ||
                !NativeMethods.GetCursorPos(out var cursor)) return true;
            return IsScreenPointOverDockPlate(new Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            // An unavailable visual transform must never remove a pin by guess.
            return true;
        }
    }

    private bool IsDragEventOverDockPlate(DragEventArgs e)
    {
        try
        {
            if (!DockPlate.IsVisible || DockPlate.ActualWidth <= 0 || DockPlate.ActualHeight <= 0)
                return false;
            return IsScreenPointOverDockPlate(PointToScreen(e.GetPosition(this)));
        }
        catch (InvalidOperationException)
        {
            // Failed hit testing must reject a drop, while the cursor-based
            // removal path above remains conservative and keeps the pin.
            return false;
        }
    }

    private bool IsScreenPointOverDockPlate(Point screenPoint)
    {
        var topLeft = DockPlate.PointToScreen(new Point(0, 0));
        var bottomRight = DockPlate.PointToScreen(
            new Point(DockPlate.ActualWidth, DockPlate.ActualHeight));
        return IsPointInsideDockBounds(screenPoint, topLeft, bottomRight);
    }

    private static bool IsPointInsideDockBounds(Point point, Point firstCorner, Point secondCorner) =>
        point.X >= Math.Min(firstCorner.X, secondCorner.X) &&
        point.X <= Math.Max(firstCorner.X, secondCorner.X) &&
        point.Y >= Math.Min(firstCorner.Y, secondCorner.Y) &&
        point.Y <= Math.Max(firstCorner.Y, secondCorner.Y);

    private static DragDropEffects DockDropEffect(bool cursorInsideDock, bool hasPin, bool hasFiles) =>
        !cursorInsideDock ? DragDropEffects.None :
        hasPin ? DragDropEffects.Move :
        hasFiles ? DragDropEffects.Copy : DragDropEffects.None;

    private void ShowDragGhost(string key, ImageSource source)
    {
        if (_dragGhost is not null && string.Equals(_dragGhostKey, key, StringComparison.Ordinal))
        {
            _dragGhost.MoveToCursor();
            return;
        }

        CloseDragGhost();
        try
        {
            _dragGhostKey = key;
            _dragGhost = new DragGhostWindow(this, source, _app.Preferences.IconSize);
            _dragGhost.ShowAtCursor();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            App.Log("Could not show the drag preview", exception);
            CloseDragGhost();
        }
    }

    private void CloseDragGhost()
    {
        var ghost = _dragGhost;
        _dragGhost = null;
        _dragGhostKey = null;
        ghost?.Dispose();
    }

    private int InsertionIndex(Point position, string? movingPinId)
    {
        var pinnedTiles = _tiles.Where(tile => tile.Item.Pin is not null
            && !string.Equals(tile.Item.Pin.Id, movingPinId, StringComparison.OrdinalIgnoreCase)).ToList();
        var finalIndex = pinnedTiles.Count;
        for (var index = 0; index < pinnedTiles.Count; index++)
        {
            var tile = pinnedTiles[index];
            var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, 0), ItemsPanel).X;
            if (position.X < center)
            {
                finalIndex = index;
                break;
            }
        }

        if (string.IsNullOrEmpty(movingPinId)) return finalIndex;
        var sourceIndex = _app.Preferences.Pins.FindIndex(pin =>
            string.Equals(pin.Id, movingPinId, StringComparison.OrdinalIgnoreCase));
        return sourceIndex >= 0 && finalIndex > sourceIndex ? finalIndex + 1 : finalIndex;
    }

    private void ShowDropMarker(int insertionIndex)
    {
        var pinnedTiles = _tiles.Where(tile => tile.Item.Pin is not null).ToList();
        double x;
        if (pinnedTiles.Count == 0)
            x = ItemsPanel.TranslatePoint(new Point(0, 0), DockBody).X;
        else if (insertionIndex >= pinnedTiles.Count)
        {
            var last = pinnedTiles[^1];
            x = last.TranslatePoint(new Point(last.ActualWidth, 0), DockBody).X;
        }
        else
            x = pinnedTiles[insertionIndex].TranslatePoint(new Point(0, 0), DockBody).X;

        DropMarker.Margin = new Thickness(Math.Max(12, x - DropMarker.Width / 2), 0, 0, 17);
        DropMarker.Height = _app.Preferences.IconSize + 5;
        DropMarker.Visibility = Visibility.Visible;
    }

    private void HideDropMarker()
    {
        _dropInsertionIndex = -1;
        DropMarker.Visibility = Visibility.Collapsed;
    }

    private void ShowDropSlot(int insertionIndex, string? movingPinId, ImageSource? previewIcon, int itemCount)
    {
        var key = string.IsNullOrEmpty(movingPinId)
            ? "files:" + (_cachedDragPaths.FirstOrDefault() ?? "") + ":" + itemCount
            : "pin:" + movingPinId;
        if (_dropPlaceholder is not null
            && ItemsPanel.Children.Contains(_dropPlaceholder)
            && _dropPlaceholderInsertionIndex == insertionIndex
            && string.Equals(_dropPlaceholderKey, key, StringComparison.Ordinal))
        {
            ItemsPanel.UpdateLayout();
            if (!string.IsNullOrEmpty(movingPinId)) _dragGhost?.SnapTo(_dropPlaceholder);
            return;
        }

        if (_dropPlaceholder is not null) ItemsPanel.Children.Remove(_dropPlaceholder);
        _dropPlaceholder = null;

        _dropSourceTile?.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        _dropSourceTile = string.IsNullOrEmpty(movingPinId)
            ? null
            : _tiles.FirstOrDefault(tile => string.Equals(
                tile.Item.Pin?.Id,
                movingPinId,
                StringComparison.OrdinalIgnoreCase));
        // A local reorder removes the source tile from layout while the slot
        // placeholder takes its exact width. External Shell drags retain their
        // one native cursor ghost; the placeholder is their only dock preview.
        if (_dropSourceTile is not null) _dropSourceTile.Visibility = Visibility.Collapsed;

        var needsExtraWidth = _dropSourceTile is null;
        if (_dropPlaceholderAddsWidth != needsExtraWidth)
        {
            _dropPlaceholderAddsWidth = needsExtraWidth;
            Width = _baseDockWidth + (needsExtraWidth ? _app.Preferences.IconSize + 22 : 0);
            PositionDock();
        }

        var remainingPins = _tiles.Where(tile => tile.Item.Pin is not null
            && !string.Equals(tile.Item.Pin.Id, movingPinId, StringComparison.OrdinalIgnoreCase)).ToList();
        var sourceIndex = string.IsNullOrEmpty(movingPinId)
            ? -1
            : _app.Preferences.Pins.FindIndex(pin =>
                string.Equals(pin.Id, movingPinId, StringComparison.OrdinalIgnoreCase));
        var finalIndex = sourceIndex >= 0 && sourceIndex < insertionIndex
            ? insertionIndex - 1
            : insertionIndex;
        finalIndex = Math.Clamp(finalIndex, 0, remainingPins.Count);

        var placeholder = CreateDropPlaceholder(previewIcon, itemCount);
        var childIndex = remainingPins.Count == 0
            ? 0
            : finalIndex < remainingPins.Count
                ? ItemsPanel.Children.IndexOf(remainingPins[finalIndex])
                : ItemsPanel.Children.IndexOf(remainingPins[^1]) + 1;
        childIndex = Math.Clamp(childIndex, 0, ItemsPanel.Children.Count);
        ItemsPanel.Children.Insert(childIndex, placeholder);
        _dropPlaceholder = placeholder;
        _dropPlaceholderInsertionIndex = insertionIndex;
        _dropPlaceholderKey = key;
        _dropInsertionIndex = insertionIndex;
        DropMarker.Visibility = Visibility.Collapsed;
        ItemsPanel.UpdateLayout();
        if (!string.IsNullOrEmpty(movingPinId)) _dragGhost?.SnapTo(placeholder);
    }

    private FrameworkElement CreateDropPlaceholder(ImageSource? previewIcon, int itemCount)
    {
        var size = _app.Preferences.IconSize;
        var iconSurface = new AppIconSurface(previewIcon, size)
        {
            Opacity = previewIcon is null ? 0.24 : 0.78,
        };

        var placeholder = new Grid
        {
            Width = size + 22,
            Height = size + 22,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Children = { iconSurface }
        };
        if (previewIcon is not null && itemCount > 1)
        {
            var badgeText = new TextBlock
            {
                Text = itemCount > 99 ? "99+" : itemCount.ToString(),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new Border
            {
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3, 3),
                Child = badgeText
            };
            badge.SetResourceReference(Border.BackgroundProperty, "DockIndicator");
            placeholder.Children.Add(badge);
        }
        return placeholder;
    }

    private void ClearDropSlot()
    {
        if (_dropPlaceholder is not null) ItemsPanel.Children.Remove(_dropPlaceholder);
        _dropPlaceholder = null;
        _dropPlaceholderInsertionIndex = -1;
        _dropPlaceholderKey = null;
        if (_dropSourceTile is not null) _dropSourceTile.Visibility = Visibility.Visible;
        _dropSourceTile = null;
        if (_dropPlaceholderAddsWidth)
        {
            _dropPlaceholderAddsWidth = false;
            Width = _baseDockWidth;
            PositionDock();
        }
        HideDropMarker();
    }

    private void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        if (_hidden) SetHidden(false);
        if (_dragInProgress) return;
        var mouse = e.GetPosition(ItemsPanel);
        foreach (var tile in _tiles)
        {
            var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, 0), ItemsPanel).X;
            var scale = DockMath.ScaleAt(mouse.X - center, tile.IconSize, _app.Preferences.Magnification && !_app.Preferences.ReducedMotion);
            tile.SetScale(scale, false);
        }
    }
    private void Dock_MouseEnter(object sender, MouseEventArgs e) { _hideTimer.Stop(); SetHidden(false); }
    private void Dock_MouseLeave(object sender, MouseEventArgs e)
    {
        foreach (var tile in _tiles) tile.SetScale(1, !_app.Preferences.ReducedMotion);
        if (_app.Preferences.AutoHide) _hideTimer.Start();
    }
    private void SetHidden(bool hidden)
    {
        if (_dragInProgress && hidden) return;
        _hidden = hidden && _app.Preferences.AutoHide;
        RevealHandle.Visibility = _hidden ? Visibility.Visible : Visibility.Collapsed;
        DockBody.IsHitTestVisible = !_hidden;
        var target = _hidden ? Height + 10 : 0;
        if (_app.Preferences.ReducedMotion)
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = target;
        }
        else Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void Dock_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu(); AddCommonMenu(menu); OpenMenu(menu, DockPlate); e.Handled = true;
    }
    private void Dock_DragEnter(object sender, DragEventArgs e)
    {
        _pollTimer.Stop();
        _hideTimer.Stop();
        SetHidden(false);
        CacheDragPayload(e.Data);
        UpdateDragFeedback(e);
    }
    private void Dock_DragOver(object sender, DragEventArgs e)
    {
        UpdateDragFeedback(e);
    }
    private void UpdateDragFeedback(DragEventArgs e)
    {
        if (!_dragPayloadCached) CacheDragPayload(e.Data);
        var hasPin = _cachedDragPinId.Length > 0;
        var hasFiles = _cachedDragPaths.Count > 0;
        var cursorInsideDock = IsDragEventOverDockPlate(e);
        e.Effects = DockDropEffect(cursorInsideDock, hasPin, hasFiles);
        if (e.Effects != DragDropEffects.None)
        {
            string? movingPinId = null;
            ImageSource? previewIcon = null;
            if (hasPin)
            {
                var pin = _app.Preferences.Pins.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, _cachedDragPinId, StringComparison.OrdinalIgnoreCase));
                if (pin is not null)
                {
                    movingPinId = pin.Id;
                    var ghostKey = "pin:" + pin.Id;
                    if (_dragGhost is null || !string.Equals(_dragGhostKey, ghostKey, StringComparison.Ordinal))
                        ShowDragGhost(ghostKey, IconService.For(pin.Id, pin.Target, pin.Icon));
                }
            }
            else if (_cachedDragPaths.Count > 0)
            {
                var path = _cachedDragPaths[0];
                previewIcon = IconService.For("drop-preview", path);
            }

            _dropInsertionIndex = InsertionIndex(e.GetPosition(ItemsPanel), movingPinId);
            ShowDropSlot(_dropInsertionIndex, movingPinId, previewIcon, _cachedDragPaths.Count);
        }
        else
        {
            ClearDropSlot();
            CloseDragGhost();
        }
        e.Handled = true;
    }
    private void Dock_DragLeave(object sender, DragEventArgs e)
    {
        if (IsDragEventOverDockPlate(e))
        {
            e.Handled = true;
            return;
        }

        ClearDropSlot();
        if (!_dragInProgress)
        {
            CloseDragGhost();
            if (!_closed) _pollTimer.Start();
        }
        else CloseDragGhost();
        ResetDragPayloadCache();
        if (_app.Preferences.AutoHide && !IsMouseOver && !_dragInProgress) _hideTimer.Start();
        e.Handled = true;
    }
    private void Dock_Drop(object sender, DragEventArgs e)
    {
        if (!IsDragEventOverDockPlate(e))
        {
            e.Effects = DragDropEffects.None;
            ClearDropSlot();
            CloseDragGhost();
            ResetDragPayloadCache();
            if (!_dragInProgress && !_closed) _pollTimer.Start();
            e.Handled = true;
            return;
        }

        var insertion = _dropInsertionIndex < 0 ? _app.Preferences.Pins.Count : _dropInsertionIndex;
        ClearDropSlot();
        if (TryReadDockPin(e.Data, out var id))
            _app.UpdatePreferences(s => PinOrder.MoveToInsertionIndex(s.Pins, id, insertion));
        else if (TryReadDroppedPaths(e.Data, out var files) && files.Count > 0)
            AddPaths(files, insertion);
        if (!_dragInProgress)
        {
            CloseDragGhost();
            if (!_closed) _pollTimer.Start();
        }
        ResetDragPayloadCache();
        e.Handled = true;
    }

    private void CacheDragPayload(IDataObject data)
    {
        if (_dragPayloadCached) return;
        _cachedDragPinId = TryReadDockPin(data, out var pinId) ? pinId : "";
        _cachedDragPaths = _cachedDragPinId.Length == 0 && TryReadDroppedPaths(data, out var paths)
            ? paths
            : Array.Empty<string>();
        _dragPayloadCached = true;
    }

    private void ResetDragPayloadCache()
    {
        _dragPayloadCached = false;
        _cachedDragPinId = "";
        _cachedDragPaths = Array.Empty<string>();
    }

    private bool TryReadDockPin(IDataObject data, out string id)
    {
        id = "";
        try
        {
            if (!data.GetDataPresent(DockPinFormat, autoConvert: false) ||
                data.GetData(DockPinFormat, autoConvert: false) is not string value
                || value.Length is < 1 or > 64)
                return false;
            if (!_app.Preferences.Pins.Any(pin => string.Equals(pin.Id, value, StringComparison.OrdinalIgnoreCase)))
                return false;
            id = value;
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            App.Log("Could not read the VeliShell drag format", ex);
            return false;
        }
    }

    private static bool TryReadDroppedPaths(IDataObject data, out IReadOnlyList<string> paths)
    {
        paths = Array.Empty<string>();
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] values)
                return false;
            paths = values.Take(MaximumDropItems)
                .Where(IsSafeDropPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return paths.Count > 0;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException
                                   or IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log("Could not read the Windows file-drop format", ex);
            return false;
        }
    }

    private static bool IsSafeDropPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= MaximumDropPathLength &&
        path.IndexOf('\0') < 0 && (File.Exists(path) || Directory.Exists(path));
    private void Dock_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var delta = e.Delta > 0 ? 4 : -4;
        _app.UpdatePreferences(s => s.IconSize = Math.Clamp(
            s.IconSize + delta, Settings.MinimumIconSize, Settings.MaximumIconSize));
        e.Handled = true;
    }
}
