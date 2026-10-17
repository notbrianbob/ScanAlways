using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace WatchLattice.Common.Alerts;

/// <summary>
/// Writes to the Application log. Registering the event source needs
/// administrator rights, which the hub has when it runs as its service;
/// writing to an already registered source does not.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWriter : IEventLogWriter
{
    private const string SourceName = EventLogAlertService.SourceName;
    private const string LogName = EventLogAlertService.LogName;
    private const string SourceKey = @"SYSTEM\CurrentControlSet\Services\EventLog\" + LogName + @"\" + SourceName;

    public bool IsSupported => true;

    public string? Prepare()
    {
        try
        {
            if (EventLog.SourceExists(SourceName))
            {
                var log = EventLog.LogNameFromSourceName(SourceName, ".");
                if (!string.Equals(log, LogName, StringComparison.OrdinalIgnoreCase))
                    return $"The '{SourceName}' event source is registered to the '{log}' log, not '{LogName}'. "
                        + $"An administrator can remove it with: Remove-EventLog -Source {SourceName}";
                RepairMessageFile();
                return null;
            }
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName) { MessageResourceFile = MessageFile() });
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or InvalidOperationException
            or IOException or ArgumentException or Win32Exception)
        {
            return $"The '{SourceName}' event source is not registered, and registering it needs administrator rights ({ex.Message}). "
                + "Start the hub once as its Windows service or from an elevated prompt to register it.";
        }
    }

    public void Write(int eventId, EventLogLevel level, IReadOnlyList<string> data)
    {
        var type = level switch
        {
            EventLogLevel.Error => EventLogEntryType.Error,
            EventLogLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        EventLog.WriteEvent(SourceName, new EventInstance(eventId, 0, type), data.Cast<object>().ToArray());
    }

    /// <summary>
    /// The in-box .NET Framework message file renders every event ID as its
    /// first insertion string, and does not move when WatchLattice is
    /// reinstalled elsewhere. Null falls back to the .NET default.
    /// </summary>
    private static string? MessageFile()
    {
        var path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", Environment.Is64BitOperatingSystem ? "Framework64" : "Framework", "v4.0.30319", "EventLogMessages.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Re-registers a source whose message file is gone, so Event Viewer can
    /// render entries again. Best effort: without admin rights entries are
    /// still written and readable in the Details tab.
    /// </summary>
    private static void RepairMessageFile()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SourceKey);
            if (key?.GetValue("EventMessageFile") is not string file) return;
            if (File.Exists(Environment.ExpandEnvironmentVariables(file))) return;
            EventLog.DeleteEventSource(SourceName);
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName) { MessageResourceFile = MessageFile() });
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or InvalidOperationException
            or IOException or ArgumentException or Win32Exception)
        {
            // Not fatal: entries are still written, Event Viewer just can't render their text.
            return;
        }
    }
}
