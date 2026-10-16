using System.Text.RegularExpressions;

namespace ScanAlways.DefenderWatchdog;

/// <summary>
/// Rules for Defender's exclusions, attack surface reduction rules and
/// cloud-delivered protection: standing findings for settings that are weak
/// right now, and changes between two snapshots. Pure, like the rest of the
/// evaluator.
/// </summary>
public static partial class DefenderHealthEvaluator
{
    public const string SettingsUnavailable = "SETTINGS_UNAVAILABLE";
    public const string ExclusionsHidden = "EXCLUSIONS_HIDDEN";
    public const string CloudProtectionOff = "CLOUD_PROTECTION_OFF";
    public const string BlockAtFirstSeenOff = "BLOCK_AT_FIRST_SEEN_OFF";

    /// <summary>Followed by the exclusion's <see cref="DefenderExclusion.Key"/>, one finding per broad exclusion.</summary>
    public const string BroadExclusionPrefix = "BROAD_EXCLUSION:";

    public const string ExclusionAdded = "EXCLUSION_ADDED";
    public const string ExclusionsAddedSummary = "EXCLUSIONS_ADDED";
    public const string ExclusionRemoved = "EXCLUSION_REMOVED";
    public const string ExclusionsRemovedSummary = "EXCLUSIONS_REMOVED";
    public const string AsrRuleWeakened = "ASR_RULE_WEAKENED";
    public const string AsrRulesWeakenedSummary = "ASR_RULES_WEAKENED";
    public const string AsrRuleStrengthened = "ASR_RULE_STRENGTHENED";
    public const string CloudBlockLevelLowered = "CLOUD_BLOCK_LEVEL_LOWERED";
    public const string CloudBlockLevelRaised = "CLOUD_BLOCK_LEVEL_RAISED";
    public const string SettingsBaselineCreated = "SETTINGS_BASELINE_CREATED";

    private static readonly HashSet<string> BroadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "sys", "scr", "com", "cpl", "ps1", "psm1", "bat", "cmd", "vbs", "vbe",
        "js", "jse", "wsf", "hta", "msi", "lnk", "jar",
    };

    private static readonly HashSet<string> BroadProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "powershell_ise.exe", "pwsh.exe", "cmd.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "explorer.exe", "svchost.exe",
    };

    // Folders whose exclusion switches scanning off for most of the places
    // malware lands or runs from. "*" stands for any one folder name.
    private static readonly string[][] BroadFolders =
    {
        new[] { "Windows" },
        new[] { "Windows", "System32" },
        new[] { "Windows", "SysWOW64" },
        new[] { "Windows", "Temp" },
        new[] { "Program Files" },
        new[] { "Program Files (x86)" },
        new[] { "ProgramData" },
        new[] { "Users" },
        new[] { "Users", "Public" },
        new[] { "Users", "*" },
        new[] { "Users", "*", "Desktop" },
        new[] { "Users", "*", "Documents" },
        new[] { "Users", "*", "Downloads" },
        new[] { "Users", "*", "AppData" },
        new[] { "Users", "*", "AppData", "Local" },
        new[] { "Users", "*", "AppData", "Local", "Temp" },
        new[] { "Users", "*", "AppData", "Roaming" },
    };

    // The environment variables Defender expands in exclusion paths, mapped to
    // the folder they usually point at.
    private static readonly Dictionary<string, string[]> EnvironmentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["%SYSTEMDRIVE%"] = Array.Empty<string>(),
        ["%SYSTEMROOT%"] = new[] { "Windows" },
        ["%WINDIR%"] = new[] { "Windows" },
        ["%PROGRAMFILES%"] = new[] { "Program Files" },
        ["%PROGRAMFILES(X86)%"] = new[] { "Program Files (x86)" },
        ["%PROGRAMDATA%"] = new[] { "ProgramData" },
        ["%ALLUSERSPROFILE%"] = new[] { "ProgramData" },
        ["%PUBLIC%"] = new[] { "Users", "Public" },
        ["%USERPROFILE%"] = new[] { "Users", "*" },
        ["%APPDATA%"] = new[] { "Users", "*", "AppData", "Roaming" },
        ["%LOCALAPPDATA%"] = new[] { "Users", "*", "AppData", "Local" },
        ["%TEMP%"] = new[] { "Users", "*", "AppData", "Local", "Temp" },
        ["%TMP%"] = new[] { "Users", "*", "AppData", "Local", "Temp" },
    };

    private static void AddSettingsFindings(
        DefenderStatus status, DefenderWatchdogOptions options, bool defenderActive, List<DefenderFinding> findings)
    {
        var settings = status.Settings;
        if (settings is null)
        {
            // No error means the provider simply didn't report settings; only
            // a failed read is worth an alert.
            if (status.SettingsError is { } error)
            {
                findings.Add(new(SettingsUnavailable, FindingSeverity.Warning,
                    $"Could not read Defender's exclusions and protection settings: {error}"));
            }
            return;
        }

        if (settings.ExclusionsHidden)
        {
            findings.Add(new(ExclusionsHidden, FindingSeverity.Warning,
                "Defender hid its exclusion list from this process, so new exclusions can't be reported. Run the hub as the installed service (or elevated) to watch exclusions."));
        }

        if (!defenderActive)
        {
            // Passive or stopped Defender already raised one finding; its
            // cloud and exclusion settings don't protect anything right now.
            return;
        }

        if (settings.MapsReporting == 0)
        {
            findings.Add(new(CloudProtectionOff, FindingSeverity.Warning,
                "Cloud-delivered protection is off, so Defender can't check new files against Microsoft's cloud."));
        }
        else if (settings.DisableBlockAtFirstSeen == true)
        {
            findings.Add(new(BlockAtFirstSeenOff, FindingSeverity.Warning, "Block at first sight is off."));
        }

        if (options.ReportBroadExclusions && settings.Exclusions is { } exclusions)
        {
            foreach (var exclusion in exclusions.Where(e => IsBroad(e) && !IsIgnored(e, options.IgnoredExclusions)))
            {
                findings.Add(new(BroadExclusionPrefix + exclusion.Key, FindingSeverity.Warning,
                    $"Defender has a broad {exclusion}: it stops scanning {BroadReason(exclusion)}."));
            }
        }
    }

    /// <summary>
    /// Reports what changed in Defender's exclusions, ASR rules and cloud block
    /// level since the saved snapshot. A part with no saved snapshot yet is
    /// recorded silently (the caller reports the baseline once); a part this
    /// check couldn't read is skipped, not treated as "everything removed".
    /// </summary>
    public static IReadOnlyList<DefenderChange> CompareSettings(
        DefenderSettingsState before, DefenderSettings current, DefenderWatchdogOptions options)
    {
        var changes = new List<DefenderChange>();
        var limit = Math.Max(0, options.MaxChangeAlertsPerCheck);

        if (before.Exclusions is { } oldExclusions && current.Exclusions is { } newExclusions)
        {
            var oldKeys = oldExclusions.Select(e => e.Key).ToHashSet();
            var newKeys = newExclusions.Select(e => e.Key).ToHashSet();

            var added = newExclusions
                .Where(e => !oldKeys.Contains(e.Key) && !IsIgnored(e, options.IgnoredExclusions))
                .ToList();
            changes.AddRange(added.Take(limit).Select(e => new DefenderChange(ExclusionAdded, FindingSeverity.Warning,
                "New Defender exclusion",
                $"{Capitalize(e.ToString())} was added, so Defender no longer scans it. If you didn't add it, remove it in Windows Security.",
                e.Key)));
            if (added.Count > limit)
            {
                changes.Add(new(ExclusionsAddedSummary, FindingSeverity.Warning, "New Defender exclusions",
                    $"{added.Count - limit} more Defender exclusions were added. Review them in Windows Security > Virus & threat protection settings > Exclusions."));
            }

            var removed = oldExclusions.Where(e => !newKeys.Contains(e.Key)).ToList();
            changes.AddRange(removed.Take(limit).Select(e => new DefenderChange(ExclusionRemoved, FindingSeverity.Info,
                "Defender exclusion removed", $"{Capitalize(e.ToString())} was removed.", e.Key)));
            if (removed.Count > limit)
            {
                changes.Add(new(ExclusionsRemovedSummary, FindingSeverity.Info, "Defender exclusions removed",
                    $"{removed.Count - limit} more Defender exclusions were removed."));
            }
        }

        if (before.AsrRules is { } oldRules && current.AsrRules is { } newRules)
        {
            var weakened = new List<DefenderChange>();
            foreach (var id in oldRules.Keys.Union(newRules.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                int? was = oldRules.TryGetValue(id, out var o) ? o : null;
                int? now = newRules.TryGetValue(id, out var n) ? n : null;
                if (was == now)
                {
                    continue;
                }

                var description = $"Attack surface reduction rule {id} changed from {DescribeAsr(was)} to {DescribeAsr(now)}.";
                if (DefenderSettings.AsrStrength(now) < DefenderSettings.AsrStrength(was))
                {
                    weakened.Add(new(AsrRuleWeakened, FindingSeverity.Warning, "Defender ASR rule weakened", description, id));
                }
                else
                {
                    changes.Add(new(AsrRuleStrengthened, FindingSeverity.Info, "Defender ASR rule changed", description, id));
                }
            }

            changes.AddRange(weakened.Take(limit));
            if (weakened.Count > limit)
            {
                changes.Add(new(AsrRulesWeakenedSummary, FindingSeverity.Warning, "Defender ASR rules weakened",
                    $"{weakened.Count - limit} more attack surface reduction rules were weakened or removed."));
            }
        }

        if (before.CloudBlockLevel is { } oldLevel && current.CloudBlockLevel is { } newLevel && oldLevel != newLevel)
        {
            var lowered = newLevel < oldLevel;
            changes.Add(new(lowered ? CloudBlockLevelLowered : CloudBlockLevelRaised,
                lowered ? FindingSeverity.Warning : FindingSeverity.Info,
                "Defender cloud block level changed",
                $"Cloud block level changed from {DefenderSettings.DescribeCloudBlockLevel(oldLevel)} to {DefenderSettings.DescribeCloudBlockLevel(newLevel)}."));
        }

        return changes;
    }

    /// <summary>
    /// Matches an exclusion against <see cref="DefenderWatchdogOptions.IgnoredExclusions"/>.
    /// Only * and ? are wildcards; backslashes are literal, since most patterns are paths.
    /// </summary>
    public static bool IsIgnored(DefenderExclusion exclusion, IEnumerable<string> patterns) => patterns
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Any(p => Regex.IsMatch(exclusion.Value,
            "^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    /// <summary>True for exclusions that switch scanning off for a lot of the machine at once.</summary>
    public static bool IsBroad(DefenderExclusion exclusion) => exclusion.Kind switch
    {
        DefenderExclusionKinds.Path => IsBroadPath(exclusion.Value),
        DefenderExclusionKinds.Extension => BroadExtensions.Contains(exclusion.Value.Trim().TrimStart('*').TrimStart('.')),
        DefenderExclusionKinds.Process => IsWildcardOnly(exclusion.Value) || BroadProcesses.Contains(FileName(exclusion.Value)),
        _ => false,
    };

    private static bool IsBroadPath(string value)
    {
        var segments = value.Trim().Split('\\', '/').Where(s => s.Length > 0).ToList();

        // Trailing "*" or "*.*" covers the folder's contents, same as the folder.
        while (segments.Count > 0 && IsWildcardOnly(segments[^1]))
        {
            segments.RemoveAt(segments.Count - 1);
        }
        if (segments.Count == 0)
        {
            return true;
        }

        var first = segments[0];
        if (first.Length == 2 && char.IsAsciiLetter(first[0]) && first[1] == ':')
        {
            segments.RemoveAt(0);
        }
        else if (EnvironmentFolders.TryGetValue(first, out var expanded))
        {
            segments.RemoveAt(0);
            segments.InsertRange(0, expanded);
        }
        else
        {
            // A relative path or UNC share: not something we can judge.
            return false;
        }

        return segments.Count == 0 || BroadFolders.Any(folder => SameFolder(folder, segments));
    }

    private static bool SameFolder(string[] pattern, List<string> segments) =>
        pattern.Length == segments.Count
        && pattern.Zip(segments).All(p => p.First == "*" || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    private static bool IsWildcardOnly(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(c => c is '*' or '?' or '.');
    }

    private static string FileName(string value)
    {
        var trimmed = value.Trim().Trim('"');
        return trimmed[(trimmed.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
    }

    private static string BroadReason(DefenderExclusion exclusion) => exclusion.Kind switch
    {
        DefenderExclusionKinds.Extension => "every file with that extension, anywhere",
        DefenderExclusionKinds.Process => "every file that process opens",
        _ => "a folder where malware commonly lands or runs",
    };

    private static string DescribeAsr(int? action) => action is { } a ? DefenderSettings.DescribeAsrAction(a) : "not set";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
