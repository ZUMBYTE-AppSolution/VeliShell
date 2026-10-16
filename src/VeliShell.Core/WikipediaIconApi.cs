using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeliShell.Core;

public sealed record WikipediaLogoCandidate(string AppName, Uri ArticleUrl, string FileTitle);
public sealed record WikipediaArticle(string AppName, Uri Url);
public sealed record WikimediaLogo(string FileTitle, string Creator, string LicenseName,
    Uri? LicenseUrl, Uri SourceUrl, Uri ImageUrl);

/// <summary>Network-free MediaWiki request construction and logo/metadata validation.</summary>
public static class WikipediaIconApi
{
    public const string ProviderId = "wikipedia-commons";
    public const string CatalogVersion = "mediawiki-v1";
    public const int MaximumFiles = 8;
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
        { "app", "application", "software", "media", "player", "exe", "desktop", "for", "the" };
    private const string ImageInfoParameters = "&prop=imageinfo&iiprop=url%7Csize%7Cmime%7Cextmetadata" +
        "&iiurlwidth=512&iiurlheight=512" +
        "&iiextmetadatafilter=Artist%7CCredit%7CLicenseShortName%7CLicenseUrl%7CUsageTerms%7CCopyrighted&maxlag=5";

    public static Uri CreateArticleSearchUri(string query, string language)
    {
        query = ValidateQuery(query);
        if (language is not ("de" or "en")) throw new ArgumentException("Unsupported Wikipedia language.");
        return new Uri($"https://{language}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2" +
            $"&generator=search&gsrsearch={Uri.EscapeDataString(query)}&gsrnamespace=0&gsrlimit=4" +
            "&prop=images&imlimit=30&maxlag=5");
    }

    public static Uri CreateFileInfoUri(IEnumerable<string> titles)
    {
        var files = titles.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles).ToArray();
        if (files.Length == 0 || files.Any(file => !SafeFileTitle(file)))
            throw new ArgumentException("Invalid Commons file titles.");
        return new Uri("https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2" +
            ImageInfoParameters + "&titles=" + Uri.EscapeDataString(string.Join('|', files)));
    }

    public static Uri CreateLogoSearchUri(string query) => new(
        "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2" +
        "&generator=search&gsrnamespace=6&gsrlimit=8&gsrsearch=" +
        Uri.EscapeDataString(Normalize(ValidateQuery(query)) + " logo") + ImageInfoParameters);

    public static WikipediaArticle? ParseArticle(ReadOnlyMemory<byte> json, string query, string language)
    {
        _ = CreateArticleSearchUri(query, language);
        using var document = Parse(json);
        var title = Pages(document).Where(page => page.ValueKind == JsonValueKind.Object &&
            page.TryGetProperty("ns", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.TryGetInt32(out var space) && space == 0)
            .Select(page => Text(page, "title", 180)).OfType<string>()
            .OrderByDescending(value => Normalize(value) == Normalize(query)).FirstOrDefault();
        return title is null ? null : new(title, new Uri($"https://{language}.wikipedia.org/wiki/" +
            Uri.EscapeDataString(title.Replace(' ', '_'))));
    }

    public static IReadOnlyList<WikipediaLogoCandidate> ParseArticleImages(
        ReadOnlyMemory<byte> json, string query, string language)
    {
        _ = CreateArticleSearchUri(query, language);
        using var document = Parse(json);
        var results = new List<WikipediaLogoCandidate>();
        foreach (var page in Pages(document).Take(4))
        {
            var title = Text(page, "title", 180);
            if (title is null || !page.TryGetProperty("ns", out var ns) || ns.ValueKind != JsonValueKind.Number || !ns.TryGetInt32(out var space) || space != 0 ||
                !page.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) continue;
            foreach (var image in images.EnumerateArray().Take(30))
            {
                var name = Text(image, "title", 300);
                if (name is null || !name.Contains(':')) continue;
                name = "File:" + name[(name.IndexOf(':') + 1)..].Replace('_', ' ');
                if (!SafeFileTitle(name) || !IsPlausibleLogo(name, query)) continue;
                results.Add(new(title, new Uri($"https://{language}.wikipedia.org/wiki/" +
                    Uri.EscapeDataString(title.Replace(' ', '_'))), name));
            }
        }
        return results.OrderByDescending(result => Normalize(result.AppName) == Normalize(query))
            .DistinctBy(result => result.FileTitle, StringComparer.OrdinalIgnoreCase).Take(MaximumFiles).ToArray();
    }

    public static IReadOnlyList<WikimediaLogo> ParseFileInfo(ReadOnlyMemory<byte> json, string? query = null)
    {
        using var document = Parse(json);
        var results = new List<WikimediaLogo>();
        foreach (var page in Pages(document).Take(MaximumFiles))
        {
            var title = Text(page, "title", 300)?.Replace('_', ' ');
            if (title is null || !SafeFileTitle(title) || query is not null && !IsPlausibleLogo(title, query) ||
                !page.TryGetProperty("imageinfo", out var infos) || infos.ValueKind != JsonValueKind.Array ||
                infos.GetArrayLength() != 1) continue;
            var info = infos[0];
            // Wordmarks and screenshots are not useful Dock app icons.
            if (info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("width", out var widthValue) || widthValue.ValueKind != JsonValueKind.Number || !widthValue.TryGetDouble(out var width) ||
                !info.TryGetProperty("height", out var heightValue) || heightValue.ValueKind != JsonValueKind.Number || !heightValue.TryGetDouble(out var height) ||
                width < 16 || height < 16 || Math.Max(width, height) / Math.Min(width, height) > 1.5) continue;
            if (!info.TryGetProperty("extmetadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                continue;
            var license = Metadata(metadata, "LicenseShortName", 120) ?? Metadata(metadata, "UsageTerms", 120);
            var licenseUrl = LicenseUri(Metadata(metadata, "LicenseUrl", 2048));
            if (license is null || !IsSupportedFreeLicense(license, licenseUrl)) continue;
            var source = Url(Text(info, "descriptionurl", 2048));
            var image = Url(Text(info, "thumburl", 2048)) ?? Url(Text(info, "url", 2048));
            if (!IsSourceUri(source) || !IsImageUri(image)) continue;
            var creator = Metadata(metadata, "Artist", 180) ?? Metadata(metadata, "Credit", 180) ?? "";
            results.Add(new(title, creator, license, licenseUrl, source!, image!));
        }
        return results.DistinctBy(result => result.FileTitle, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(result => LogoRank(result.FileTitle)).ToArray();
    }

    public static bool IsImageUri(Uri? uri) => StandardHttps(uri) &&
        uri!.Host is "upload.wikimedia.org" or "thumb.wikimedia.org" &&
        uri.AbsolutePath.StartsWith("/wikipedia/commons/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    public static bool IsSourceUri(Uri? uri) => StandardHttps(uri) &&
        uri!.Host == "commons.wikimedia.org" && uri.AbsolutePath.StartsWith("/wiki/File:", StringComparison.Ordinal);

    public static bool IsArticleUri(Uri? uri) => StandardHttps(uri) &&
        uri!.Host is "en.wikipedia.org" or "de.wikipedia.org" &&
        uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal);

    public static string ValidateQuery(string? query)
    {
        var text = query?.Trim() ?? "";
        if (text.Length is < 2 or > 160 || text.Any(char.IsControl)) throw new ArgumentException("Invalid app name.");
        return text;
    }

    private static bool IsPlausibleLogo(string title, string query)
    {
        var name = Normalize(title[5..]);
        if (new[] { "commons logo", "wikimedia", "oojs", "symbol portal", "symbol category", "free and open source", "screenshot", "screen shot" }
            .Any(name.Contains)) return false;
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3 && !GenericWords.Contains(word)).Distinct().ToArray();
        if (words.Length == 0 || words.Count(name.Contains) < Math.Min(2, words.Length)) return false;
        return new[] { "logo", "icon", "emblem", "logomark" }.Any(name.Contains) ||
            Normalize(Path.GetFileNameWithoutExtension(title[5..])) == Normalize(query);
    }

    private static bool SafeFileTitle(string title) => title.StartsWith("File:", StringComparison.Ordinal) &&
        title.Length is > 5 and <= 300 && !title.Any(char.IsControl) && !title.Contains('|') &&
        new[] { ".svg", ".png", ".jpg", ".jpeg" }.Any(extension => title.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static int LogoRank(string title)
    {
        var name = Normalize(title);
        var score = name.Contains("icon") || name.Contains("app logo") || name.Contains("square") ? 5 : 0;
        if (Regex.IsMatch(name, @"\b\d{4}\s+\d{4}\b") ||
            new[] { "wrapped", "kids", "beta", "canary", "dev logo" }.Any(name.Contains)) score -= 10;
        return score;
    }

    public static bool IsSupportedFreeLicense(string name, Uri? url)
    {
        if (name.Length is 0 or > 120 || name.Any(char.IsControl) ||
            url is not null && (!url.IsAbsoluteUri || LicenseUri(url.AbsoluteUri) is null)) return false;
        var normalized = name.ToLowerInvariant();
        if (normalized is "public domain" or "cc0" or "cc-zero" || normalized.StartsWith("pd-")) return true;
        if (url is null || normalized.Contains("non-free") || normalized.Contains("fair use") ||
            normalized.Contains("-nc") || normalized.Contains("-nd")) return false;
        if (url.Host is "creativecommons.org" or "www.creativecommons.org")
            return url.AbsolutePath.StartsWith("/licenses/by/") || url.AbsolutePath.StartsWith("/licenses/by-sa/") ||
                   url.AbsolutePath.StartsWith("/publicdomain/zero/") || url.AbsolutePath.StartsWith("/publicdomain/mark/");
        return normalized.StartsWith("gpl") || normalized.StartsWith("lgpl") || normalized.StartsWith("gfdl") ||
               normalized is "mit" || normalized.StartsWith("apache");
    }

    private static Uri? LicenseUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Host is not ("creativecommons.org" or "www.creativecommons.org" or "gnu.org" or "www.gnu.org" or
                "opensource.org" or "www.opensource.org" or "apache.org" or "www.apache.org")) return null;
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("Wikipedia response too large.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.TryGetProperty("error", out _))
        { document.Dispose(); throw new InvalidDataException("Wikipedia API error."); }
        return document;
    }

    private static IEnumerable<JsonElement> Pages(JsonDocument document) =>
        document.RootElement.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Object &&
        query.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array
            ? pages.EnumerateArray() : [];

    private static string? Metadata(JsonElement metadata, string key, int maximum)
    {
        if (!metadata.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.Object ||
            !property.TryGetProperty("value", out var raw) || raw.ValueKind != JsonValueKind.String ||
            raw.GetString() is not { Length: > 0 and <= 16000 } text) return null;
        var plain = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(50)));
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        if (plain.Any(char.IsControl)) return null;
        return plain.Length == 0 ? null : plain[..Math.Min(plain.Length, maximum)];
    }

    private static string? Text(JsonElement element, string key, int maximum) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl)
            ? text.Trim() : null;
    private static Uri? Url(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    private static bool StandardHttps(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true } &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 2048;
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
