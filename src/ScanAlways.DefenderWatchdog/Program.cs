using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanAlways.DefenderWatchdog;

var builder = Host.CreateApplicationBuilder(args);

// Let the same binary run as a Windows service or a plain console process.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScanAlways Defender Watchdog";
});

builder.Services.AddScanAlwaysDefenderWatchdog(builder.Configuration);
builder.Services.AddHostedService<DefenderWatchdogWorker>();

var host = builder.Build();
host.Run();
