using WatchLattice.Persistence.Verification;

namespace WatchLattice.Persistence.Tests.Verification;

internal sealed class FakeTaskSource : IScheduledTaskSource
{
    public Dictionary<string, TaskReadResult> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, TaskReadResult>? Override { get; set; }
    public List<string> Reads { get; } = new();

    public TaskReadResult Read(string taskPath)
    {
        Reads.Add(taskPath);
        if (Override is not null) return Override(taskPath);
        return Tasks.TryGetValue(taskPath, out var result) ? result : new(TaskReadStatus.NotFound);
    }
}

internal sealed class FakeComRegistry : IComRegistry
{
    public Dictionary<(ComScope Scope, string Clsid, string? Sid), ComRegistration> Keys { get; } = new();
    public List<string> LoadedUsers { get; } = new();
    public HashSet<string> UnloadedHives { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Machine(string clsid, string? inproc, string? treatAs = null, ComScope scope = ComScope.Machine64, string? localServer = null) =>
        Keys[(scope, clsid, null)] = new($@"HKLM\SOFTWARE\Classes\CLSID\{clsid} ({(scope == ComScope.Machine64 ? 64 : 32)}-bit view)", true, inproc, localServer, treatAs);

    public void User(string sid, string clsid, string? inproc) =>
        Keys[(ComScope.User, clsid, sid)] = new($@"HKU\{sid}_Classes\CLSID\{clsid}", true, inproc);

    public ComRegistration Read(ComScope scope, string clsid, string? userSid = null)
    {
        if (scope == ComScope.User && userSid is not null && UnloadedHives.Contains(userSid))
            return new($@"HKU\{userSid}_Classes\CLSID\{clsid}", false, HiveUnavailable: true);
        var location = scope == ComScope.User ? $@"HKU\{userSid}_Classes\CLSID\{clsid}" : $@"HKLM\SOFTWARE\Classes\CLSID\{clsid}";
        return Keys.TryGetValue((scope, clsid, scope == ComScope.User ? userSid : null), out var found) ? found : new(location, false);
    }

    public IReadOnlyList<string> LoadedUserSids() => LoadedUsers;
}

internal sealed class FakeAccounts : IAccountResolver
{
    public Dictionary<string, string> Sids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ToSid(string account) => Sids.TryGetValue(account, out var sid) ? sid : null;
    public string? ProfilePath(string sid) => Profiles.TryGetValue(sid, out var path) ? path : null;
    public string CurrentAccount => @"NT AUTHORITY\SYSTEM";
}

internal sealed class FakeFiles : IFileInspector
{
    public Dictionary<string, FileEvidence> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Inspected { get; } = new();

    public void Add(string path, SignatureEvidence signature, string sha = "AB12") =>
        Files[path] = new FileEvidence(path, true, 123456, sha, "10.0.26100.1", Signature: signature);

    public bool Exists(string path) => Files.TryGetValue(path, out var file) && file.Exists;

    public Task<FileEvidence> InspectAsync(string path, CancellationToken cancellationToken)
    {
        Inspected.Add(path);
        return Task.FromResult(Files.TryGetValue(path, out var file) ? file : new FileEvidence(path, false));
    }
}
