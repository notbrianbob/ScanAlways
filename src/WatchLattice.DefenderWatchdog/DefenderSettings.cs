using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchLattice.DefenderWatchdog;

/// <summary>One Defender exclusion. <see cref="Kind"/> is one of the <see cref="DefenderExclusionKinds"/>.</summary>
public sealed record DefenderExclusion(string Kind, string Value)
{
    /// <summary>Case-insensitive identity, so "C:\Tools" and "c:\tools" are the same exclusion.</summary>
    [JsonIgnore]
    public string Key => $"{Kind}:{Value}".ToUpperInvariant();

    public override string ToString() => $"{Kind} exclusion \"{Value}\"";
}

public static class DefenderExclusionKinds
{
    public const string Path = "Path";
    public const string Extension = "Extension";
    public const string Process = "Process";
    public const string IpAddress = "IpAddress";
}

/// <summary>
/// The parts of Defender's configuration (<c>Get-MpPreference</c>) that
/// malware likes to weaken quietly: exclusions, attack surface reduction (ASR)
/// rules, and cloud-delivered protection. Null fields were not reported.
/// </summary>
public sealed record DefenderSettings
{
    /// <summary>
    /// Every path, extension, process and IP exclusion. Null when Defender
    /// hid them, which it does unless the reader runs elevated (the hub
    /// service does).
    /// </summary>
    public IReadOnlyList<DefenderExclusion>? Exclusions { get; init; }

    /// <summary>True when Defender answered with its "must be an administrator" placeholder instead of the lists.</summary>
    public bool ExclusionsHidden { get; init; }

    /// <summary>ASR rule GUID (lower case) to action. Null when not reported.</summary>
    public IReadOnlyDictionary<string, int>? AsrRules { get; init; }

    /// <summary>Cloud-delivered protection (MAPS): 0 disabled, 1 basic, 2 advanced.</summary>
    public int? MapsReporting { get; init; }

    public bool? DisableBlockAtFirstSeen { get; init; }

    /// <summary>0 default, 1 moderate, 2 high, 4 high+, 6 zero tolerance.</summary>
    public int? CloudBlockLevel { get; init; }

    public static string DescribeAsrAction(int action) => action switch
    {
        0 => "Disabled",
        1 => "Block",
        2 => "Audit",
        5 => "Not configured",
        6 => "Warn",
        _ => $"action {action}",
    };

    /// <summary>How much an ASR action protects: Block &gt; Warn &gt; Audit &gt; off.</summary>
    public static int AsrStrength(int? action) => action switch
    {
        1 => 3,
        6 => 2,
        2 => 1,
        _ => 0,
    };

    public static string DescribeCloudBlockLevel(int level) => level switch
    {
        0 => "Default",
        1 => "Moderate",
        2 => "High",
        4 => "High+",
        6 => "Zero tolerance",
        _ => $"level {level}",
    };

    internal static DefenderSettings Parse(JsonElement element)
    {
        var exclusions = ParseExclusions(element, out var hidden);
        return new DefenderSettings
        {
            Exclusions = exclusions,
            ExclusionsHidden = hidden,
            AsrRules = ParseAsrRules(element),
            MapsReporting = Int(element, "MAPSReporting"),
            DisableBlockAtFirstSeen = element.TryGetProperty("DisableBlockAtFirstSeen", out var b)
                ? b.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null,
            CloudBlockLevel = Int(element, "CloudBlockLevel"),
        };
    }

    private static IReadOnlyList<DefenderExclusion>? ParseExclusions(JsonElement element, out bool hidden)
    {
        hidden = false;
        var exclusions = new List<DefenderExclusion>();
        foreach (var (property, kind) in new[]
                 {
                     ("ExclusionPath", DefenderExclusionKinds.Path),
                     ("ExclusionExtension", DefenderExclusionKinds.Extension),
                     ("ExclusionProcess", DefenderExclusionKinds.Process),
                     ("ExclusionIpAddress", DefenderExclusionKinds.IpAddress),
                 })
        {
            var values = Strings(element, property);
            if (values is null)
            {
                return null;
            }
            foreach (var value in values)
            {
                // Without elevation Defender answers with a single placeholder
                // such as "N/A: Must be and administrator to view exclusions".
                if (value.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    hidden = true;
                    return null;
                }
                exclusions.Add(new DefenderExclusion(kind, value));
            }
        }
        return exclusions.DistinctBy(e => e.Key).ToList();
    }

    private static IReadOnlyDictionary<string, int>? ParseAsrRules(JsonElement element)
    {
        var ids = Strings(element, "AsrRuleIds");
        if (ids is null
            || !element.TryGetProperty("AsrRuleActions", out var actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.GetArrayLength() != ids.Count)
        {
            return null;
        }

        var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Number || !action.TryGetInt32(out var value))
            {
                return null;
            }
            rules[ids[i++].Trim().ToLowerInvariant()] = value;
        }
        return rules;
    }

    private static List<string>? Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new List<string>();
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return new List<string> { value.GetString()! };
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            .Select(v => v.GetString()!.Trim())
            .ToList();
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>
/// The watchdog's memory of Defender's settings between checks and across
/// restarts, so an exclusion added while the hub was stopped is still
/// reported at the next start.
/// </summary>
public sealed class DefenderSettingsState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Null until the first readable exclusion list is recorded.</summary>
    public List<DefenderExclusion>? Exclusions { get; set; }

    /// <summary>Null until the first ASR rule list is recorded.</summary>
    public Dictionary<string, int>? AsrRules { get; set; }

    public int? CloudBlockLevel { get; set; }

    public bool IsValid() =>
        Version == CurrentVersion
        && (Exclusions is null || Exclusions.All(e => e is not null && !string.IsNullOrEmpty(e.Kind) && e.Value is not null))
        && (AsrRules is null || AsrRules.Keys.All(k => !string.IsNullOrEmpty(k)));
}
