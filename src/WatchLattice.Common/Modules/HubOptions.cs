namespace WatchLattice.Common.Modules;

/// <summary>
/// Settings for the hub itself, bound from the "Hub" configuration section.
/// </summary>
public sealed class HubOptions
{
    public const string SectionName = "Hub";

    /// <summary>Restart a module that throws, after a back-off delay.</summary>
    public bool RestartFailedModules { get; set; } = true;

    /// <summary>Delay before the first restart; doubles on each consecutive failure.</summary>
    public double RestartDelaySeconds { get; set; } = 30;

    /// <summary>Upper bound on the restart delay.</summary>
    public double MaxRestartDelaySeconds { get; set; } = 600;

    /// <summary>The back-off delay to use after <paramref name="consecutiveFailures"/> failures.</summary>
    public TimeSpan RestartDelay(int consecutiveFailures)
    {
        var baseDelay = Math.Max(0, RestartDelaySeconds);
        var cap = Math.Max(baseDelay, MaxRestartDelaySeconds);
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 30);
        return TimeSpan.FromSeconds(Math.Min(cap, baseDelay * Math.Pow(2, exponent)));
    }
}
