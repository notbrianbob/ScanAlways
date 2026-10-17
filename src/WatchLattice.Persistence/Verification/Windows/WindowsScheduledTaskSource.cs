using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WatchLattice.Persistence.Verification.Windows;

/// <summary>
/// Reads a registered task's XML through the Task Scheduler 2.0 COM API
/// (ITaskService → ITaskFolder → IRegisteredTask.Xml). Only GetFolder,
/// GetTask and the Xml property are used: nothing is registered, enabled,
/// disabled or run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskSource : IScheduledTaskSource
{
    private static readonly Guid TaskSchedulerClsid = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    public TaskReadResult Read(string taskPath)
    {
        if (!WindowsPath.IsValidTaskPath(taskPath)) return new(TaskReadStatus.Error, Error: "Not a valid task path.");
        var (folderPath, name) = WindowsPath.SplitTaskPath(taskPath);
        object? service = null, folder = null, task = null;
        try
        {
            var type = Type.GetTypeFromCLSID(TaskSchedulerClsid, throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            // Late-bound IDispatch calls; Connect's four optional arguments are left unset (this machine, this account).
            Invoke(service, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, folderPath);
            task = Invoke(folder!, "GetTask", BindingFlags.InvokeMethod, name);
            var xml = Invoke(task!, "Xml", BindingFlags.GetProperty) as string;
            return string.IsNullOrEmpty(xml) ? new(TaskReadStatus.Error, Error: "The Task Scheduler returned an empty definition.") : new(TaskReadStatus.Found, xml);
        }
        catch (Exception ex) when (HResult(ex) is FileNotFound or PathNotFound)
        {
            return new(TaskReadStatus.NotFound);
        }
        catch (Exception ex) when (HResult(ex) == AccessDenied || ex is UnauthorizedAccessException)
        {
            return new(TaskReadStatus.AccessDenied, Error: "Access denied.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException
            or TargetInvocationException or InvalidComObjectException)
        {
            return new(TaskReadStatus.Error, Error: $"{(ex.InnerException ?? ex).Message} (0x{HResult(ex):X8})");
        }
        finally
        {
            foreach (var com in new[] { task, folder, service }.Where(c => c is not null && Marshal.IsComObject(c)))
                Marshal.FinalReleaseComObject(com!);
        }
    }

    private static object? Invoke(object target, string member, BindingFlags kind, params object?[] arguments) =>
        target.GetType().InvokeMember(member, kind, binder: null, target, arguments, CultureInfo.InvariantCulture);

    private static int HResult(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner.HResult : ex.HResult;
}
