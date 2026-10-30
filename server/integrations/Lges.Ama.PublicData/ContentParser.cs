using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Lges.Ama.PublicData;

public static partial class ContentParser
{
    public static string PlainText(string html, int maximum = 2000)
    {
        using var document = new HtmlParser().ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script,style,noscript,nav,header,footer")) element.Remove();
        return Bound(Whitespace().Replace(document.Body?.TextContent ?? string.Empty, " ").Trim(), maximum);
    }

    public static string Bound(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "…";

    public static QuoteResult ParseQuote(string html, DateTimeOffset retrievedAt)
    {
        using var document = new HtmlParser().ParseDocument(html);
        if (!(document.QuerySelector("#stock_info .name")?.TextContent.Contains("373220", StringComparison.Ordinal) ?? false))
            throw new InvalidDataException("The stock source did not identify LG Energy Solution.");
        var observed = document.QuerySelector("#stock_info .date")?.TextContent.Trim().Replace(" 기준", "", StringComparison.Ordinal);
        if (!DateTime.TryParseExact(observed, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            throw new InvalidDataException("The stock timestamp is missing or invalid.");
        var at = new DateTimeOffset(localTime, TimeSpan.FromHours(9));
        if (at > retrievedAt.AddMinutes(5)) throw new InvalidDataException("The stock timestamp is in the future.");
        decimal ReadNumber(string selector)
        {
            var value = document.QuerySelector(selector)?.TextContent.Trim().Replace(",", "", StringComparison.Ordinal).Replace("%", "", StringComparison.Ordinal);
            if (!decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                throw new InvalidDataException("The stock source changed its format.");
            return number;
        }
        var price = ReadNumber("#cur_close");
        if (price <= 0) throw new InvalidDataException("The stock price is invalid.");
        var change = ReadNumber("#updown");
        if (document.QuerySelector("#updown")?.ClassList.Contains("down") == true) change = -Math.Abs(change);
        var stale = retrievedAt - at > TimeSpan.FromDays(4);
        return new QuoteResult(stale ? "stale" : "available", "373220", "KRW", price, change, ReadNumber("#percent"),
            at.ToString("O", CultureInfo.InvariantCulture), retrievedAt, StockSource, "LG Energy Solution IR / GSIFN",
            "Company IR page snapshot; delay is unspecified and this is not a streaming or trading feed. Cache up to 60 seconds. Always show observedAt in KST."
                + (stale ? " The observation is more than four calendar days old." : ""));
    }

    public const string StockSource = "https://ir.gsifn.io/lgensol/ir_current.html?koreng=1";

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Whitespace();
}
