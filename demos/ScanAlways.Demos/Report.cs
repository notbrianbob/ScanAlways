using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ScanAlways.Demos;

/// <summary>Collects one demo's Markdown summary and writes it, plus any raw files, to its output folder.</summary>
public sealed class Report
{
    private readonly StringBuilder _md = new();

    public Report(string outputDir, string title)
    {
        OutputDir = outputDir;
        Directory.CreateDirectory(outputDir);
        Line($"## {title}");
        Line();
    }

    public string OutputDir { get; }

    public string Markdown => _md.ToString();

    /// <summary>Writes a line to both the console and the Markdown summary.</summary>
    public void Line(string text = "")
    {
        Console.WriteLine(text);
        _md.AppendLine(text);
    }

    public void Code(IEnumerable<string> lines)
    {
        Line("```text");
        foreach (var line in lines)
        {
            Line(line);
        }
        Line("```");
        Line();
    }

    public void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(OutputDir, name), content);

    public void Save() => WriteFile("summary.md", Markdown);

    public static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    public static string Bytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 => Inv($"{bytes / (1024 * 1024):F2} MiB"),
        >= 1024 => Inv($"{bytes / 1024:F1} KiB"),
        _ => Inv($"{bytes:F0} B"),
    };

    /// <summary>A short description of the machine a run was measured on.</summary>
    public static IEnumerable<string> Environment()
    {
        yield return Inv($"- Date (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}");
        yield return $"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        yield return $"- CPU: {CpuName()}, {System.Environment.ProcessorCount} logical cores";
        yield return $"- Runtime: {RuntimeInformation.FrameworkDescription}";
        var ci = System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        yield return ci
            ? $"- Host: GitHub Actions runner ({System.Environment.GetEnvironmentVariable("RUNNER_OS")}), commit {System.Environment.GetEnvironmentVariable("GITHUB_SHA")?[..7]}"
            : "- Host: local machine";
    }

    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            return System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        }
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null)
                {
                    return line.Split(':', 2)[1].Trim();
                }
            }
        }
        catch (IOException)
        {
        }
        return "unknown";
    }
}
