using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CCCG.Core.Dispatch;

namespace CCCG.Dispatch;

public sealed class DispatchBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string Invoke(string operation, object arguments)
    {
        var descriptor = ResolveWorker();
        VerifyWorker(descriptor);
        var info = BuildWorkerStartInfo(descriptor.Path);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start the CCCG dispatch worker.");
        var request = new DispatchBackendRequest
        {
            Operation = operation,
            Arguments = JsonSerializer.SerializeToElement(arguments, JsonOptions)
        };
        process.StandardInput.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        process.StandardInput.Close();

        var timeout = operation == "dispatchWait"
            ? TimeSpan.FromHours(2)
            : TimeSpan.FromSeconds(30);
        var responseTask = process.StandardOutput.ReadLineAsync();
        if (!responseTask.Wait(timeout))
        {
            TryKill(process);
            throw new TimeoutException($"CCCG worker operation '{operation}' timed out.");
        }

        var line = responseTask.Result;
        if (string.IsNullOrWhiteSpace(line))
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidDataException(
                "CCCG dispatch worker returned no JSON response. " + Truncate(error, 1000));
        }

        var response = JsonSerializer.Deserialize<DispatchBackendResponse>(line, JsonOptions)
            ?? throw new InvalidDataException("CCCG dispatch worker response is empty.");
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "CCCG dispatch worker failed.");
        }

        return response.Payload ?? "{}";
    }

    public static ProcessStartInfo BuildWorkerStartInfo(string path)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        RecursionContext.ApplyInheritedEnvironment(info);
        return info;
    }

    public DispatchWorkerDescriptor ResolveWorker()
    {
        var configured = Environment.GetEnvironmentVariable("CCCG_DISPATCH_WORKER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return DescriptorFor(configured, "environment");
        }

        var descriptorPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "worker-current.json");
        if (File.Exists(descriptorPath))
        {
            var descriptor = JsonSerializer.Deserialize<DispatchWorkerDescriptor>(
                File.ReadAllText(descriptorPath),
                JsonOptions);
            if (descriptor is { SchemaVersion: 1 }
                && !string.IsNullOrWhiteSpace(descriptor.Path))
            {
                return descriptor;
            }
        }

        var sibling = Path.Combine(AppContext.BaseDirectory, "worker", "cccg-dispatch-worker.exe");
        if (File.Exists(sibling))
        {
            return DescriptorFor(sibling, "bundled");
        }

        throw new InvalidOperationException(
            $"No CCCG dispatch worker is installed. Expected '{descriptorPath}'.");
    }

    private static DispatchWorkerDescriptor DescriptorFor(string path, string version)
    {
        path = Path.GetFullPath(path);
        return new DispatchWorkerDescriptor
        {
            SchemaVersion = 1,
            Path = path,
            Version = version,
            Sha256 = File.Exists(path) ? Hash(path) : "",
            InstalledAtUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTimeOffset.MinValue
        };
    }

    private static void VerifyWorker(DispatchWorkerDescriptor descriptor)
    {
        if (!File.Exists(descriptor.Path))
        {
            throw new FileNotFoundException("CCCG dispatch worker does not exist.", descriptor.Path);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Sha256)
            && !string.Equals(Hash(descriptor.Path), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "CCCG dispatch worker hash does not match worker-current.json; refusing a partial update.");
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];
}
