using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class IconPickerWindow : VeliShellWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<AppStoreIconSearchHit> _hits;
    private readonly List<(AppStoreIconSearchHit Hit, Image Preview)> _previews = [];

    internal IconPickerWindow(string appName, string query, IReadOnlyList<AppStoreIconSearchHit> hits)
    {
        _hits = hits;
        InitializeComponent();
        Heading.Text = string.Format(LocalizationService.Current.ActiveCulture,
            LocalizationService.Current.Get("IconPicker.Heading"), appName);
        SearchHint.Text = string.Format(LocalizationService.Current.ActiveCulture,
            LocalizationService.Current.Get("IconPicker.SearchHint"), query, hits.Count);
        BuildResults();
        Loaded += async (_, _) => await LoadPreviewsAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    internal AppStoreIconSearchHit? SelectedHit { get; private set; }

    private void BuildResults()
    {
        if (_hits.Count == 0)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Current.Get("IconPicker.NoResults"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 18, 4, 18)
            });
            return;
        }

        for (var index = 0; index < _hits.Count; index++)
        {
            var hit = _hits[index];
            var preview = new Image
            {
                Source = IconService.For("online-placeholder"),
                Width = 62,
                Height = 62,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 15, 0)
            };
            var title = new TextBlock
            {
                Text = hit.AppName,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var credit = new TextBlock
            {
                Text = FormatAttribution(hit),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            credit.SetResourceReference(ForegroundProperty, "TextSecondary");
            var details = new TextBlock
            {
                Text = FormatDetails(hit),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            };
            details.SetResourceReference(ForegroundProperty, "TextSecondary");
            var storeLink = new Button
            {
                Content = LocalizationService.Current.Get("IconPicker.OpenResult"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 7, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                MinHeight = 26,
                ToolTip = hit.StoreUrl.AbsoluteUri
            };
            AutomationProperties.SetName(storeLink,
                LocalizationService.Current.Get("IconPicker.OpenResult") + ": " + hit.AppName);
            storeLink.Click += (_, args) =>
            {
                args.Handled = true;
                LaunchService.Open(hit.StoreUrl.AbsoluteUri);
            };

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(title);
            labels.Children.Add(credit);
            labels.Children.Add(details);
            labels.Children.Add(storeLink);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(preview);
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);

            var choice = new RadioButton
            {
                GroupName = "OnlineIcon",
                Content = content,
                Tag = hit,
                Margin = new Thickness(0, 0, 0, 9),
                Padding = new Thickness(15, 12, 15, 12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            choice.SetResourceReference(StyleProperty, "AppearanceChoice");
            AutomationProperties.SetName(choice,
                hit.AppName + ", " + FormatAttribution(hit));
            choice.Checked += (_, _) =>
            {
                SelectedHit = hit;
                ApplyButton.IsEnabled = true;
            };
            ResultsPanel.Children.Add(choice);
            _previews.Add((hit, preview));
        }
    }

    private async Task LoadPreviewsAsync()
    {
        try
        {
            await Task.WhenAll(_previews.Select(async entry =>
            {
                var image = await AppStoreIconService.LoadPreviewAsync(entry.Hit, _lifetime.Token);
                if (image is null || _lifetime.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() => entry.Preview.Source = image);
            }));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private static string FormatDetails(AppStoreIconSearchHit hit)
    {
        var values = new[] { hit.Category, hit.BundleId }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(" · ", values);
    }

    private static string FormatAttribution(AppStoreIconSearchHit hit) =>
        "App Store · " + hit.DeveloperName;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHit is null) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OpenProvider_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open(
            "https://performance-partners.apple.com/resources/documentation/itunes-store-web-service-search-api/");
}
