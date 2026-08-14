using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class DispatchBackendRequest
{
    public string Operation { get; set; } = "";
    public JsonElement Arguments { get; set; }
}

public sealed class DispatchBackendResponse
{
    public bool Ok { get; set; }
    public string? Payload { get; set; }
    public string? Error { get; set; }
    public string WorkerVersion { get; set; } = "";
    public int WorkerPid { get; set; }
}

public sealed class DispatchWorkerDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset InstalledAtUtc { get; set; }
}
