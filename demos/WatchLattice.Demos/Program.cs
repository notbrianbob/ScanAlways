using System.Globalization;
using WatchLattice.Common;
using WatchLattice.Demos;

// Reproducible demonstrations and overhead measurements for the WatchLattice
// modules. Every demo works in a throwaway folder under --work and writes its
// results (summary.md, raw CSV samples, copies of the module logs) under --out.
// See demos/README.md for what each one shows and how it is measured.

var parsed = DemoArgs.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine(DemoArgs.Usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Directory.CreateDirectory(parsed.OutputDir);
Directory.CreateDirectory(parsed.WorkDir);

if (parsed.Command == "corpus")
{
    var summary = Corpus.Create(parsed.CorpusDir ?? Path.Combine(parsed.WorkDir, "decoy-corpus"), parsed.CorpusFiles, parsed.Seed);
    Console.WriteLine(Report.Inv($"Created {summary.Files:N0} files ({Report.Bytes(summary.TotalBytes)}) under {summary.Root}"));
    return 0;
}

var sections = new List<string>();
if (parsed.Command is "decoy" or "all")
{
    sections.Add((await DecoyDemo.RunAsync(parsed, cts.Token)).Markdown);
}
if (parsed.Command is "canary" or "all")
{
    sections.Add((await CanaryDemo.RunAsync(parsed, cts.Token)).Markdown);
}
if (parsed.Command is "watchdog" or "all")
{
    sections.Add((await WatchdogDemo.RunAsync(parsed, cts.Token)).Markdown);
}

var header = new List<string> { "# WatchLattice demo results", "", "Measured with `demos/WatchLattice.Demos`. Environment:", "" };
header.AddRange(Report.Environment());
header.Add("");
File.WriteAllText(
    Path.Combine(parsed.OutputDir, "summary.md"),
    string.Join('\n', header) + "\n" + string.Join("\n", sections));
Console.WriteLine();
Console.WriteLine($"Results written to {Path.GetFullPath(parsed.OutputDir)}");
return Environment.ExitCode;

namespace WatchLattice.Demos
{
    public sealed record DemoArgs
    {
        public const string Usage = """
            usage: dotnet run -c Release --project demos/WatchLattice.Demos -- <all|decoy|canary|watchdog|corpus> [options]

              --seconds N            decoy measurement window per intensity (default 60)
              --baseline-seconds N   idle baseline before the decoy runs (default 15)
              --intensity LIST       comma-separated: Idle,Balanced,FullScan (default Balanced,FullScan)
              --fast-seconds N       decoy run with the pacing cap lifted, for cost per file (default 15)
              --canary-idle-seconds N  idle window with canaries armed (default 30)
              --files N              synthetic corpus size (default 2000)
              --seed N               corpus and narration seed (default 1337)
              --corpus DIR           sweep this folder instead of a fresh synthetic corpus
              --sample-ms N          sampling interval (default 1000)
              --live                 also read this machine's real Defender status (Windows, read-only)
              --out DIR              results folder (default ./demo-results)
              --work DIR             scratch folder (default: a temp folder)
            """;

        public string Command { get; init; } = "all";
        public int Seconds { get; init; } = 60;
        public int BaselineSeconds { get; init; } = 15;
        public int CanaryIdleSeconds { get; init; } = 30;
        public int FastSeconds { get; init; } = 15;
        public IReadOnlyList<DecoyIntensity> Intensities { get; init; } = new[] { DecoyIntensity.Balanced, DecoyIntensity.FullScan };
        public int CorpusFiles { get; init; } = 2000;
        public int Seed { get; init; } = 1337;
        public string? CorpusDir { get; init; }
        public int SampleMs { get; init; } = 1000;
        public bool Live { get; init; }
        public string OutputDir { get; init; } = "demo-results";
        public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "watchlattice-demos");

        public static DemoArgs? Parse(string[] args)
        {
            if (args.Length == 0 || args[0] is not ("all" or "decoy" or "canary" or "watchdog" or "corpus"))
            {
                return null;
            }

            var result = new DemoArgs { Command = args[0] };
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                int NextInt(int min) => int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min
                    ? v
                    : throw new ArgumentException($"{args[i - 1]} needs a whole number >= {min}");

                try
                {
                    result = args[i] switch
                    {
                        "--seconds" => result with { Seconds = NextInt(1) },
                        "--baseline-seconds" => result with { BaselineSeconds = NextInt(1) },
                        "--fast-seconds" => result with { FastSeconds = NextInt(1) },
                        "--canary-idle-seconds" => result with { CanaryIdleSeconds = NextInt(1) },
                        "--files" => result with { CorpusFiles = NextInt(1) },
                        "--seed" => result with { Seed = NextInt(0) },
                        "--sample-ms" => result with { SampleMs = NextInt(100) },
                        "--corpus" => result with { CorpusDir = Path.GetFullPath(Next()) },
                        "--out" => result with { OutputDir = Next() },
                        "--work" => result with { WorkDir = Path.GetFullPath(Next()) },
                        "--live" => result with { Live = true },
                        "--intensity" => result with
                        {
                            Intensities = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(s => Enum.Parse<DecoyIntensity>(s, ignoreCase: true)).ToList(),
                        },
                        _ => throw new ArgumentException($"unknown option {args[i]}"),
                    };
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return null;
                }
            }
            return result;
        }
    }
}
