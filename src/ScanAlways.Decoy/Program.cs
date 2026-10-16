using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanAlways.Common;
using ScanAlways.Decoy;

var builder = Host.CreateApplicationBuilder(args);

// Let the same binary run as a Windows service or a plain console process.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScanAlways Protection";
});

builder.Services.AddScanAlwaysDecoy(builder.Configuration);
builder.Services.AddHostedService<DecoyWorker>();

var host = builder.Build();
host.Run();
