using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

/// <summary>An optional app launcher; the Windows Start menu remains untouched.</summary>
public partial class StartLauncherWindow : Window
{
    private const int PageSize = 64;
    private readonly App _app;
    private IReadOnlyList<SearchEntry> _catalog = [];
    private int _shown = PageSize;
    private bool _closed;
    private bool _dragging;

    internal StartLauncherWindow(App app)
    {
        _app = app;
        _catalog = app.ProgramIndex.Snapshot;
        InitializeComponent();
        UserLabel.Text = Environment.UserName;
        app.ProgramIndex.Changed += IndexChanged;
        Loaded += (_, _) => { Render(); QueryBox.Focus(); };
        Deactivated += (_, _) => { if (!_dragging) Close(); };
        Closed += (_, _) =>
        {
            _closed = true;
            app.ProgramIndex.Changed -= IndexChanged;
        };
    }

    internal void ShowAbove(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner;
        Topmost = topmost;
        PlaceAbove(anchor);
        Show();
        Activate();
        QueryBox.Focus();
    }

    private void PlaceAbove(FrameworkElement anchor)
    {
        var source = PresentationSource.FromVisual(anchor);
        if (source?.CompositionTarget is null) return;
        var topLeft = anchor.PointToScreen(new Point(0, 0));
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point
        {
            X = checked((int)Math.Round((topLeft.X + bottomRight.X) / 2)),
            Y = checked((int)Math.Round(topLeft.Y))
        }, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var areaTopLeft = toDip.Transform(new Point(info.Work.Left, info.Work.Top));
        var areaBottomRight = toDip.Transform(new Point(info.Work.Right, info.Work.Bottom));
        var anchorTopLeft = toDip.Transform(topLeft);
        var anchorBottomRight = toDip.Transform(bottomRight);
        var bounds = CalculateBounds(new Rect(anchorTopLeft, anchorBottomRight),
            new Rect(areaTopLeft, areaBottomRight), new Size(610, 640));
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private static Rect CalculateBounds(Rect anchor, Rect workArea, Size desired)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 ||
            !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            desired.Width <= 0 || desired.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        const double margin = 10;
        var width = Math.Min(desired.Width, Math.Max(1, workArea.Width - margin * 2));
        var height = Math.Min(desired.Height, Math.Max(1, workArea.Height - margin * 2));
        var minLeft = workArea.Left + Math.Min(margin, Math.Max(0, workArea.Width - width));
        var maxLeft = Math.Max(minLeft, workArea.Right - width - margin);
        var minTop = workArea.Top + Math.Min(margin, Math.Max(0, workArea.Height - height));
        var maxTop = Math.Max(minTop, workArea.Bottom - height - margin);
        return new Rect(
            Math.Clamp(anchor.Left + (anchor.Width - width) / 2, minLeft, maxLeft),
            Math.Clamp(anchor.Top - height - margin, minTop, maxTop), width, height);
    }

    private void IndexChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closed) return;
        _catalog = _app.ProgramIndex.Snapshot;
        Render();
    });

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _shown = PageSize;
        Render();
    }

    private void Render()
    {
        if (AppsPanel is null || PinnedPanel is null || QueryBox is null) return;
        var query = QueryBox.Text.Trim();
        PinnedSection.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length == 0) RenderPinned();
        ListHeading.Text = L(query.Length == 0 ? "Start.AllApps" : "Start.Results");

        var entries = _catalog
            .Where(entry => entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => query.Length > 0 && entry.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)
                ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        AppsPanel.Children.Clear();
        foreach (var entry in entries.Take(_shown)) AppsPanel.Children.Add(CreateAppRow(entry));
        if (entries.Length == 0)
            AppsPanel.Children.Add(new TextBlock
            {
                Text = L("Start.Empty"), Margin = new Thickness(8, 18, 0, 18),
                Foreground = (Brush)FindResource("TextSecondary")
            });
        ResultCount.Text = entries.Length.ToString(LocalizationService.Current.ActiveCulture);
        MoreButton.Visibility = entries.Length > _shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderPinned()
    {
        PinnedPanel.Children.Clear();
        foreach (var pin in _app.Preferences.Pins.Take(12))
        {
            var entry = new SearchEntry(LocalizationService.Current.DisplayPinName(pin), pin.Target, "pin");
            var button = new Button
            {
                Style = (Style)FindResource("GlassTileButton"),
                Width = 127, Height = 77, Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 7, 7),
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new AppIconSurface(IconService.For(pin.Kind == PinKind.VirtualFolder
                            ? "virtual-folder" : pin.Id, pin.Target, pin.Icon), 35,
                            pin.Kind == PinKind.VirtualFolder || Directory.Exists(pin.Target))
                            { Width = 39, Height = 39 },
                        new TextBlock { Text = entry.Name, MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis,
                            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
                            Foreground = (Brush)FindResource("TextPrimary") }
                    }
                }
            };
            AutomationProperties.SetName(button, entry.Name);
            button.Click += (_, _) =>
            {
                if (pin.Kind == PinKind.VirtualFolder)
                {
                    Close();
                    _app.Dock.OpenVirtualFolder(pin);
                }
                else Open(entry.Target);
            };
            WireDrag(button, entry);
            PinnedPanel.Children.Add(button);
        }
    }

    private Button CreateAppRow(SearchEntry entry)
    {
        var icon = new AppIconSurface(IconService.For("app", entry.Target), 35, false)
        {
            Width = 39, Height = 39, Margin = new Thickness(0, 0, 10, 0)
        };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = entry.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextPrimary")
        });
        labels.Children.Add(new TextBlock
        {
            Text = L(entry.Category == "webapp" ? "Search.WebApp" : "Search.Program"),
            FontSize = 10.5, Foreground = (Brush)FindResource("TextSecondary")
        });
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, labels } };
        var button = new Button
        {
            Style = (Style)FindResource("GlassTileButton"),
            Content = content, Height = 56, Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = entry.Name
        };
        AutomationProperties.SetName(button, entry.Name);
        button.Click += (_, _) => Open(entry.Target);
        if (File.Exists(entry.Target))
        {
            WireDrag(button, entry);
            var context = new ContextMenu();
            var pin = new MenuItem { Header = L("Start.PinToDock") };
            pin.Click += (_, _) => _app.Dock.AddPaths([entry.Target]);
            context.Items.Add(pin);
            button.ContextMenu = context;
        }
        return button;
    }

    private void WireDrag(Button button, SearchEntry entry)
    {
        if (!File.Exists(entry.Target)) return;
        Point start = default;
        button.PreviewMouseLeftButtonDown += (_, args) => start = args.GetPosition(button);
        button.PreviewMouseMove += (_, args) =>
        {
            if (_dragging || args.LeftButton != MouseButtonState.Pressed) return;
            var point = args.GetPosition(button);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            try
            {
                _dragging = true;
                var data = new DataObject(DataFormats.FileDrop, new[] { entry.Target });
                DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
            }
            finally
            {
                _dragging = false;
                Close();
            }
        };
    }

    private void Open(string target)
    {
        Close();
        LaunchService.Open(target);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _shown = Math.Min(_catalog.Count, _shown + PageSize);
        Render();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ShowPreferences();
    }

    private void WindowsMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
        WindowsStartService.Open();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement == QueryBox)
        {
            var first = _catalog.Where(entry => entry.Name.Contains(QueryBox.Text.Trim(),
                StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
            if (first is not null) Open(first.Target);
            e.Handled = true;
        }
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
}
