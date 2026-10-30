using System.Net;
using Lges.Ama.PublicData;

namespace Lges.Ama.PublicData.Tests;

[TestClass]
public sealed class PublicDataTests
{
    private const string Quote = "<div id='stock_info'><h4 class='name'>LG에너지솔루션(373220)</h4><div class='date'>2026-10-27 15:30 기준</div></div><dd id='cur_close'>100,000</dd><dd id='updown' class='down'>1,000</dd><dd id='percent'>-0.99%</dd>";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public void QuotePreservesCurrencyObservationAndNegativeChange()
    {
        var result=ContentParser.ParseQuote(Quote, Now);
        Assert.AreEqual(100000m,result.Price);
        Assert.AreEqual(-1000m,result.Change);
        Assert.AreEqual("KRW",result.Currency);
        StringAssert.Contains(result.ObservedAt!,"+09:00");
        StringAssert.Contains(result.FreshnessNote,"not a streaming");
    }
    [TestMethod] public void StaleQuoteIsLabeled() => Assert.AreEqual("stale",ContentParser.ParseQuote(Quote,Now.AddDays(8)).Status);
    [TestMethod] public void WrongCompanyIsRejected() => Assert.ThrowsExactly<InvalidDataException>(()=>ContentParser.ParseQuote(Quote.Replace("373220","051910",StringComparison.Ordinal),Now));
    [TestMethod] public void FutureTimestampIsRejected() => Assert.ThrowsExactly<InvalidDataException>(()=>ContentParser.ParseQuote(Quote,Now.AddDays(-5)));
    [TestMethod] public void MissingPriceIsRejected() => Assert.ThrowsExactly<InvalidDataException>(()=>ContentParser.ParseQuote(Quote.Replace("100,000","N/A",StringComparison.Ordinal),Now));
    [TestMethod] public void ScriptsAreNotReturned() => Assert.AreEqual("News text",ContentParser.PlainText("<script>steal()</script><p>News text</p>"));
    [TestMethod]
    [DataRow("http://inside.lgensol.com/")]
    [DataRow("https://inside.lgensol.com.evil.test/")]
    [DataRow("https://127.0.0.1/")]
    [DataRow("https://user@inside.lgensol.com/")]
    [DataRow("https://inside.lgensol.com:8443/")]
    public void ArbitraryDestinationsAreRejected(string url) => Assert.IsFalse(PublicHttp.IsAllowed(new Uri(url)));
    [TestMethod]
    public async Task QuoteOutageReturnsNoInventedNumber()
    {
        using var client=new HttpClient(new StubHandler(_=>new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var provider=new CompanyProvider(new PublicHttp(client,TimeProvider.System),TimeProvider.System);
        var result=await provider.QuoteAsync(CancellationToken.None);
        Assert.AreEqual("unavailable",result.Status); Assert.IsNull(result.Price);
    }
    [TestMethod]
    public async Task RedirectIsNotFollowed()
    {
        using var client=new HttpClient(new StubHandler(_=>new HttpResponseMessage(HttpStatusCode.Redirect)));
        var http=new PublicHttp(client,TimeProvider.System);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>http.GetAsync(new Uri(ContentParser.StockSource),TimeSpan.Zero,CancellationToken.None));
    }
    [TestMethod]
    public async Task GdeltFailureKeepsOfficialResultsAndMarksPartial()
    {
        using var client=new HttpClient(new StubHandler(request=> request.RequestUri!.Host=="api.gdeltproject.org"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[{\"id\":12,\"date_gmt\":\"2026-10-27T00:00:00\",\"link\":\"https://inside.lgensol.com/test\",\"title\":{\"rendered\":\"Earnings\"},\"excerpt\":{\"rendered\":\"Company statement\"}}]")}));
        var provider=new NewsProvider(new PublicHttp(client,TimeProvider.System),TimeProvider.System);
        var result=await provider.SearchAsync("실적",30,5,CancellationToken.None);
        Assert.AreEqual("partial",result.Status); Assert.HasCount(1,result.Items);
        Assert.IsTrue(result.Notices.Any(n=>n.Contains("GDELT is unavailable",StringComparison.Ordinal)));
    }
    private sealed class StubHandler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(reply(request)); }
}
