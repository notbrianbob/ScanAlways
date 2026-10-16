namespace ScanAlways.Canary;

/// <summary>What happened to a canary.</summary>
public enum CanaryChange
{
    /// <summary>The canary's content was written to, or it was replaced.</summary>
    Modified,

    /// <summary>The canary was deleted.</summary>
    Deleted,

    /// <summary>The canary was renamed (ransomware often appends an extension).</summary>
    Renamed,
}

/// <summary>A tripped canary. Nothing legitimate should ever touch one.</summary>
public sealed record CanaryAlert(DateTimeOffset At, string Path, CanaryChange Change, string Detail)
{
    public override string ToString() =>
        $"CANARY TRIPPED: {Change} {Path}" + (string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})");
}
