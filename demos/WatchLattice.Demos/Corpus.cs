using System.Globalization;

namespace WatchLattice.Demos;

/// <summary>
/// Builds a synthetic, seeded folder of ordinary-looking files for the decoy to
/// sweep, so every run reads the same tree instead of whatever happens to be in
/// the current user's profile. Same seed and count give byte-identical files.
/// </summary>
public static class Corpus
{
    private static readonly string[] Extensions = { ".docx", ".xlsx", ".pdf", ".jpg", ".png", ".txt", ".zip", ".dll", ".exe", ".mp3" };

    /// <summary>Size buckets, weighted towards small files the way a user profile is.</summary>
    private static readonly (int Weight, int MinBytes, int MaxBytes)[] Sizes =
    {
        (60, 512, 16 * 1024),
        (30, 16 * 1024, 256 * 1024),
        (9, 256 * 1024, 1024 * 1024),
        (1, 1024 * 1024, 4 * 1024 * 1024),
    };

    public sealed record Summary(string Root, int Files, long TotalBytes, int Folders);

    public static Summary Create(string root, int fileCount, int seed)
    {
        Directory.CreateDirectory(root);
        var rng = new Random(seed);
        var folders = new List<string>();
        for (var i = 0; i < Math.Max(1, fileCount / 50); i++)
        {
            var depth = 1 + rng.Next(3);
            var path = root;
            for (var d = 0; d < depth; d++)
            {
                path = Path.Combine(path, $"folder{rng.Next(20):D2}");
            }
            Directory.CreateDirectory(path);
            folders.Add(path);
        }

        long total = 0;
        var buffer = new byte[4 * 1024 * 1024];
        for (var i = 0; i < fileCount; i++)
        {
            var size = PickSize(rng);
            rng.NextBytes(buffer.AsSpan(0, size));
            var folder = folders[rng.Next(folders.Count)];
            var name = string.Create(CultureInfo.InvariantCulture, $"file{i:D5}{Extensions[rng.Next(Extensions.Length)]}");
            File.WriteAllBytes(Path.Combine(folder, name), buffer.AsSpan(0, size).ToArray());
            total += size;
        }

        return new Summary(root, fileCount, total, folders.Distinct().Count());
    }

    private static int PickSize(Random rng)
    {
        var roll = rng.Next(Sizes.Sum(s => s.Weight));
        foreach (var (weight, min, max) in Sizes)
        {
            if (roll < weight)
            {
                return rng.Next(min, max);
            }
            roll -= weight;
        }
        return Sizes[0].MinBytes;
    }
}
