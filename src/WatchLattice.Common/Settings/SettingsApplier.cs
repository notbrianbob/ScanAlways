using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WatchLattice.Common.Settings;

/// <summary>Process exit codes for <c>--apply-settings</c>; the settings editor reads them.</summary>
public enum SettingsOutcome
{
    Applied = 0,
    Failed = 1,
    NoChanges = 20,
    Invalid = 21,
    RolledBack = 22,
}

/// <summary>
/// Saves settings the editor staged as <c>appsettings.json.pending</c> next to
/// the hub. Only an administrator can write that folder, so the staged file
/// cannot be swapped by an unprivileged process between the editor writing it
/// and the hub reading it. The applier checks it, keeps a timestamped backup
/// of the current file, replaces appsettings.json atomically, and restarts the
/// hub service. If the hub does not come back up, the backup is put back.
/// </summary>
public sealed class SettingsApplier
{
    public const string SettingsFileName = "appsettings.json";
    public const string PendingFileName = "appsettings.json.pending";
    public const string BackupFolderName = "settings-backups";
    public const int BackupsKept = 10;
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<SettingsSection> _sections;
    private readonly IReadOnlyList<(string Path, Type Type)> _types;
    private readonly IServiceControl? _service;
    private readonly Action<string> _log;
    private readonly TimeSpan _settleTime;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="settleTime">How long the restarted hub must keep running before the change counts as working.</param>
    public SettingsApplier(IReadOnlyList<SettingsSection> sections, IReadOnlyList<(string Path, Type Type)> types,
        IServiceControl? service, Action<string> log, TimeSpan? settleTime = null, Func<DateTimeOffset>? clock = null)
    {
        _sections = sections;
        _types = types;
        _service = service;
        _log = log;
        _settleTime = settleTime ?? TimeSpan.FromSeconds(8);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SettingsOutcome> ApplyAsync(string hubDirectory, CancellationToken cancellationToken = default)
    {
        var settingsPath = Path.Combine(hubDirectory, SettingsFileName);
        var pendingPath = Path.Combine(hubDirectory, PendingFileName);
        try
        {
            if (!File.Exists(pendingPath))
            {
                _log($"There are no staged settings ({PendingFileName}) to apply.");
                return SettingsOutcome.Failed;
            }

            byte[] currentBytes;
            JsonObject current, proposed;
            try
            {
                currentBytes = ReadBounded(settingsPath);
                current = SettingValues.Parse(Encoding.UTF8.GetString(currentBytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _log($"The current {SettingsFileName} cannot be read, so nothing was changed: {ex.Message}");
                return SettingsOutcome.Failed;
            }
            try { proposed = SettingValues.Parse(Encoding.UTF8.GetString(ReadBounded(pendingPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _log("The new settings are not valid JSON, so nothing was changed: " + ex.Message);
                return SettingsOutcome.Invalid;
            }

            var check = SettingsValidator.Check(_sections, current, proposed);
            var errors = check.Errors.Concat(SettingsValidator.CheckBinding(_types, proposed)).ToArray();
            foreach (var warning in check.Warnings) _log("Note: " + warning);
            if (errors.Length > 0)
            {
                _log("The new settings were not saved:");
                foreach (var error in errors) _log("  " + error);
                return SettingsOutcome.Invalid;
            }
            if (check.Changes.Count == 0)
            {
                _log("Nothing changed.");
                return SettingsOutcome.NoChanges;
            }

            _log("Changes:");
            foreach (var change in check.Changes) _log("  " + change);
            try
            {
                var backup = Backup(hubDirectory, currentBytes);
                _log("Saved a copy of the previous settings to " + backup);
                WriteAtomically(settingsPath, Encoding.UTF8.GetBytes(SettingValues.Serialize(proposed)));
                _log("Saved " + settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Writing the hub's folder needs administrator rights.
                _log("The new settings could not be saved, so nothing was changed: " + ex.Message);
                return SettingsOutcome.Failed;
            }

            if (_service is null || !_service.IsRunning())
            {
                _log("The hub service is not running here. The new settings take effect the next time the hub starts.");
                return SettingsOutcome.Applied;
            }

            if (await RestartAsync(cancellationToken))
            {
                _log("The hub restarted with the new settings.");
                return SettingsOutcome.Applied;
            }

            _log("The hub did not keep running with the new settings. Putting the previous settings back.");
            WriteAtomically(settingsPath, currentBytes);
            try
            {
                if (!_service.IsRunning()) _service.Start(ServiceTimeout);
                _log("The previous settings are back in place and the hub was started again.");
            }
            catch (Exception ex) when (IsServiceFailure(ex))
            {
                _log("The previous settings are back in place, but the hub did not start: " + ex.Message);
            }
            return SettingsOutcome.RolledBack;
        }
        finally
        {
            try { File.Delete(pendingPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _log("Restarting the hub service…");
            _service!.Stop(ServiceTimeout);
            _service.Start(ServiceTimeout);
        }
        catch (Exception ex) when (IsServiceFailure(ex))
        {
            _log("The hub service did not restart: " + ex.Message);
            return false;
        }

        // A hub that cannot use its settings usually stops within a few seconds of starting.
        var deadline = _clock() + _settleTime;
        while (_clock() < deadline)
        {
            if (!_service.IsRunning()) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return _service.IsRunning();
    }

    // ServiceController reports a slow start or stop as System.ServiceProcess.TimeoutException,
    // which this cross-platform library does not reference, so it is matched by name.
    private static bool IsServiceFailure(Exception ex) =>
        ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException
        || ex.GetType().Name == nameof(TimeoutException);

    private string Backup(string hubDirectory, byte[] bytes)
    {
        var folder = Path.Combine(hubDirectory, BackupFolderName);
        Directory.CreateDirectory(folder);
        var stamp = _clock().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"appsettings-{stamp}.json");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"appsettings-{stamp}-{n}.json");
        WriteAtomically(path, bytes);
        foreach (var old in Directory.GetFiles(folder, "appsettings-*.json")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(BackupsKept))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return path;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > SettingsValidator.MaxBytes) throw new InvalidDataException("The settings file is larger than 1 MB.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
