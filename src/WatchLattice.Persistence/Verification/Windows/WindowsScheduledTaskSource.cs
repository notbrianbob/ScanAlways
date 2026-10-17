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
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder(folderPath);
            task = ((dynamic)folder).GetTask(name);
            string xml = ((dynamic)task).Xml;
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
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or System.Reflection.TargetInvocationException or InvalidComObjectException)
        {
            return new(TaskReadStatus.Error, Error: $"{(ex.InnerException ?? ex).Message} (0x{HResult(ex):X8})");
        }
        finally
        {
            foreach (var com in new[] { task, folder, service })
                if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
    }

    private static int HResult(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.HResult : ex.HResult;
}
