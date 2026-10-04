using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.Core;

public sealed record ItunesSoftwareSearchHit(
    long TrackId,
    string TrackName,
    string DeveloperName,
    string? BundleId,
    string? PrimaryGenreName,
    string ArtworkUrl100,
    string? ArtworkUrl512,
    string TrackViewUrl);

/// <summary>
/// Strict, network-free handling for Apple's documented iTunes Search API.
/// Keeping request construction and response validation in Core makes the
/// provider contract independently testable.
/// </summary>
public static class ItunesSearchApi
{
    public const int MaximumHits = 25;
    public const string ProviderId = "apple-itunes-search";
    public const string CatalogVersion = "mac-software-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 12
    };

    public static Uri CreateSoftwareSearchUri(string query, string countryCode)
    {
        query = ValidateQuery(query);
        countryCode = NormalizeCountryCode(countryCode);
        var encoded = Uri.EscapeDataString(query);
        return new Uri(
            $"https://itunes.apple.com/search?term={encoded}&country={countryCode}" +
            $"&media=software&entity=macSoftware&limit={MaximumHits}&explicit=No",
            UriKind.Absolute);
    }

    public static IReadOnlyList<ItunesSoftwareSearchHit> ParseSearchResponse(ReadOnlySpan<byte> json)
    {
        SearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SearchResponse>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die iTunes-Search-Antwort ist kein gültiges JSON-Objekt.", exception);
        }

        if (response?.Results is null || response.ResultCount < 0 ||
            response.ResultCount > MaximumHits || response.Results.Count > MaximumHits ||
            response.ResultCount != response.Results.Count)
            throw new InvalidDataException("Die iTunes-Search-Antwort hat ein unerwartetes Format.");

        var hits = new List<ItunesSoftwareSearchHit>(response.Results.Count);
        var trackIds = new HashSet<long>();
        foreach (var item in response.Results)
        {
            if (item is null || item.TrackId <= 0 || !trackIds.Add(item.TrackId) ||
                !string.Equals(item.WrapperType, "software", StringComparison.Ordinal) ||
                item.Kind is not ("software" or "mac-software") ||
                !IsSafeText(item.TrackName, 180) ||
                (!IsSafeText(item.SellerName, 180) && !IsSafeText(item.ArtistName, 180)) ||
                !IsSafeText(item.ArtworkUrl100, 2_048) || !IsSafeText(item.TrackViewUrl, 2_048))
                continue;

            hits.Add(new ItunesSoftwareSearchHit(
                item.TrackId,
                item.TrackName!.Trim(),
                (Clean(item.SellerName, 180) ?? item.ArtistName!.Trim()),
                Clean(item.BundleId, 255),
                Clean(item.PrimaryGenreName, 120),
                item.ArtworkUrl100!.Trim(),
                Clean(item.ArtworkUrl512, 2_048),
                item.TrackViewUrl!.Trim()));
        }
        return hits;
    }

    public static string ValidateQuery(string? query)
    {
        var value = query?.Trim() ?? string.Empty;
        if (value.Length is < 2 or > 160 || value.Any(char.IsControl))
            throw new ArgumentException("The app name is not suitable for an iTunes Search request.", nameof(query));
        return value;
    }

    public static string NormalizeCountryCode(string? countryCode)
    {
        var value = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        return value.Length == 2 && value.All(char.IsAsciiLetter) ? value : "US";
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string? Clean(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength) ? value!.Trim() : null;

    private sealed class SearchResponse
    {
        [JsonPropertyName("resultCount")]
        public int ResultCount { get; init; } = -1;

        [JsonPropertyName("results")]
        public List<SearchHit?>? Results { get; init; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("wrapperType")]
        public string? WrapperType { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("trackId")]
        public long TrackId { get; init; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; init; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; init; }

        [JsonPropertyName("sellerName")]
        public string? SellerName { get; init; }

        [JsonPropertyName("bundleId")]
        public string? BundleId { get; init; }

        [JsonPropertyName("primaryGenreName")]
        public string? PrimaryGenreName { get; init; }

        [JsonPropertyName("artworkUrl100")]
        public string? ArtworkUrl100 { get; init; }

        [JsonPropertyName("artworkUrl512")]
        public string? ArtworkUrl512 { get; init; }

        [JsonPropertyName("trackViewUrl")]
        public string? TrackViewUrl { get; init; }
    }
}
