namespace ScanAlways.Updater;

/// <summary>One installed folder to refresh from a staged package folder.</summary>
public sealed record InstallComponent(string Name, string SourceDirectory, string TargetDirectory,
    IReadOnlyCollection<string>? PreservedFiles = null);

public sealed record InstallResult(int FilesReplaced, int FilesAdded, IReadOnlyList<string> PreservedFiles,
    IReadOnlyList<string> LeftoverBackups);

/// <summary>The install failed and the previous files could not all be put back.</summary>
public sealed class UpdateRollbackException : Exception
{
    public UpdateRollbackException(string message, IReadOnlyList<string> backups, Exception inner) : base(message, inner) =>
        Backups = backups;

    public IReadOnlyList<string> Backups { get; }
}

/// <summary>
/// Swaps package files into place with rollback. Only files the package
/// contains are touched: each existing one is first moved aside into a backup
/// folder in the same directory (a rename, which Windows allows even for a
/// running executable), then the new copy is written. Any failure moves every
/// original back. Files the package does not contain, such as logs or extra
/// files the user added, are never moved or deleted. Preserved files (the
/// hub's appsettings.json) keep the user's copy; the release's copy is written
/// next to it as "&lt;name&gt;.default".
/// </summary>
public static class UpdateInstaller
{
    public const string BackupPrefix = ".update-backup-";
    public const string DefaultSuffix = ".default";

    public static InstallResult Install(IReadOnlyList<InstallComponent> components, Func<DateTimeOffset>? clock = null)
    {
        var stamp = (clock ?? (() => DateTimeOffset.UtcNow))().ToString("yyyyMMddHHmmss");
        var moved = new List<(string Original, string Backup)>();
        var created = new List<string>();
        var backups = new List<string>();
        var preserved = new List<string>();
        var replaced = 0;

        foreach (var component in components) RemoveOldBackups(component.TargetDirectory);
        try
        {
            foreach (var component in components)
            {
                var source = Path.GetFullPath(component.SourceDirectory);
                var target = Path.GetFullPath(component.TargetDirectory);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"The package has no {component.Name} folder.");
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"{target} does not exist.");
                var backup = Path.Combine(target, BackupPrefix + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
                backups.Add(backup);
                var keep = new HashSet<string>(component.PreservedFiles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var destination = Path.Combine(target, relative);
                    if (keep.Contains(relative) && File.Exists(destination))
                    {
                        preserved.Add(destination);
                        destination += DefaultSuffix;
                    }
                    if (File.Exists(destination))
                    {
                        var aside = Path.Combine(backup, Path.GetRelativePath(target, destination));
                        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                        File.Move(destination, aside);
                        moved.Add((destination, aside));
                        replaced++;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: false);
                    created.Add(destination);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(moved, created, backups, ex);
            throw;
        }

        var leftovers = new List<string>();
        foreach (var backup in backups)
        {
            if (!Directory.Exists(backup)) continue;
            // A running executable can be renamed but not deleted; its old copy
            // stays until the next update clears it.
            try { Directory.Delete(backup, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add(backup); }
        }
        return new InstallResult(replaced, created.Count - replaced, preserved, leftovers);
    }

    private static void Rollback(List<(string Original, string Backup)> moved, List<string> created, List<string> backups,
        Exception cause)
    {
        var failures = new List<string>();
        for (var i = created.Count - 1; i >= 0; i--)
        {
            try { File.Delete(created[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(created[i]); }
        }
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            try { File.Move(moved[i].Backup, moved[i].Original, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(moved[i].Original); }
        }
        if (failures.Count > 0)
        {
            throw new UpdateRollbackException(
                $"The update failed ({cause.Message}) and {failures.Count} file(s) could not be restored. "
                + "The previous files are kept in: " + string.Join(", ", backups.Where(Directory.Exists)),
                backups.Where(Directory.Exists).ToArray(), cause);
        }
        foreach (var backup in backups)
        {
            try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RemoveOldBackups(string target)
    {
        if (!Directory.Exists(target)) return;
        foreach (var old in Directory.EnumerateDirectories(target, BackupPrefix + "*"))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
