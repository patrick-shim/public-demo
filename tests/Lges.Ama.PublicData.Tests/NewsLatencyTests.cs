using System.Net;
using Lges.Ama.PublicData;

namespace Lges.Ama.PublicData.Tests;

[TestClass]
public sealed class NewsLatencyTests
{
    private const string OfficialNews = "[{\"id\":12,\"date_gmt\":\"2026-10-27T00:00:00\",\"link\":\"https://inside.lgensol.com/test\",\"title\":{\"rendered\":\"Earnings\"},\"excerpt\":{\"rendered\":\"Company statement\"}}]";

    [TestMethod]
    public async Task RequestBudgetCancelsSlowBodyAfterHeadersArrive()
    {
        using var client = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) })));
        var http = new PublicHttp(client, TimeProvider.System);
        using var callerCancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => http.GetAsync(
            new Uri("https://api.gdeltproject.org/api/v2/doc/doc"),
            TimeSpan.Zero,
            callerCancellation.Token,
            TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsFalse(callerCancellation.IsCancellationRequested);
    }

    [TestMethod]
    public async Task TimedOutGdeltDoesNotDelayNextTopicAndPreservesOfficialNews()
    {
        var gdeltRequests = 0;
        using var client = new HttpClient(new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "api.gdeltproject.org") return Task.FromResult(OfficialResponse());
            Interlocked.Increment(ref gdeltRequests);
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated source timeout."));
        }));
        var provider = new NewsProvider(new PublicHttp(client, TimeProvider.System), TimeProvider.System);

        var first = await provider.SearchAsync("ESS", 30, 5, CancellationToken.None);
        var second = await provider.SearchAsync("실적", 30, 5, CancellationToken.None);

        Assert.AreEqual(1, gdeltRequests);
        Assert.AreEqual("partial", first.Status);
        Assert.AreEqual("partial", second.Status);
        Assert.HasCount(1, second.Items);
        Assert.IsTrue(second.Notices.Any(notice => notice.Contains("GDELT is unavailable", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RateLimitedGdeltRecoversAfterThirtySecondCooldown()
    {
        var clock = new AdjustableClock();
        var gdeltRequests = 0;
        using var client = new HttpClient(new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "api.gdeltproject.org") return Task.FromResult(OfficialResponse());
            var attempt = Interlocked.Increment(ref gdeltRequests);
            return Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"articles\":[]}") });
        }));
        var provider = new NewsProvider(new PublicHttp(client, clock), clock);

        Assert.AreEqual("partial", (await provider.SearchAsync("ESS", 30, 5, CancellationToken.None)).Status);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.AreEqual("partial", (await provider.SearchAsync("실적", 30, 5, CancellationToken.None)).Status);
        Assert.AreEqual(1, gdeltRequests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("available", (await provider.SearchAsync("battery", 30, 5, CancellationToken.None)).Status);
        Assert.AreEqual(2, gdeltRequests);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndDoesNotStartCooldown()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gdeltRequests = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host != "api.gdeltproject.org") return OfficialResponse();
            if (Interlocked.Increment(ref gdeltRequests) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"articles\":[]}") };
        }));
        var provider = new NewsProvider(new PublicHttp(client, TimeProvider.System), TimeProvider.System);
        using var callerCancellation = new CancellationTokenSource();

        var first = provider.SearchAsync("ESS", 30, 5, callerCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callerCancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        var next = await provider.SearchAsync("실적", 30, 5, CancellationToken.None);

        Assert.AreEqual(2, gdeltRequests);
        Assert.AreEqual("available", next.Status);
    }

    [TestMethod]
    public async Task OtherSourceFailuresDoNotStartCooldown()
    {
        var gdeltRequests = 0;
        using var client = new HttpClient(new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "api.gdeltproject.org") return Task.FromResult(OfficialResponse());
            Interlocked.Increment(ref gdeltRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var provider = new NewsProvider(new PublicHttp(client, TimeProvider.System), TimeProvider.System);

        await provider.SearchAsync("ESS", 30, 5, CancellationToken.None);
        await provider.SearchAsync("실적", 30, 5, CancellationToken.None);

        Assert.AreEqual(2, gdeltRequests);
    }

    private static HttpResponseMessage OfficialResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(OfficialNews)
    };

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            reply(request, cancellationToken);
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
