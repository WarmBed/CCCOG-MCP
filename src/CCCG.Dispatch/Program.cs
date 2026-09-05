using System.Globalization;
using CCCG.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton<DispatchBackendClient>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
var backend = app.Services.GetRequiredService<DispatchBackendClient>();

// Startup housekeeping: rescue any job left stuck "running" (or mislabeled
// "failed" by a past dead-worker pass) by a worker process that died mid-job,
// and reclaim disk from expired job dirs / mailbox lines — see
// DispatchRunner.Maintain. Best-effort and fire-and-forget: a fresh Host
// install with no worker yet, an older installed worker that predates the
// "maintain" operation, or any other startup race must never block MCP tool
// negotiation over this.
RunMaintenance("startup");

// Periodic housekeeping while this Host lives (one Host per Claude session,
// so at least one is almost always alive): the same sweep on a timer, so a
// job whose worker died while nobody was polling gets reconciled within
// minutes instead of whenever someone next happens to ask for its status.
var interval = ResolveMaintenanceInterval();
if (interval > TimeSpan.Zero)
{
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync())
        {
            RunMaintenance("periodic");
        }
    });
}

await app.RunAsync();

void RunMaintenance(string trigger)
{
    try
    {
        backend.Invoke("maintain", new { });
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine(
            $"cccg-dispatch: {trigger} maintenance skipped: {exception.Message}");
    }
}

// CCCG_MAINTENANCE_INTERVAL_MINUTES: how often this Host re-runs the sweep.
// Unset/unparsable -> 15 minutes; 0 disables the timer (startup pass still
// runs). Defensive parse: a malformed operator override must never crash
// the MCP server on launch.
static TimeSpan ResolveMaintenanceInterval()
{
    var raw = Environment.GetEnvironmentVariable("CCCG_MAINTENANCE_INTERVAL_MINUTES");
    if (!string.IsNullOrWhiteSpace(raw)
        && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
        && !double.IsNaN(minutes)
        && minutes >= 0
        && minutes <= TimeSpan.MaxValue.TotalMinutes)
    {
        return TimeSpan.FromMinutes(minutes);
    }

    return TimeSpan.FromMinutes(15);
}
