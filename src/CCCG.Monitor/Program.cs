using CCCG.Core.Monitoring;
using CCCG.Monitor;

try
{
    var options = MonitorOptions.Parse(args);
    if (options.Command == "help")
    {
        PrintHelp();
        return 0;
    }

    var discovered = SourceDiscovery.Discover();
    var sources = new MonitorSources(
        options.MainLog ?? discovered.MainLog,
        options.SessionRoots.Count > 0 ? options.SessionRoots : discovered.SessionRoots,
        options.TranscriptRoot ?? discovered.TranscriptRoot);

    switch (options.Command)
    {
        case "doctor":
            return Doctor(sources, options.DataDirectory, options.CaptureContent);
        case "replay":
            return Replay(options, sources);
        case "mark":
            return Mark(options);
        case "watch":
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                return await new LiveMonitor(options, sources).RunAsync(cancellation.Token);
            }
        default:
            Console.Error.WriteLine($"Unknown command '{options.Command}'.");
            PrintHelp();
            return 2;
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine("cccg-monitor: " + exception.Message);
    return 2;
}

static int Doctor(MonitorSources sources, string dataDirectory, bool captureContent)
{
    Console.WriteLine("CCCG monitor doctor (read-only)");
    Console.WriteLine($"main.log:       {DescribeFile(sources.MainLog)}");
    Console.WriteLine($"session roots:  {sources.SessionRoots.Count}");
    foreach (var root in sources.SessionRoots)
    {
        var count = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories).Count()
            : 0;
        Console.WriteLine($"  {MonitorPathRedactor.Redact(root)} ({count} session metadata files)");
    }

    Console.WriteLine($"transcripts:    {(sources.TranscriptRoot is null ? "not found" : MonitorPathRedactor.Redact(sources.TranscriptRoot))}");
    Console.WriteLine($"data directory: {MonitorPathRedactor.Redact(dataDirectory)}");
    var processes = ClaudeProcessProbe.Snapshot();
    Console.WriteLine($"Claude processes: {processes.Count}");
    foreach (var group in processes.GroupBy(process => process.Role).OrderBy(group => group.Key))
    {
        Console.WriteLine($"  {group.Key}: {group.Count()}");
    }

    Console.WriteLine($"content capture: {(captureContent ? "ENABLED for a subsequent watch run" : "disabled")}");
    return sources.MainLog is not null && File.Exists(sources.MainLog) ? 0 : 2;
}

static int Replay(MonitorOptions options, MonitorSources sources)
{
    var input = options.ReplayInput ?? sources.MainLog;
    if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
    {
        Console.Error.WriteLine("Replay input was not found. Use --input <main.log>.");
        return 2;
    }

    var pseudonymizer = StablePseudonymizer.LoadOrCreate(options.DataDirectory);
    var parser = new ClaudeLogParser();
    var deduplicator = new EventDeduplicator();
    using var dataset = MonitorDataset.Create(options.DataDirectory, new[] { input }, "replay");
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var line in SharedFileReader.ReadLines(input))
    {
        var observed = DateTimeOffset.UtcNow;
        if (!deduplicator.ShouldEmit(line, observed)
            || !parser.TryParse(line, out var signal)
            || signal is null)
        {
            continue;
        }

        dataset.Write(new MonitorEvent
        {
            ObservedAtUtc = observed,
            SourceTime = signal.SourceTime,
            Source = "desktop-main-log",
            Event = signal.Event,
            Level = signal.Level,
            SessionKey = signal.RawSessionId is null ? null : pseudonymizer.Session(signal.RawSessionId),
            Historical = true,
            Data = signal.Data
        });
        counts[signal.Event] = counts.GetValueOrDefault(signal.Event) + 1;
    }

    Console.WriteLine($"Dataset: {dataset.RunDirectory}");
    Console.WriteLine($"Normalized events: {dataset.EventCount}");
    foreach (var pair in counts.OrderByDescending(pair => pair.Value))
    {
        Console.WriteLine($"  {pair.Key}: {pair.Value}");
    }

    return 0;
}

static int Mark(MonitorOptions options)
{
    if (options.Label is not { Length: > 0 } label
        || label.Length > 80
        || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
    {
        Console.Error.WriteLine("--label is required and may contain 1-80 ASCII letters, digits, '-', '_', '.', or ':'.");
        return 2;
    }

    Directory.CreateDirectory(options.DataDirectory);
    var path = Path.Combine(options.DataDirectory, "markers.jsonl");
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
    writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        new { createdAtUtc = DateTimeOffset.UtcNow, label }));
    Console.WriteLine($"Marker written: {label}");
    return 0;
}

static string DescribeFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        return "not found";
    }

    var file = new FileInfo(path);
    return $"{MonitorPathRedactor.Redact(path)} ({file.Length} bytes, {file.LastWriteTime:O})";
}

static void PrintHelp()
{
    Console.WriteLine("""
        cccg-monitor - read-only Claude Desktop lifecycle monitor

        Commands:
          cccg-monitor doctor
          cccg-monitor watch [options]
          cccg-monitor replay --input <main.log> [options]
          cccg-monitor mark --label <TEST-ID>

        Options:
          --main-log <path>         Override discovered Desktop main.log
          --session-root <path>     Add/override session metadata root (repeatable)
          --transcript-root <path>  Override transcript metadata root
          --data-dir <path>         Dataset root (default: %LOCALAPPDATA%\CCCG\monitor-data)
          --history <lines>         Normalize recent log history before live tail (default: 2000)
          --duration <seconds>      Stop automatically after a bounded live run
          --no-dashboard            Print one line per normalized event
          --quiet                   Suppress event lines (dataset still records everything)
          --capture-content         Store new user/assistant text in a separate local content.jsonl
          --ready-file <path>       Supervisor readiness file (internal)
          --stop-file <path>        Supervisor cooperative-stop signal (internal)
          --generation <id>         Supervisor generation identifier (internal)
          --label <TEST-ID>         Short payload-free label for a live test marker

        Content capture is off by default. When enabled it records only transcript lines appended
        after startup, and only user text plus visible assistant text. It excludes thinking,
        tool inputs/results, attachments, raw Desktop log lines, credentials, titles, and cwd.
        """);
}
