using System.Text.Json;

namespace WatchLattice.Canary;

/// <summary>
/// The record of which files the watcher planted and what their content hash
/// was. It is how the watcher tells its own canaries apart from a real user
/// file that happens to share a name: a file that is not in the manifest is
/// never overwritten or deleted.
/// </summary>
public sealed class CanaryManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Full canary path to lowercase hex SHA-256 of its content.</summary>
    public Dictionary<string, string> Files { get; set; } = new(CanaryPaths.Comparer);

    /// <summary>Loads the manifest, or returns an empty one if it is missing or unreadable.</summary>
    public static CanaryManifest Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CanaryManifest();
            }

            var loaded = JsonSerializer.Deserialize<CanaryManifest>(File.ReadAllText(path), JsonOptions);
            var manifest = new CanaryManifest();
            if (loaded?.Files is not null)
            {
                foreach (var (file, hash) in loaded.Files)
                {
                    manifest.Files[file] = hash;
                }
            }
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CanaryManifest();
        }
    }

    /// <summary>Writes the manifest atomically (temp file, then move).</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
