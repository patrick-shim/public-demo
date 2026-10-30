using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lges.Ama.PublicData;

public sealed partial class NewsProvider(PublicHttp http, TimeProvider clock)
{
    private static readonly TimeSpan GdeltRequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan GdeltCooldown = TimeSpan.FromSeconds(30);
    private long _gdeltRetryAfterUtcTicks;

    public async Task<SearchResult> SearchAsync(string query, int days, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, 90);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 15);
        if (query.Length > 120) throw new ArgumentException("Search terms must be at most 120 characters.", nameof(query));
        var safeQuery = SearchCharacters().Replace(query, " ").Trim();
        var tasks = new[] { TrySourceAsync("LG Energy Solution newsroom", () => OfficialAsync(safeQuery, days, limit, cancellationToken), cancellationToken),
            TrySourceAsync("GDELT", () => GdeltAsync(safeQuery, days, limit, cancellationToken), cancellationToken) };
        var results = await Task.WhenAll(tasks);
        var notices = results.Where(r => r.Notice is not null).Select(r => r.Notice!).ToList();
        notices.Add("Newsroom entries are company statements. GDELT provides third-party article discovery; headlines alone do not verify a claim. Cache up to five minutes.");
        var items = results.SelectMany(r => r.Items).DistinctBy(i => i.Url).OrderByDescending(i => i.PublishedAt).Take(limit).ToArray();
        return new SearchResult(results.All(r => r.Notice is not null) ? "unavailable" : notices.Count > 1 ? "partial" : "available", clock.GetUtcNow(), items, notices);
    }

    private async Task<IReadOnlyList<SourceItem>> OfficialAsync(string query, int days, int limit, CancellationToken ct)
    {
        var after = clock.GetUtcNow().AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var url = new Uri($"https://inside.lgensol.com/wp-json/wp/v2/posts?search={Uri.EscapeDataString(query)}&per_page={limit}&after={after}&orderby=date&order=desc&_fields=id,date_gmt,link,title,excerpt");
        using var data = JsonDocument.Parse(await http.GetAsync(url, TimeSpan.FromMinutes(5), ct));
        return data.RootElement.EnumerateArray().Select(p => new SourceItem(
            ContentParser.PlainText(p.GetProperty("title").GetProperty("rendered").GetString() ?? "", 250),
            SafeLink(p.GetProperty("link").GetString() ?? ""), "LG Energy Solution newsroom", p.GetProperty("date_gmt").GetString() + "Z",
            ContentParser.PlainText(p.GetProperty("excerpt").GetProperty("rendered").GetString() ?? "", 700)
                + $" [articleId: {p.GetProperty("id").GetInt32()}]" )).ToArray();
    }

    private async Task<IReadOnlyList<SourceItem>> GdeltAsync(string query, int days, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _gdeltRetryAfterUtcTicks))
            throw new HttpRequestException("GDELT is temporarily unavailable during its short retry cooldown.");
        var terms = "(\"LG Energy Solution\" OR \"LG에너지솔루션\")" + (query.Length == 0 ? "" : $" \"{query}\"");
        var url = new Uri($"https://api.gdeltproject.org/api/v2/doc/doc?query={Uri.EscapeDataString(terms)}&mode=artlist&format=json&sort=datedesc&timespan={days}d&maxrecords={limit}");
        string content;
        try
        {
            content = await http.GetAsync(url, TimeSpan.FromMinutes(5), ct, GdeltRequestTimeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _gdeltRetryAfterUtcTicks, clock.GetUtcNow().Add(GdeltCooldown).UtcTicks);
            throw;
        }
        catch (HttpRequestException exception) when (!ct.IsCancellationRequested
            && exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Interlocked.Exchange(ref _gdeltRetryAfterUtcTicks, clock.GetUtcNow().Add(GdeltCooldown).UtcTicks);
            throw;
        }
        using var data = JsonDocument.Parse(content);
        if (!data.RootElement.TryGetProperty("articles", out var articles)) return [];
        return articles.EnumerateArray().Select(p => new SourceItem(ContentParser.Bound(p.GetProperty("title").GetString() ?? "", 250),
            SafeLink(p.GetProperty("url").GetString() ?? ""), "GDELT / " + p.GetProperty("domain").GetString(),
            ParseGdeltTime(p.GetProperty("seendate").GetString()), "Discovery headline only; GDELT seen time is not necessarily the publication time.")).ToArray();
    }

    public async Task<OfficialPage> ReadArticleAsync(int articleId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(articleId, 1);
        var url = new Uri($"https://inside.lgensol.com/wp-json/wp/v2/posts/{articleId}?_fields=date_gmt,link,title,content");
        using var data = JsonDocument.Parse(await http.GetAsync(url, TimeSpan.FromMinutes(10), ct));
        var root = data.RootElement;
        return new OfficialPage(ContentParser.PlainText(root.GetProperty("title").GetProperty("rendered").GetString() ?? "", 250),
            SafeLink(root.GetProperty("link").GetString() ?? ""), clock.GetUtcNow(),
            "Published (UTC): " + root.GetProperty("date_gmt").GetString() + "Z. " + ContentParser.PlainText(root.GetProperty("content").GetProperty("rendered").GetString() ?? "", 12000), []);
    }

    private static async Task<(IReadOnlyList<SourceItem> Items, string? Notice)> TrySourceAsync(string name, Func<Task<IReadOnlyList<SourceItem>>> action, CancellationToken ct)
    {
        try { return (await action(), null); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        { return ([], $"{name} is unavailable for this request. No results were invented; retry later or use the other source."); }
    }

    private static string? ParseGdeltTime(string? value) => DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var time) ? time.ToString("O", CultureInfo.InvariantCulture) : null;
    private static string SafeLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
        ? uri.AbsoluteUri : throw new InvalidDataException("Invalid source link.");
    [GeneratedRegex(@"[^\p{L}\p{N}\s-]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SearchCharacters();
}
