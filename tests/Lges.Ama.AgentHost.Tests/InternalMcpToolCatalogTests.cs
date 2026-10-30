using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class InternalMcpToolCatalogTests
{
    private static readonly string[] ExpectedTools = ["search_company_news","read_news_article","get_ir_materials","get_company_overview","get_stock_quote"];
    [TestMethod]
    public void ContractPinsExactlyTheFiveCompanyTools()
    {
        var options=new InternalMcpOptions { NewsEndpoint="https://news.example/mcp",IrEndpoint="https://ir.example/mcp",MarketEndpoint="https://market.example/mcp" };
        var tools=options.GetServers().SelectMany(s=>s.ToolContracts).ToArray();
        CollectionAssert.AreEquivalent(ExpectedTools,tools.Select(t=>t.Name).ToArray());
        Assert.IsTrue(tools.All(t=>t.SchemaSha256.Length==64));
    }
    [TestMethod]
    public void RejectsInsecureProductionEndpoint()
    {
        var options=new InternalMcpOptions {NewsEndpoint="http://localhost:5211/mcp",IrEndpoint="http://localhost:5212/mcp",MarketEndpoint="http://localhost:5213/mcp"};
        Assert.IsTrue(options.HasValidEndpoints()); Assert.IsFalse(options.HasValidEndpoints(true));
    }
    [TestMethod]
    public void RejectsMissingEndpoint() => Assert.IsFalse(new InternalMcpOptions().HasValidEndpoints(true));
}
