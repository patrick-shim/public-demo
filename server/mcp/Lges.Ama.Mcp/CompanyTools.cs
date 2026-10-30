using System.ComponentModel;
using Lges.Ama.Mcp.Hosting;
using Lges.Ama.PublicData;
using ModelContextProtocol.Server;

namespace Lges.Ama.Mcp;

[McpServerToolType]
public sealed class NewsTools(NewsProvider provider, ILogger<NewsTools> logger)
{
    [McpServerTool(Name = "search_company_news", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Searches LG Energy Solution official news and GDELT with source links and explicit partial-result notices.")]
    public Task<SearchResult> SearchAsync(
        [Description("Optional topic such as ESS or 실적. Company scope is fixed to LG Energy Solution.")] string query = "",
        [Description("Lookback in days, from 1 through 90.")] int days = 30,
        [Description("Maximum results, from 1 through 15.")] int limit = 8,
        CancellationToken cancellationToken = default) => McpToolExecution.RunAsync("news.search", logger,
            ct => provider.SearchAsync(query, days, limit, ct), cancellationToken);

    [McpServerTool(Name = "read_news_article", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Reads bounded text from an official LG Energy Solution newsroom article returned by search.")]
    public Task<OfficialPage> ReadAsync(
        [Description("The positive articleId returned by search_company_news.")] int articleId,
        CancellationToken cancellationToken = default) => McpToolExecution.RunAsync("news.read", logger,
            ct => provider.ReadArticleAsync(articleId, ct), cancellationToken);
}

[McpServerToolType]
public sealed class IrTools(CompanyProvider provider, ILogger<IrTools> logger)
{
    [McpServerTool(Name = "get_ir_materials", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Finds official LG Energy Solution earnings materials; returns document links without claiming to have read PDFs.")]
    public Task<OfficialPage> MaterialsAsync(
        [Description("Reporting year, or 0 for all available years.")] int year = 0,
        [Description("Maximum links, from 1 through 25.")] int limit = 10,
        CancellationToken cancellationToken = default) => McpToolExecution.RunAsync("ir.materials", logger,
            ct => provider.MaterialsAsync(year, limit, ct), cancellationToken);

    [McpServerTool(Name = "get_company_overview", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Reads LG Energy Solution company overview from its official website.")]
    public Task<OfficialPage> OverviewAsync(
        [Description("Language: ko or en.")] string language = "ko",
        CancellationToken cancellationToken = default) => McpToolExecution.RunAsync("ir.overview", logger,
            ct => provider.OverviewAsync(language, ct), cancellationToken);
}

[McpServerToolType]
public sealed class MarketTools(CompanyProvider provider, ILogger<MarketTools> logger)
{
    [McpServerTool(Name = "get_stock_quote", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Reads KOSPI 373220 from LG Energy Solution's embedded IR stock page, with KRW, KST observation time, and delay caveats.")]
    public Task<QuoteResult> QuoteAsync(CancellationToken cancellationToken = default) =>
        McpToolExecution.RunAsync("market.quote", logger, provider.QuoteAsync, cancellationToken);
}
