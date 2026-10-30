using System.Collections.Concurrent;
using System.Net;

namespace Lges.Ama.PublicData;

// No user-selected hosts, redirects, credentials, or browser execution.
public sealed class PublicHttp(HttpClient client, TimeProvider clock)
{
    private sealed record Cached(DateTimeOffset At, string Content);
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.lgensol.com", "inside.lgensol.com", "api.gdeltproject.org", "ir.gsifn.io"
    };

    public static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo) && Hosts.Contains(uri.Host);

    public async Task<string> GetAsync(
        Uri uri,
        TimeSpan lifetime,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        if (!IsAllowed(uri)) throw new ArgumentException("Only approved public sources may be requested.", nameof(uri));
        cancellationToken.ThrowIfCancellationRequested();
        if (requestTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (_cache.TryGetValue(uri.AbsoluteUri, out var cached) && clock.GetUtcNow() - cached.At < lifetime)
            return cached.Content;
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Carry the optional source
        // budget through the body read as well, while preserving cancellation from the caller.
        using var timeoutCancellation = requestTimeout is { } budget
            ? new CancellationTokenSource(budget, clock)
            : null;
        using var linkedCancellation = timeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var requestCancellation = linkedCancellation?.Token ?? cancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LgesAmaDemo/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Public source redirected; review its new endpoint before enabling it.");
        response.EnsureSuccessStatusCode();
        const int maximum = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Public response exceeded the limit.");
        await using var input = await response.Content.ReadAsStreamAsync(requestCancellation);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, requestCancellation)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Public response exceeded the limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), requestCancellation);
        }
        var content = System.Text.Encoding.UTF8.GetString(output.ToArray());
        if (_cache.Count >= 128) _cache.Clear();
        _cache[uri.AbsoluteUri] = new Cached(clock.GetUtcNow(), content);
        return content;
    }
}
