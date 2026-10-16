using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ScanAlways.Updater;

/// <summary>HTTPS plumbing shared by the checker and the installer.</summary>
public static class UpdateHttp
{
    public static HttpClient CreateClient(ReleaseVersion current)
    {
        var handler = new SocketsHttpHandler
        {
            // GitHub serves release assets through a redirect to its download CDN.
            // HttpClient never follows an https-to-http redirect; hosts are checked after.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WatchLattice-Updater", current.ToString()));
        return client;
    }

    /// <summary>GitHub's own hosts: the API, release pages, and the release-asset CDN.</summary>
    public static bool IsGitHubHost(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo)
        && (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The response is larger than expected.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(input, buffer, maxBytes, null, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>Downloads a small text asset (the checksum file).</summary>
    public static async Task<string> DownloadTextAsync(HttpClient http, Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await GetCheckedAsync(http, url, cancellationToken);
        return Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, maxBytes, cancellationToken));
    }

    /// <summary>Streams an asset into <paramref name="destination"/> and returns its SHA-256 as lowercase hex.</summary>
    public static async Task<string> DownloadToAsync(HttpClient http, Uri url, Stream destination, long maxBytes,
        CancellationToken cancellationToken)
    {
        using var response = await GetCheckedAsync(http, url, cancellationToken);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The package is larger than allowed.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyBoundedAsync(input, destination, maxBytes, hash, cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<HttpResponseMessage> GetCheckedAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        if (!IsGitHubHost(url)) throw new InvalidDataException($"Refusing to download from {url.Host}.");
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri;
            if (final is null || !IsGitHubHost(final))
                throw new InvalidDataException($"The download was redirected to an unexpected host ({final?.Host ?? "unknown"}).");
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maxBytes, IncrementalHash? hash,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidDataException("The download is larger than allowed.");
            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
