namespace WatchLattice.Canary.Tests;

/// <summary>A throwaway directory that is deleted when the test finishes.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "watchlattice-canary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Watched = Directory.CreateDirectory(System.IO.Path.Combine(Path, "watched")).FullName;
    }

    public string Path { get; }

    /// <summary>The folder canaries are planted in.</summary>
    public string Watched { get; }

    public CanaryOptions Options(params string[] fileNames)
    {
        var options = new CanaryOptions
        {
            Folders = { Watched },
            ManifestPath = System.IO.Path.Combine(Path, "manifest.json"),
            AlertLogPath = System.IO.Path.Combine(Path, "alerts.log"),
            CanarySizeBytes = 4096,
        };
        options.FileNames.AddRange(fileNames.Length > 0 ? fileNames : new[] { "!000_Test_Canary.docx" });
        return options;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
