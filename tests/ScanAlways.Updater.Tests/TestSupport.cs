using System.IO.Compression;
using System.Net;
using System.Text;

namespace ScanAlways.Updater.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wl-updater-" + Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string content)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Serves canned responses by URL; optionally pretends a redirect landed somewhere else.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();

    public List<Uri> Requests { get; } = new();

    public void Add(string url, byte[] body, string? finalUrl = null) => _routes[url] = () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        if (finalUrl is not null) response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        return response;
    };

    public void Add(string url, string body) => Add(url, Encoding.UTF8.GetBytes(body));

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    public void Add(string url, HttpStatusCode status) => _routes[url] = () => new HttpResponseMessage(status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var route = _routes.GetValueOrDefault(request.RequestUri!.ToString(), NotFound);
        var response = route();
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

internal sealed class FakeReleaseSource : IReleaseSource
{
    public Func<ReleaseInfo?> Next { get; set; } = () => null;

    public int Calls { get; private set; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Next());
    }
}

internal sealed class FakeServiceControl : IHubServiceControl
{
    public bool Running { get; set; }
    public List<string> Calls { get; } = new();

    public bool IsRunning() => Running;

    public void Stop(TimeSpan timeout)
    {
        Calls.Add("stop");
        Running = false;
    }

    public void Start(TimeSpan timeout)
    {
        Calls.Add("start");
        Running = true;
    }
}

internal static class Releases
{
    public const string Repository = "notbrianbob/WatchLattice";

    public static string DownloadUrl(string version, string name) =>
        $"https://github.com/{Repository}/releases/download/v{version}/{name}";

    public static ReleaseInfo Make(string version, bool package = true, bool checksums = true, string? packageUrl = null)
    {
        var assets = new List<ReleaseAsset>();
        var packageName = $"WatchLattice-{version}-win-x64.zip";
        if (package) assets.Add(new(packageName, new Uri(packageUrl ?? DownloadUrl(version, packageName)), 100));
        if (checksums) assets.Add(new("SHA256SUMS.txt", new Uri(DownloadUrl(version, "SHA256SUMS.txt")), 90));
        return new ReleaseInfo("v" + version, "WatchLattice " + version,
            new Uri($"https://github.com/{Repository}/releases/tag/v{version}"), DateTimeOffset.UtcNow, assets);
    }

    /// <summary>A release zip with Hub/ and Tray/ folders.</summary>
    public static byte[] Package(string marker, IDictionary<string, string>? extra = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
            Add("Hub/ScanAlways.Protection.Service.exe", "hub " + marker);
            Add("Hub/ScanAlways.Common.dll", "common " + marker);
            Add("Hub/appsettings.json", "{ \"release\": \"" + marker + "\" }");
            Add("Tray/ScanAlways.Tray.exe", "tray " + marker);
            foreach (var (name, content) in extra ?? new Dictionary<string, string>()) Add(name, content);
        }
        return buffer.ToArray();
    }

    public static string Sha256(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Sha256Sums.Compute(stream);
    }
}
