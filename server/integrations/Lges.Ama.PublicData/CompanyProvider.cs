using System.Globalization;
using AngleSharp.Html.Parser;

namespace Lges.Ama.PublicData;

public sealed class CompanyProvider(PublicHttp http, TimeProvider clock)
{
    public async Task<QuoteResult> QuoteAsync(CancellationToken ct)
    {
        try { return ContentParser.ParseQuote(await http.GetAsync(new Uri(ContentParser.StockSource), TimeSpan.FromSeconds(60), ct), clock.GetUtcNow()); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            return new QuoteResult("unavailable", "373220", "KRW", null, null, null, null, clock.GetUtcNow(), ContentParser.StockSource,
                "LG Energy Solution IR / GSIFN", "The public quote is unavailable or its format changed. Do not invent a price. Refer the user to the official IR stock page.");
        }
    }

    public async Task<OfficialPage> OverviewAsync(string language, CancellationToken ct)
    {
        if (language is not ("ko" or "en")) throw new ArgumentException("Language must be ko or en.", nameof(language));
        var url = new Uri($"https://www.lgensol.com/{(language == "ko" ? "kr" : "en")}/company/info-outline");
        return await ReadPageAsync(url, ct);
    }

    public async Task<OfficialPage> MaterialsAsync(int year, int limit, CancellationToken ct)
    {
        if (year != 0 && (year < 2022 || year > clock.GetUtcNow().Year)) throw new ArgumentOutOfRangeException(nameof(year));
        if (limit is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(limit));
        var page = await ReadPageAsync(new Uri("https://www.lgensol.com/kr/investors/earnings-announcement"), ct);
        var links = page.Links.Where(l => year == 0 || (l.Title + l.Url).Contains(year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).Take(limit).ToArray();
        return page with { Links = links, Text = "Official earnings-material directory. The attached PDF contents have not been extracted; do not claim to have read them. "
            + (year == 0 ? "" : $"Requested year: {year}. ") + ContentParser.Bound(page.Text, 4500) };
    }

    private async Task<OfficialPage> ReadPageAsync(Uri url, CancellationToken ct)
    {
        var html = await http.GetAsync(url, TimeSpan.FromMinutes(30), ct);
        using var document = new HtmlParser().ParseDocument(html);
        var links = document.QuerySelectorAll("a[href]").Select(a =>
        {
            if (!Uri.TryCreate(url, a.GetAttribute("href"), out var target) || target.Scheme != "https" || target.Host != "www.lgensol.com") return null;
            if (!target.AbsolutePath.Contains("/upload/", StringComparison.OrdinalIgnoreCase) && !target.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
            var context = a.Closest("li")?.TextContent ?? a.TextContent;
            var group = a.Closest("ul.history-desc")?.ParentElement;
            var reportingYear = group?.QuerySelector(".history-year")?.TextContent.Trim();
            if (reportingYear is not null) context = reportingYear + " " + context;
            return new SourceItem(ContentParser.PlainText(context, 400), target.AbsoluteUri, "LG Energy Solution IR", null, "Official material link; document contents are not yet read.");
        }).OfType<SourceItem>().DistinctBy(l => l.Url).Take(100).ToArray();
        foreach (var node in document.QuerySelectorAll("script,style,noscript,nav,header,footer")) node.Remove();
        var main = document.QuerySelector(".content-area") ?? document.QuerySelector("main") ?? document.QuerySelector("#container") ?? document.Body;
        return new OfficialPage(document.Title ?? "LG Energy Solution", url.AbsoluteUri, clock.GetUtcNow(), ContentParser.PlainText(main?.InnerHtml ?? "", 10000), links);
    }
}
