using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class SearchWindow : VeliShellWindow
{
    private readonly App _app;
    private IReadOnlyList<SearchEntry> _catalog = [];
    private IReadOnlyList<SearchEntry> _running = [];
    private IReadOnlyList<SearchEntry> _webResults = [];
    private readonly DispatcherTimer _webDebounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource? _webRequest;
    private readonly List<SearchEntry> _visible = [];
    private int _selectedIndex;
    private bool _closed;

    public SearchWindow(App app)
    {
        _app = app;
        _catalog = app.ProgramIndex.Snapshot;
        InitializeComponent();
        app.ProgramIndex.Changed += IndexChanged;
        _webDebounce.Tick += async (_, _) =>
        {
            _webDebounce.Stop();
            var query = QueryBox.Text.Trim();
            _webRequest?.Cancel();
            var requestLifetime = new CancellationTokenSource();
            _webRequest = requestLifetime;
            var token = requestLifetime.Token;
            try
            {
                var hits = await LiveWebSearchService.SearchAsync(query, token);
                if (!_closed && !token.IsCancellationRequested && query == QueryBox.Text.Trim())
                {
                    _webResults = hits.Select(hit => new SearchEntry(hit.Title, hit.Url.AbsoluteUri,
                            "web-result", hit.Description))
                        .ToArray();
                    RenderResults();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { App.Log("Live web results are unavailable", exception); }
            finally
            {
                if (ReferenceEquals(_webRequest, requestLifetime)) _webRequest = null;
                requestLifetime.Dispose();
            }
        };
        Loaded += async (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(16, work.Height * 0.16);
            QueryBox.Focus();
            RenderResults();
            try
            {
                var windows = await Task.Run(WindowCatalog.Read);
                if (!_closed)
                {
                    _running = windows
                        .Where(window => !string.IsNullOrWhiteSpace(window.Executable))
                        .Select(window => new SearchEntry(window.ProcessName, window.Executable, "app"))
                        .DistinctBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase).ToArray();
                    _catalog = _app.ProgramIndex.Snapshot.Concat(_running).ToArray();
                    RenderResults();
                }
            }
            catch (Exception exception)
            {
                App.Log("Local program search could not read open windows", exception);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _webDebounce.Stop();
            _webRequest?.Cancel();
            app.ProgramIndex.Changed -= IndexChanged;
        };
    }

    private void IndexChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closed) return;
        _catalog = _app.ProgramIndex.Snapshot.Concat(_running).ToArray();
        RenderResults();
    });

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _webDebounce.Stop();
        _webRequest?.Cancel();
        _webResults = [];
        RenderResults();
        if (QueryBox.Text.Trim().Length < 3) return;
        try
        {
            if (UserApiCredentials.Read(UserApiCredentials.BraveSearch) is not null)
                _webDebounce.Start();
        }
        catch (Exception exception) { App.Log("Could not read live-search credential", exception); }
    }

    private void RenderResults()
    {
        if (ResultsPanel is null || QueryBox is null) return;
        ResultsPanel.Children.Clear();
        _visible.Clear();
        var query = QueryBox.Text.Trim();
        _visible.AddRange(SearchCatalogService.Match(query, _app.Preferences.Pins, _catalog));
        _visible.AddRange(_webResults);
        if (query.Length is > 0 and <= 200)
            _visible.Add(new SearchEntry(
                string.Format(LocalizationService.Current.ActiveCulture,
                    LocalizationService.Current.Get("Search.WebFor"), query), query, "web"));

        for (var index = 0; index < _visible.Count; index++)
        {
            var entry = _visible[index];
            var rowIndex = index;
            var button = new Button
            {
                Tag = rowIndex,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = CreateRow(entry)
            };
            button.Click += (_, _) => Activate(rowIndex);
            button.MouseEnter += (_, _) => { _selectedIndex = rowIndex; UpdateSelection(); };
            ResultsPanel.Children.Add(button);
        }
        if (_visible.Count == 0)
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Current.Get("Search.NoResults"),
                Margin = new Thickness(11, 14, 0, 0),
                Foreground = (Brush)FindResource("TextSecondary")
            });
        _selectedIndex = 0;
        UpdateSelection();
        Footer.Text = LocalizationService.Current.Get("Search.PrivacyHint") + " · " +
                      _app.SearchHotkeyStatus;
    }

    private UIElement CreateRow(SearchEntry entry)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = entry.Category is "web" or "web-result"
            ? IconService.For("browser")
            : IconService.For("app", entry.Target);
        row.Children.Add(new AppIconSurface(icon, 34, false)
        {
            Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0)
        });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock
        {
            Text = entry.Category == "web-result" ? new Uri(entry.Target).Host :
                LocalizationService.Current.Get(entry.Category == "web" ? "Search.Web" :
                entry.Category == "pin" ? "Search.Pinned" : "Search.Program"),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary")
        });
        if (!string.IsNullOrWhiteSpace(entry.Description))
            labels.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextSecondary")
            });
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        return row;
    }

    private void UpdateSelection()
    {
        for (var index = 0; index < ResultsPanel.Children.Count; index++)
        {
            if (ResultsPanel.Children[index] is not Button button) continue;
            button.Background = index == _selectedIndex ? (Brush)FindResource("HoverSurface") : Brushes.Transparent;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_visible.Count == 0) return;
        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _visible.Count - 1);
        UpdateSelection();
        if (ResultsPanel.Children[_selectedIndex] is FrameworkElement element)
            element.BringIntoView();
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        var entry = _visible[index];
        var target = entry.Category == "web"
            ? SearchCatalogService.WebSearchUri(entry.Target).AbsoluteUri
            : entry.Target;
        Close();
        LaunchService.Open(target);
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; }
        else if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Activate(_selectedIndex); e.Handled = true; }
    }

    internal void FocusQuery()
    {
        if (!IsVisible) Show();
        Activate();
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
}
