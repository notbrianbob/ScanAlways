using System.Net;
using System.Text;
using Xunit;

namespace ScanAlways.Updater.Tests;

public class ReleaseSourceTests
{
    private const string Latest = "https://api.github.com/repos/notbrianbob/WatchLattice/releases/latest";

    private const string Sample = """
        {
          "tag_name": "v0.2.0",
          "name": "WatchLattice 0.2.0",
          "html_url": "https://github.com/notbrianbob/WatchLattice/releases/tag/v0.2.0",
          "published_at": "2026-10-20T12:00:00Z",
          "draft": false,
          "prerelease": false,
          "body": "notes",
          "assets": [
            { "name": "WatchLattice-0.2.0-win-x64.zip", "size": 1234,
              "browser_download_url": "https://github.com/notbrianbob/WatchLattice/releases/download/v0.2.0/WatchLattice-0.2.0-win-x64.zip" },
            { "name": "SHA256SUMS.txt", "size": 99,
              "browser_download_url": "https://github.com/notbrianbob/WatchLattice/releases/download/v0.2.0/SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsTheFieldsTheUpdaterUses()
    {
        var release = GitHubReleaseSource.Parse(Encoding.UTF8.GetBytes(Sample));

        Assert.Equal("v0.2.0", release.TagName);
        Assert.Equal("WatchLattice 0.2.0", release.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(1234, release.Assets[0].Size);
    }

    [Fact]
    public void Parse_RejectsNonHttpsLinks()
    {
        var json = Sample.Replace("https://github.com/notbrianbob/WatchLattice/releases/tag", "http://github.com/notbrianbob/WatchLattice/releases/tag");

        Assert.Throws<InvalidDataException>(() => GitHubReleaseSource.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task GetLatest_ReturnsNull_WhenTheRepositoryHasNoRelease()
    {
        var handler = new FakeHttpHandler();
        handler.Add(Latest, HttpStatusCode.NotFound);

        var release = await new GitHubReleaseSource(new HttpClient(handler), Releases.Repository).GetLatestAsync(default);

        Assert.Null(release);
    }

    [Fact]
    public async Task GetLatest_ParsesTheApiResponse_AndFailsOnServerErrors()
    {
        var handler = new FakeHttpHandler();
        handler.Add(Latest, Sample);
        Assert.Equal("v0.2.0", (await new GitHubReleaseSource(new HttpClient(handler), Releases.Repository).GetLatestAsync(default))!.TagName);

        handler.Add(Latest, HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubReleaseSource(new HttpClient(handler), Releases.Repository).GetLatestAsync(default));
    }

    [Theory]
    [InlineData("notbrianbob/WatchLattice", true)]
    [InlineData("owner/repo.name_x-1", true)]
    [InlineData("owner", false)]
    [InlineData("owner/../other", false)]
    [InlineData("https://github.com/owner/repo", false)]
    [InlineData("owner/repo/extra", false)]
    public void Repository_MustBeOwnerSlashName(string repository, bool valid)
    {
        var options = new UpdaterOptions { Repository = repository };
        if (valid) Assert.Equal(repository, options.ResolveRepository());
        else Assert.Throws<InvalidDataException>(() => options.ResolveRepository());
    }
}
