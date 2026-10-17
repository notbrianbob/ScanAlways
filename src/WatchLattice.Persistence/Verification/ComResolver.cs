namespace WatchLattice.Persistence.Verification;

/// <summary>Where a COM handler's CLSID leads, and how much that can be trusted.</summary>
/// <param name="Server">The registered server: an InprocServer32 DLL path, or the program from a LocalServer32 command line.</param>
public sealed record ComResolution(string? Clsid, CheckStatus Status, string Detail, ComRegistration? Effective,
    string? Server, IReadOnlyList<string> Notes);

/// <summary>
/// Resolves a COM handler the way the task host would find it: per-user
/// registrations of the account the task runs as take precedence over the
/// machine-wide class, and TreatAs redirects to another class. The task host
/// is a native process, so the machine's native registry view is the one
/// that counts; the other view is only reported.
/// </summary>
public static class ComResolver
{
    private const int MaxTreatAsHops = 4;

    public static ComResolution Resolve(IComRegistry registry, string? rawClsid, IReadOnlyList<string> userSids, bool is64BitOperatingSystem)
    {
        if (!WindowsPath.TryNormalizeClsid(rawClsid, out var clsid))
            return new(null, CheckStatus.Fail, $"\"{rawClsid}\" is not a valid CLSID, so nothing was looked up.", null, null, Array.Empty<string>());

        var notes = new List<string>();
        var native = is64BitOperatingSystem ? ComScope.Machine64 : ComScope.Machine32;
        var findings = new List<(CheckStatus Status, string Text)>();
        void Finding(CheckStatus status, string text) => findings.Add((status, text));
        var current = clsid;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ComRegistration? effective = null;

        for (var hop = 0; hop <= MaxTreatAsHops; hop++)
        {
            seen.Add(current);
            var machine = registry.Read(native, current);
            var perUser = userSids.Select(sid => (Sid: sid, Registration: registry.Read(ComScope.User, current, sid))).ToArray();
            var overriding = perUser.Where(p => p.Registration.Exists).ToArray();

            if (overriding.Length > 0)
            {
                var who = string.Join(", ", overriding.Select(p => Accounts.Describe(p.Sid)));
                if (machine.Exists)
                {
                    Finding(CheckStatus.Fail,
                        $"A per-user registration of {current} for {who} overrides the machine-wide one, so the task loads " +
                        $"{ServerOf(overriding[0].Registration) ?? "(no server)"} instead of {ServerOf(machine) ?? "(no server)"}. " +
                        "Overriding a machine-wide class per user is a known COM hijacking technique.");
                }
                else
                {
                    Finding(CheckStatus.Warning,
                        $"{current} is registered only for {who} ({overriding[0].Registration.Location}), not machine-wide.");
                }
                effective = overriding[0].Registration;
            }
            else if (machine.Exists)
            {
                effective = machine;
            }
            else
            {
                effective = null;
                if (!machine.Readable)
                    Finding(CheckStatus.Unknown, $"Could not read {machine.Location}: {machine.Error ?? "access denied"}.");
                else
                    Finding(CheckStatus.Warning,
                        $"{current} is not registered, so the handler is unknown. The task cannot start it until something registers that class.");
            }

            foreach (var (sid, registration) in perUser.Where(p => !p.Registration.Readable))
            {
                Finding(CheckStatus.Unknown, registration.HiveUnavailable
                    ? $"Per-user registrations for {Accounts.Describe(sid)} could not be checked because that profile is not loaded."
                    : $"Per-user registrations for {Accounts.Describe(sid)} could not be read: {registration.Error ?? "access denied"}.");
            }

            if (effective?.TreatAs is { } treatAs && WindowsPath.TryNormalizeClsid(treatAs, out var target) && !string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                Finding(CheckStatus.Warning,
                    $"{current} is redirected by TreatAs to {target}. Redirection is rare for task handlers and is also used to hijack classes.");
                notes.Add($"TreatAs: {current} → {target} ({effective.Location})");
                if (seen.Contains(target) || hop == MaxTreatAsHops)
                {
                    Finding(CheckStatus.Fail, "The TreatAs redirections loop or are too deep to follow.");
                    effective = null;
                    break;
                }
                current = target;
                continue;
            }
            break;
        }

        if (is64BitOperatingSystem)
        {
            var wow = registry.Read(ComScope.Machine32, clsid);
            var nativeServer = ServerOf(registry.Read(ComScope.Machine64, clsid));
            if (wow.Exists && !string.Equals(ServerOf(wow), nativeServer, StringComparison.OrdinalIgnoreCase))
                notes.Add($"The 32-bit registry view registers this class differently ({ServerOf(wow) ?? "no server"}). The 64-bit task host uses the 64-bit view.");
        }

        var server = effective is null ? null : ServerOf(effective);
        if (effective is not null && server is null)
            Finding(CheckStatus.Warning, $"{effective.Location} has no InprocServer32 or LocalServer32 server.");
        if (effective is not null && effective.InprocServer32 is null && effective.LocalServer32 is not null)
            notes.Add($"Out-of-process server: {effective.LocalServer32}");

        if (findings.Count == 0)
            return new(clsid, CheckStatus.Pass, $"Registered machine-wide at {effective!.Location}: {server}. No per-user registration overrides it.",
                effective, server, notes);
        var worst = findings.MaxBy(f => Rank(f.Status)).Status;
        var detail = string.Join(" ", findings.OrderByDescending(f => Rank(f.Status)).Select(f => f.Text));
        return new(clsid, worst, detail, effective, server, notes);
    }

    private static string? ServerOf(ComRegistration registration) =>
        !string.IsNullOrWhiteSpace(registration.InprocServer32) ? WindowsPath.Unquote(registration.InprocServer32)
        : !string.IsNullOrWhiteSpace(registration.LocalServer32) ? WindowsPath.ExtractCommandPath(registration.LocalServer32)
        : null;

    internal static int Rank(CheckStatus status) => status switch
    {
        CheckStatus.Pass => 0,
        CheckStatus.Unknown => 1,
        CheckStatus.Warning => 2,
        _ => 3,
    };
}
