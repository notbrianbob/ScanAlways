namespace WatchLattice.Common;

/// <summary>Stops and starts the hub's Windows service, for updates and settings changes.</summary>
public interface IServiceControl
{
    /// <summary>False when the service is not installed or already stopped.</summary>
    bool IsRunning();
    void Stop(TimeSpan timeout);
    void Start(TimeSpan timeout);
}
