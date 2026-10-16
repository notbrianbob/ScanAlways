using System.Text.RegularExpressions;

namespace WatchLattice.Updater;

/// <summary>Configuration for the update checker and installer.</summary>
public sealed class UpdaterOptions
{
    public const string SectionName = "Updater";
    public const string DefaultRepository = "notbrianbob/WatchLattice";

    private static readonly Regex RepositoryPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>GitHub "owner/name" whose Releases are the update source.</summary>
    public string Repository { get; set; } = DefaultRepository;

    /// <summary>Hours between checks. Minimum 1.</summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>Seconds to wait after the hub starts before the first check.</summary>
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Builds versioned 0.0.0-dev (anything not built by the release workflow)
    /// do not check by default, so local and CI runs make no network calls.
    /// </summary>
    public bool CheckDevelopmentBuilds { get; set; }

    /// <summary>Largest release package the installer will download.</summary>
    public long MaxPackageBytes { get; set; } = 256L * 1024 * 1024;

    public string ResolveRepository()
    {
        var repository = (Repository ?? "").Trim();
        if (!RepositoryPattern.IsMatch(repository) || repository.Contains(".."))
            throw new InvalidDataException($"Updater:Repository must look like owner/name, not '{Repository}'.");
        return repository;
    }
}
