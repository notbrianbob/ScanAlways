using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanAlways.Canary;

var builder = Host.CreateApplicationBuilder(args);

// Let the same binary run as a Windows service or a plain console process.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScanAlways Canary Watcher";
});

builder.Services.AddScanAlwaysCanary(builder.Configuration);
builder.Services.AddHostedService<CanaryWorker>();

var host = builder.Build();
host.Run();
