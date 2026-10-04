using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using VeliShell.Core;
using VeliShell.Desktop.Services.Localization;

namespace VeliShell.Desktop.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Current { get; } = new();

    private UiLanguage _preference = UiLanguage.System;
    private IReadOnlyDictionary<string, string> _strings = GermanStrings.Values;

    private LocalizationService() => Apply(UiLanguage.System);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public UiLanguage Preference => _preference;
    public CultureInfo ActiveCulture { get; private set; } = CultureInfo.GetCultureInfo("de-DE");
    public string this[string key] => _strings.TryGetValue(key, out var value)
        ? value
        : GermanStrings.Values.TryGetValue(key, out var fallback) ? fallback : key;

    public void Apply(UiLanguage preference)
    {
        if (!Enum.IsDefined(preference)) preference = UiLanguage.System;
        _preference = preference;
        var systemCulture = CultureInfo.CurrentUICulture;
        var useGerman = preference == UiLanguage.German ||
                        preference == UiLanguage.System && systemCulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);
        ActiveCulture = useGerman ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");
        _strings = useGerman ? GermanStrings.Values : EnglishStrings.Values;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveCulture)));
        Changed?.Invoke();
    }

    public string Get(string key) => this[key];

    public string DisplayPinName(Pin pin)
    {
        var key = pin.Id.ToLowerInvariant() switch
        {
            "files" when pin.Name is "Dateien" or "Files" => "Pins.Files",
            "browser" when pin.Name == "Browser" => "Pins.Browser",
            "notes" when pin.Name is "Editor" or "Text Editor" => "Pins.Editor",
            "system" when pin.Name is "Windows-Einstellungen" or "Windows Settings" => "Pins.Settings",
            _ => null
        };
        if (key is not null) return Get(key);
        return pin.Name is "Anwendung" or "Application" ? Get("ShellLink.Application") : pin.Name;
    }

    public bool IsKnownValue(string key, string? value) =>
        value is not null &&
        (GermanStrings.Values.TryGetValue(key, out var german) && string.Equals(value, german, StringComparison.Ordinal) ||
         EnglishStrings.Values.TryGetValue(key, out var english) && string.Equals(value, english, StringComparison.Ordinal));
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
