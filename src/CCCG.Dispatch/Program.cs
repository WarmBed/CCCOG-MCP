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

// Startup reconciliation: rescue any job left stuck "running" (or
// mislabeled "failed" by a past dead-worker pass) by a worker process that
// died mid-job without recording a terminal result — see
// DispatchRunner.ReconcileStuckJobs. Best-effort and fire-and-forget: a
// fresh Host install with no worker yet, or any other startup race, must
// never block MCP tool negotiation over this.
try
{
    app.Services.GetRequiredService<DispatchBackendClient>().Invoke("reconcileStuckJobs", new { });
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    await Console.Error.WriteLineAsync(
        "cccg-dispatch: startup job reconciliation skipped: " + exception.Message);
}

await app.RunAsync();
