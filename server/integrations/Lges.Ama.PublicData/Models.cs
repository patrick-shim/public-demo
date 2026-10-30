namespace Lges.Ama.PublicData;

public sealed record SourceItem(string Title, string Url, string Source, string? PublishedAt, string Summary);
public sealed record SearchResult(string Status, DateTimeOffset RetrievedAt, IReadOnlyList<SourceItem> Items, IReadOnlyList<string> Notices);
public sealed record QuoteResult(string Status, string Symbol, string Currency, decimal? Price, decimal? Change, decimal? ChangePercent,
    string? ObservedAt, DateTimeOffset RetrievedAt, string SourceUrl, string Source, string FreshnessNote);
public sealed record OfficialPage(string Title, string Url, DateTimeOffset RetrievedAt, string Text, IReadOnlyList<SourceItem> Links);
