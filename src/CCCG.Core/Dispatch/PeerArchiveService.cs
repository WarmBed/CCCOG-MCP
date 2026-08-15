using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed record ArchiveFileEntry(
    string OriginalPath,
    string ArchivedPath,
    long Length,
    string Sha256);

public sealed class ArchiveManifest
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public int SchemaVersion { get; set; } = 1;

    public string Provider { get; set; } = "";

    public string SessionId { get; set; } = "";

    public DateTimeOffset ArchivedAtUtc { get; set; }

    public List<ArchiveFileEntry> Files { get; set; } = new();

    public List<string> IndexEntries { get; set; } = new();

    public static ArchiveManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(path), ReadOptions);
        return manifest ?? throw new InvalidDataException("Archive manifest is empty.");
    }
}

public sealed record PeerArchiveResult(
    string Status,
    string Provider,
    string SessionId,
    string? ArchivePath,
    string? ManifestPath,
    string Message);

/// <summary>
/// Moves closed provider session files into a recoverable cccg-archive tree.
/// No provider delete command is called and every partial move is rolled back.
/// </summary>
public sealed class PeerArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string codexHome;
    private readonly string grokHome;
    private readonly string claudeHome;
    private readonly OwnerRegistry owners;
    private readonly Func<string, string, bool>? liveProbe;
    private readonly Func<string, string, bool>? ownerProbe;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string, bool>? manifestCanWrite;

    public PeerArchiveService(
        string? codexHome = null,
        string? grokHome = null,
        string? claudeHome = null,
        OwnerRegistry? owners = null,
        Func<string, string, bool>? liveProbe = null,
        Func<string, string, bool>? ownerProbe = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, bool>? manifestCanWrite = null)
    {
        this.codexHome = codexHome ?? CodexHome.Resolve().Path;
        this.grokHome = grokHome ?? GrokHome.Resolve().Path;
        this.claudeHome = claudeHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        this.owners = owners ?? new OwnerRegistry();
        this.liveProbe = liveProbe;
        this.ownerProbe = ownerProbe;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.manifestCanWrite = manifestCanWrite;
    }

    public PeerArchiveResult Archive(string provider, string sessionId)
    {
        provider = NormalizeProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        sessionId = sessionId.Trim();
        if (provider is not ("codex" or "grok" or "claude"))
        {
            throw new ArgumentException("Provider must be codex, grok, or claude.");
        }

        if (IsLive(provider, sessionId))
        {
            return Result("live_refused", provider, sessionId,
                "Live provider writers are inspection-only; no files were moved.");
        }

        if (IsOwned(provider, sessionId))
        {
            return Result("owner_refused", provider, sessionId,
                "A running CCCG owner owns this session; no files were moved.");
        }

        var plan = BuildPlan(provider, sessionId);
        if (plan is null || plan.Files.Count == 0)
        {
            return Result("not_found", provider, sessionId,
                $"No closed {provider} session files for '{sessionId}'.");
        }

        var lockPath = plan.LockPath;
        var gate = CrossProcessFileGate.Acquire(lockPath, TimeSpan.FromSeconds(30));
        try
        {
            if (IsLive(provider, sessionId))
            {
                return Result("live_refused", provider, sessionId,
                    "Session became live before archive; no files were moved.");
            }

            if (IsOwned(provider, sessionId))
            {
                return Result("owner_refused", provider, sessionId,
                    "A CCCG owner claimed the session before archive; no files were moved.");
            }

            if (Directory.Exists(plan.ArchivePath))
            {
                return Result("conflict", provider, sessionId,
                    "Archive destination already exists; source was left intact.",
                    plan.ArchivePath);
            }

            var manifest = new ArchiveManifest
            {
                Provider = provider,
                SessionId = sessionId,
                ArchivedAtUtc = plan.ArchivedAtUtc,
                Files = plan.Files.Select(file => new ArchiveFileEntry(
                    file.SourcePath,
                    file.DestinationPath,
                    file.Length,
                    file.Sha256)).ToList(),
                IndexEntries = plan.IndexEntries
            };
            var manifestText = JsonSerializer.Serialize(manifest, JsonOptions);
            var manifestPath = Path.Combine(plan.ArchivePath, "manifest.json");
            if (manifestCanWrite is not null && !manifestCanWrite(manifestPath))
            {
                return Result("failed", provider, sessionId,
                    "Archive manifest preflight failed; source was left intact.",
                    plan.ArchivePath, manifestPath);
            }

            var moved = new List<ArchivePlanFile>();
            var indexChanged = false;
            try
            {
                Directory.CreateDirectory(plan.ArchivePath);
                foreach (var file in plan.Files)
                {
                    EnsureUnder(plan.ArchivePath, file.DestinationPath);
                    EnsureUnder(plan.StorageRoot, file.SourcePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(file.DestinationPath)!);
                    File.Move(file.SourcePath, file.DestinationPath);
                    moved.Add(file);
                }

                foreach (var file in moved)
                {
                    DeleteEmptyParents(file.SourcePath, plan.StorageRoot);
                }

                if (plan.IndexPath is not null && plan.FilteredIndexText is not null)
                {
                    CrossProcessFileGate.AtomicWriteAllText(plan.IndexPath, plan.FilteredIndexText);
                    indexChanged = true;
                }

                CrossProcessFileGate.AtomicWriteAllText(manifestPath, manifestText);
                return new PeerArchiveResult(
                    "archived",
                    provider,
                    sessionId,
                    plan.ArchivePath,
                    manifestPath,
                    "Session files moved to cccg-archive; manifest records manual restore paths.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (indexChanged && plan.IndexPath is not null && plan.OriginalIndexText is not null)
                {
                    TryAtomicRestore(plan.IndexPath, plan.OriginalIndexText);
                }

                foreach (var file in moved.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(file.DestinationPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(file.SourcePath)!);
                            File.Move(file.DestinationPath, file.SourcePath);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                TryDeleteEmpty(plan.ArchivePath);
                return Result("failed", provider, sessionId,
                    "Archive failed and moved files were rolled back: " + exception.Message,
                    plan.ArchivePath, manifestPath);
            }
        }
        finally
        {
            gate.Dispose();
            TryDeleteArchiveLock(lockPath);
        }
    }

    private ArchivePlan? BuildPlan(string provider, string sessionId)
    {
        return provider switch
        {
            "codex" => BuildCodexPlan(sessionId),
            "grok" => BuildGrokPlan(sessionId),
            "claude" => BuildClaudePlan(sessionId),
            _ => null
        };
    }

    private ArchivePlan? BuildCodexPlan(string sessionId)
    {
        var storageRoot = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(storageRoot))
        {
            return null;
        }

        var files = new List<ArchivePlanFile>();
        var archiveFolder = ArchiveFolder(sessionId);
        foreach (var path in Directory.EnumerateFiles(storageRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            if (IsArchivePath(path) || !RolloutMatches(path, sessionId))
            {
                continue;
            }

            files.Add(FilePlan(storageRoot, path, Path.Combine(
                storageRoot, "cccg-archive", archiveFolder,
                Path.GetRelativePath(storageRoot, path))));
        }

        if (files.Count == 0)
        {
            return null;
        }

        var indexPath = Path.Combine(codexHome, "session_index.jsonl");
        var original = File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
        var entries = new List<string>();
        string? filtered = original;
        if (original is not null)
        {
            var kept = new List<string>();
            foreach (var line in original.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (string.Equals(String(document.RootElement, "id"), sessionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(line);
                        continue;
                    }
                }
                catch (JsonException)
                {
                }

                kept.Add(line);
            }

            filtered = kept.Count == 0 ? "" : string.Join(Environment.NewLine, kept) + Environment.NewLine;
        }

        return new ArchivePlan(
            storageRoot,
            files,
            Path.Combine(storageRoot, "cccg-archive", archiveFolder),
            Path.Combine(storageRoot, sessionId + ".cccg-archive.lock"),
            clock().ToUniversalTime(),
            indexPath,
            original,
            filtered,
            entries);
    }

    private ArchivePlan? BuildGrokPlan(string sessionId)
    {
        var storageRoot = Path.Combine(grokHome, "sessions");
        var summary = Directory.Exists(storageRoot)
            ? Directory.EnumerateFiles(storageRoot, "summary.json", SearchOption.AllDirectories)
                .FirstOrDefault(path => !IsArchivePath(path) && SummaryMatches(path, sessionId))
            : null;
        if (summary is null)
        {
            return null;
        }

        var sessionDir = Path.GetDirectoryName(summary)!;
        var archiveFolder = ArchiveFolder(sessionId);
        var files = Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
            .Where(path => !IsArchivePath(path))
            .Select(path => FilePlan(sessionDir, path, Path.Combine(
                storageRoot, "cccg-archive", archiveFolder,
                Path.GetRelativePath(sessionDir, path))))
            .ToList();
        return new ArchivePlan(
            storageRoot,
            files,
            Path.Combine(storageRoot, "cccg-archive", archiveFolder),
            sessionDir + ".cccg-archive.lock",
            clock().ToUniversalTime(),
            null,
            null,
            null,
            new List<string>());
    }

    private ArchivePlan? BuildClaudePlan(string sessionId)
    {
        var storageRoot = Path.Combine(claudeHome, "projects");
        var transcript = Directory.Exists(storageRoot)
            ? Directory.EnumerateFiles(storageRoot, sessionId + ".jsonl", SearchOption.AllDirectories)
                .FirstOrDefault(path => !IsArchivePath(path))
            : null;
        if (transcript is null)
        {
            return null;
        }

        var project = Path.GetDirectoryName(transcript)!;
        var archiveFolder = ArchiveFolder(sessionId);
        var files = new List<ArchivePlanFile>
        {
            FilePlan(project, transcript, Path.Combine(
                storageRoot, "cccg-archive", Path.GetFileName(project),
                archiveFolder, Path.GetFileName(transcript)))
        };
        var sidecar = Path.Combine(project, sessionId);
        if (Directory.Exists(sidecar))
        {
            files.AddRange(Directory.EnumerateFiles(sidecar, "*", SearchOption.AllDirectories)
                .Select(path => FilePlan(sidecar, path, Path.Combine(
                    storageRoot, "cccg-archive", Path.GetFileName(project),
                    archiveFolder, sessionId,
                    Path.GetRelativePath(sidecar, path)))));
        }

        return new ArchivePlan(
            storageRoot,
            files,
            Path.Combine(storageRoot, "cccg-archive", Path.GetFileName(project), archiveFolder),
            transcript + ".cccg-archive.lock",
            clock().ToUniversalTime(),
            null,
            null,
            null,
            new List<string>());
    }

    private ArchivePlanFile FilePlan(string storageRoot, string source, string destination)
    {
        var info = new FileInfo(source);
        return new ArchivePlanFile(source, destination, info.Length, Hash(source));
    }

    private static bool RolloutMatches(string path, string sessionId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("payload", out var payload)
                && string.Equals(String(payload, "session_id"), sessionId,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static bool SummaryMatches(string path, string sessionId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var info = document.RootElement.TryGetProperty("info", out var value)
                ? value
                : default;
            return string.Equals(String(info, "id"), sessionId,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), sessionId,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private bool IsLive(string provider, string sessionId)
    {
        if (liveProbe is not null)
        {
            return liveProbe(provider, sessionId);
        }

        return provider switch
        {
            "codex" => CodexLockIsHeld(sessionId),
            "grok" => ActivePidIsAlive(Path.Combine(grokHome, "active_sessions.json"), sessionId),
            "claude" => ActivePidIsAlive(Path.Combine(claudeHome, "sessions"), sessionId),
            _ => false
        };
    }

    private bool CodexLockIsHeld(string sessionId)
    {
        var path = Path.Combine(codexHome, "thread-writer-locks", sessionId + ".lock");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ActivePidIsAlive(string path, string sessionId)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    var root = document.RootElement;
                    if (string.Equals(String(root, "sessionId"), sessionId,
                            StringComparison.OrdinalIgnoreCase)
                        && PidIsAlive(root))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                }
            }

            return false;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (string.Equals(String(row, "session_id"), sessionId,
                        StringComparison.OrdinalIgnoreCase)
                    && PidIsAlive(row))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        return false;
    }

    private static bool PidIsAlive(JsonElement row)
    {
        if (!row.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var processId))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsOwned(string provider, string sessionId) =>
        ownerProbe?.Invoke(provider, sessionId)
        ?? owners.TryFind(provider, sessionId, cwd: null) is not null;

    private string ArchiveFolder(string sessionId) =>
        clock().ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ") + "_" + sessionId;

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Archive path escapes its provider root.");
        }
    }

    private static void TryAtomicRestore(string path, string text)
    {
        try
        {
            CrossProcessFileGate.AtomicWriteAllText(path, text);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteArchiveLock(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteEmptyParents(string filePath, string stopRoot)
    {
        var current = Path.GetDirectoryName(filePath);
        var fullRoot = Path.GetFullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(current)
               && !string.Equals(
                   Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar),
                   fullRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(current)
                    || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
            }
            catch (IOException)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsArchivePath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "cccg-archive", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeProvider(string provider) =>
        (provider ?? "").Trim().ToLowerInvariant();

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PeerArchiveResult Result(
        string status,
        string provider,
        string sessionId,
        string message,
        string? archivePath = null,
        string? manifestPath = null) =>
        new(status, provider, sessionId, archivePath, manifestPath, message);

    private sealed record ArchivePlan(
        string StorageRoot,
        List<ArchivePlanFile> Files,
        string ArchivePath,
        string LockPath,
        DateTimeOffset ArchivedAtUtc,
        string? IndexPath,
        string? OriginalIndexText,
        string? FilteredIndexText,
        List<string> IndexEntries);

    private sealed record ArchivePlanFile(
        string SourcePath,
        string DestinationPath,
        long Length,
        string Sha256);
}
