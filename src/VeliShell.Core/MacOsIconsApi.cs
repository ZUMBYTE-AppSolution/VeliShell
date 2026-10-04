using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.Core;

public sealed record MacOsIconsApiHit(
    string AppName,
    string? LowResPngUrl,
    string? IcnsUrl,
    string? IosUrl,
    string? Category,
    string Credit,
    string? UploadedBy,
    string? CreditUrl,
    long Downloads);

/// <summary>
/// Strict, network-free parser for the documented macOSicons.com search API.
/// Keeping it in Core makes response validation independently testable.
/// </summary>
public static class MacOsIconsApi
{
    public const int MaximumHits = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 10
    };

    public static IReadOnlyList<MacOsIconsApiHit> ParseSearchResponse(ReadOnlySpan<byte> json)
    {
        SearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SearchResponse>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die macOSicons-Antwort ist kein gültiges JSON-Objekt.", exception);
        }

        if (response?.Hits is null || response.Hits.Count > MaximumHits)
            throw new InvalidDataException("Die macOSicons-Antwort hat ein unerwartetes Format.");

        var hits = new List<MacOsIconsApiHit>(response.Hits.Count);
        foreach (var item in response.Hits)
        {
            if (item is null || !IsSafeText(item.AppName, 160)) continue;
            var uploadedBy = Clean(item.UploadedBy, 160);
            var credit = Clean(item.Credit, 160) ?? uploadedBy;
            if (credit is null ||
                (string.IsNullOrWhiteSpace(item.LowResPngUrl) && string.IsNullOrWhiteSpace(item.IosUrl)))
                continue;

            hits.Add(new MacOsIconsApiHit(
                item.AppName!.Trim(),
                Clean(item.LowResPngUrl, 2_048),
                Clean(item.IcnsUrl, 2_048),
                Clean(item.IosUrl, 2_048),
                Clean(item.Category, 100),
                credit,
                uploadedBy,
                Clean(item.CreditUrl, 2_048),
                Math.Max(0, item.Downloads)));
        }
        return hits;
    }

    /// <summary>
    /// Preserves both creator fields required by the provider while avoiding
    /// duplicate names when credit and uploader identify the same person.
    /// </summary>
    public static string FormatCreatorAttribution(
        string? credit,
        string? uploadedBy,
        string? fallbackName = null)
    {
        var creators = new[] { Clean(credit, 180), Clean(uploadedBy, 180) }
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (creators.Count == 0 && Clean(fallbackName, 180) is { } fallback)
            creators.Add(fallback);
        return string.Join(" · ", creators);
    }

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string? Clean(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength) ? value!.Trim() : null;

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<SearchHit?>? Hits { get; init; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("appName")]
        public string? AppName { get; init; }
        [JsonPropertyName("lowResPngUrl")]
        public string? LowResPngUrl { get; init; }
        [JsonPropertyName("icnsUrl")]
        public string? IcnsUrl { get; init; }
        [JsonPropertyName("iOSUrl")]
        public string? IosUrl { get; init; }
        [JsonPropertyName("category")]
        public string? Category { get; init; }
        [JsonPropertyName("credit")]
        public string? Credit { get; init; }
        [JsonPropertyName("uploadedBy")]
        public string? UploadedBy { get; init; }
        [JsonPropertyName("creditUrl")]
        public string? CreditUrl { get; init; }
        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }
    }
}
