namespace CCCG.Host;

internal sealed record HostOptions
{
    public string Command { get; init; } = "run";

    public string? WorkerPath { get; init; }

    public string StateDirectory { get; init; } = DefaultStateDirectory();

    public string DataDirectory { get; init; } = DefaultDataDirectory();

    public string? MainLog { get; init; }

    public List<string> SessionRoots { get; init; } = [];

    public string? TranscriptRoot { get; init; }

    public int HistoryLines { get; init; } = 2000;

    public bool CaptureContent { get; init; }

    public bool NoDashboard { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan HandoffOverlap { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan CommandWait { get; init; } = TimeSpan.FromSeconds(30);

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        var command = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0].ToLowerInvariant()
            : "run";
        string? worker = null;
        string? stateDirectory = null;
        string? dataDirectory = null;
        string? mainLog = null;
        string? transcriptRoot = null;
        var sessionRoots = new List<string>();
        var history = 2000;
        var captureContent = false;
        var noDashboard = false;
        var readyTimeout = TimeSpan.FromSeconds(20);
        var stopTimeout = TimeSpan.FromSeconds(10);
        var poll = TimeSpan.FromMilliseconds(500);
        var overlap = TimeSpan.FromSeconds(1);
        var wait = TimeSpan.FromSeconds(30);

        var start = command == "run" && (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            ? 0
            : 1;
        for (var index = start; index < args.Count; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--worker":
                    worker = RequiredValue(args, ref index, current);
                    break;
                case "--state-dir":
                    stateDirectory = RequiredValue(args, ref index, current);
                    break;
                case "--data-dir":
                    dataDirectory = RequiredValue(args, ref index, current);
                    break;
                case "--main-log":
                    mainLog = RequiredValue(args, ref index, current);
                    break;
                case "--session-root":
                    sessionRoots.Add(RequiredValue(args, ref index, current));
                    break;
                case "--transcript-root":
                    transcriptRoot = RequiredValue(args, ref index, current);
                    break;
                case "--history":
                    history = NonNegativeInteger(RequiredValue(args, ref index, current), current);
                    break;
                case "--capture-content":
                    captureContent = true;
                    break;
                case "--no-dashboard":
                    noDashboard = true;
                    break;
                case "--ready-timeout":
                    readyTimeout = PositiveSeconds(RequiredValue(args, ref index, current), current);
                    break;
                case "--stop-timeout":
                    stopTimeout = PositiveSeconds(RequiredValue(args, ref index, current), current);
                    break;
                case "--poll-ms":
                    poll = PositiveMilliseconds(RequiredValue(args, ref index, current), current);
                    break;
                case "--overlap-ms":
                    overlap = NonNegativeMilliseconds(RequiredValue(args, ref index, current), current);
                    break;
                case "--wait":
                    wait = PositiveSeconds(RequiredValue(args, ref index, current), current);
                    break;
                case "--help":
                case "-h":
                    return new HostOptions { Command = "help" };
                default:
                    throw new ArgumentException($"Unknown option '{current}'.");
            }
        }

        if (command is not ("run" or "update" or "status" or "stop" or "help"))
        {
            throw new ArgumentException($"Unknown command '{command}'.");
        }

        return new HostOptions
        {
            Command = command,
            WorkerPath = worker,
            StateDirectory = Path.GetFullPath(stateDirectory ?? DefaultStateDirectory()),
            DataDirectory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory()),
            MainLog = mainLog is null ? null : Path.GetFullPath(mainLog),
            SessionRoots = sessionRoots.Select(Path.GetFullPath).ToList(),
            TranscriptRoot = transcriptRoot is null ? null : Path.GetFullPath(transcriptRoot),
            HistoryLines = history,
            CaptureContent = captureContent,
            NoDashboard = noDashboard,
            ReadyTimeout = readyTimeout,
            StopTimeout = stopTimeout,
            PollInterval = poll,
            HandoffOverlap = overlap,
            CommandWait = wait
        };
    }

    private static string RequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[++index];
    }

    private static int NonNegativeInteger(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{option} must be zero or greater.");

    private static TimeSpan PositiveSeconds(string value, string option) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : throw new ArgumentException($"{option} must be greater than zero.");

    private static TimeSpan PositiveMilliseconds(string value, string option) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? TimeSpan.FromMilliseconds(parsed)
            : throw new ArgumentException($"{option} must be greater than zero.");

    private static TimeSpan NonNegativeMilliseconds(string value, string option) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? TimeSpan.FromMilliseconds(parsed)
            : throw new ArgumentException($"{option} must be zero or greater.");

    private static string DefaultStateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCCG",
        "host");

    private static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCCG",
        "monitor-data");
}
