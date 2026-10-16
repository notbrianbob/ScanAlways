namespace ScanAlways.Updater;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(string TagName, string Name, Uri HtmlUrl, DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets, bool Prerelease = false, bool Draft = false);

/// <summary>Where releases come from. The hub uses GitHub Releases; tests use fakes.</summary>
public interface IReleaseSource
{
    /// <summary>The latest published, non-prerelease release, or null when there is none.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken);
}
