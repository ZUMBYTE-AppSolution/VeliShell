using System.Net.Http;
using System.Text.Json;

namespace VeliShell.Desktop.Services;

internal sealed record LiveWebHit(string Title, Uri Url, string? Description);

internal static class LiveWebSearchService
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<LiveWebHit> Hits)> Cache =
        new(StringComparer.Ordinal);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    internal static async Task<IReadOnlyList<LiveWebHit>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        var key = UserApiCredentials.Read(UserApiCredentials.BraveSearch);
        if (string.IsNullOrWhiteSpace(key) || query.Length is < 3 or > 200) return [];
        lock (CacheGate)
            if (Cache.TryGetValue(query, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Hits;
        var endpoint = new Uri("https://api.search.brave.com/res/v1/web/search?q=" +
                               Uri.EscapeDataString(query) + "&count=5&safesearch=moderate");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-Subscription-Token", key);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 256 * 1024)
            return [];
        var body = await BoundedHttpContent.ReadAsync(response.Content, 256 * 1024,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
        if (!document.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];
        var hits = new List<LiveWebHit>(5);
        foreach (var result in results.EnumerateArray().Take(5))
        {
            if (!result.TryGetProperty("title", out var titleValue) ||
                !result.TryGetProperty("url", out var urlValue)) continue;
            var title = titleValue.GetString();
            var urlText = urlValue.GetString();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200 ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0) continue;
            var description = result.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString() : null;
            hits.Add(new LiveWebHit(title, url,
                description is { Length: > 240 } ? description[..240] : description));
        }
        lock (CacheGate)
        {
            foreach (var expired in Cache.Where(entry => entry.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                         .Select(entry => entry.Key).ToArray()) Cache.Remove(expired);
            if (Cache.Count >= 32) Cache.Remove(Cache.Keys.First());
            Cache[query] = (DateTimeOffset.UtcNow.AddMinutes(10), hits);
        }
        return hits;
    }
}
