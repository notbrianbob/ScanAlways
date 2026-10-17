using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Configuration;
using WatchLattice.Updater;

namespace WatchLattice.Hub;

/// <summary>
/// <c>--check-update</c> prints whether a newer release exists.
/// <c>--apply-update</c> installs it: the tray starts this elevated when the
/// user chooses Install update, and it can be run by hand from an
/// administrator prompt. Exit codes are <see cref="UpdateOutcome"/>; a
/// successful check exits with UpToDate (10) whether or not an update exists.
/// </summary>
internal static class UpdateCommand
{
    // Kept from the ScanAlways name so installed services keep their name across updates.
    public const string ServiceName = "ScanAlways Protection";

    public static async Task<int> RunAsync(IConfiguration configuration, bool apply, bool pauseOnFailure)
    {
        var options = new UpdaterOptions();
        configuration.GetSection(UpdaterOptions.SectionName).Bind(options);
        var current = ReleaseVersion.FromAssembly(typeof(UpdateCommand).Assembly);
        using var http = UpdateHttp.CreateClient(current);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        UpdateOutcome outcome;
        try
        {
            var source = new GitHubReleaseSource(http, options.ResolveRepository());
            if (!apply)
            {
                outcome = await CheckAsync(source, options, current, cancel.Token);
            }
            else
            {
                Console.WriteLine("WatchLattice updater");
                IHubServiceControl? service = OperatingSystem.IsWindows() ? new WindowsHubServiceControl(ServiceName) : null;
                ISignatureVerifier? signatures = OperatingSystem.IsWindows() ? new WindowsAuthenticodeVerifier() : null;
                var applier = new UpdateApplier(source, http, options, service, Console.WriteLine, signatures);
                var hubDirectory = AppContext.BaseDirectory;
                outcome = await applier.ApplyAsync(current, hubDirectory, ReleaseLayout.FindTrayDirectory(hubDirectory), cancel.Token);
            }
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine(ex.Message);
            outcome = UpdateOutcome.Failed;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            outcome = UpdateOutcome.Failed;
        }

        if (apply && pauseOnFailure && outcome is not (UpdateOutcome.Installed or UpdateOutcome.UpToDate))
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
        return (int)outcome;
    }

    private static async Task<UpdateOutcome> CheckAsync(IReleaseSource source, UpdaterOptions options, ReleaseVersion current,
        CancellationToken cancellationToken)
    {
        UpdateCandidate? candidate;
        try
        {
            candidate = UpdateEvaluator.FindNewer(current, await source.GetLatestAsync(cancellationToken), options.ResolveRepository());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Could not check for updates: " + ex.Message);
            return UpdateOutcome.Failed;
        }

        if (candidate is null)
        {
            Console.WriteLine($"WatchLattice {current} is up to date.");
            return UpdateOutcome.UpToDate;
        }
        Console.WriteLine($"WatchLattice {candidate.Version} is available (running {current}): {candidate.Release.HtmlUrl}");
        if (!candidate.Installable)
        {
            Console.WriteLine(candidate.Problem);
            return UpdateOutcome.NotAvailable;
        }
        Console.WriteLine("Install it with --apply-update from an administrator prompt.");
        return UpdateOutcome.UpToDate;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsHubServiceControl : IHubServiceControl
{
    private readonly string _name;

    public WindowsHubServiceControl(string name) => _name = name;

    public bool IsRunning()
    {
        try
        {
            using var service = new ServiceController(_name);
            return service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending);
        }
        catch (InvalidOperationException)
        {
            // Not installed as a service.
            return false;
        }
    }

    public void Stop(TimeSpan timeout)
    {
        using var service = new ServiceController(_name);
        if (service.Status != ServiceControllerStatus.Stopped) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void Start(TimeSpan timeout)
    {
        using var service = new ServiceController(_name);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }
}
