using System.IO.Compression;

namespace WatchLattice.Updater;

/// <summary>Extracts a verified release zip, refusing anything that could land outside the staging folder.</summary>
public static class PackageExtractor
{
    public const int MaxEntries = 5000;
    public const long MaxExtractedBytes = 1024L * 1024 * 1024;

    public static void Extract(Stream zip, string destination, IEnumerable<string> requiredFiles)
    {
        destination = Path.GetFullPath(destination);
        var root = destination.EndsWith(Path.DirectorySeparatorChar) ? destination : destination + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("The package has too many entries.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0')
                || name.Split('/').Any(part => part is ".." or "."))
                throw new InvalidDataException($"The package contains an unsafe path: {entry.FullName}");
            // Join never discards the destination the way Combine does for a rooted name.
            var target = Path.GetFullPath(Path.Join(destination, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The package contains an unsafe path: {entry.FullName}");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Count real bytes rather than trusting the entry's declared size.
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;
                if (total > MaxExtractedBytes) throw new InvalidDataException("The package expands to more than allowed.");
                output.Write(buffer, 0, read);
            }
        }

        foreach (var required in requiredFiles)
        {
            if (!File.Exists(Path.Combine(destination, required)))
                throw new InvalidDataException($"The package is missing {required}.");
        }
    }
}
