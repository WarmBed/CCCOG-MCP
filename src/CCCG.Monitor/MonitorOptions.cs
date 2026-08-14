namespace CCCG.Monitor;

internal sealed record MonitorOptions
{
    public string Command { get; init; } = "watch";

    public string? MainLog { get; init; }

    public List<string> SessionRoots { get; init; } = [];

    public string? TranscriptRoot { get; init; }

    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCCG",
        "monitor-data");

    public int HistoryLines { get; init; } = 2000;

    public TimeSpan? Duration { get; init; }

    public bool NoDashboard { get; init; }

    public bool Quiet { get; init; }

    public bool CaptureContent { get; init; }

    public string? ReplayInput { get; init; }

    public string? Label { get; init; }

    public string? ReadyFile { get; init; }

    public string? StopFile { get; init; }

    public string? Generation { get; init; }

    public static MonitorOptions Parse(IReadOnlyList<string> args)
    {
        var command = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0].ToLowerInvariant()
            : "watch";
        string? mainLog = null;
        string? transcriptRoot = null;
        string? replayInput = null;
        string? dataDirectory = null;
        string? label = null;
        string? readyFile = null;
        string? stopFile = null;
        string? generation = null;
        var roots = new List<string>();
        var history = 2000;
        TimeSpan? duration = null;
        var noDashboard = false;
        var quiet = false;
        var captureContent = false;

        var start = command == "watch" && (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            ? 0
            : 1;
        for (var index = start; index < args.Count; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--main-log":
                    mainLog = RequiredValue(args, ref index, current);
                    break;
                case "--session-root":
                    roots.Add(RequiredValue(args, ref index, current));
                    break;
                case "--transcript-root":
                    transcriptRoot = RequiredValue(args, ref index, current);
                    break;
                case "--data-dir":
                    dataDirectory = RequiredValue(args, ref index, current);
                    break;
                case "--history":
                    history = int.Parse(RequiredValue(args, ref index, current), System.Globalization.CultureInfo.InvariantCulture);
                    if (history < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(args), "--history must be zero or greater.");
                    }

                    break;
                case "--duration":
                    var seconds = double.Parse(
                        RequiredValue(args, ref index, current),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (seconds <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(args), "--duration must be greater than zero.");
                    }

                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                case "--input":
                    replayInput = RequiredValue(args, ref index, current);
                    break;
                case "--label":
                    label = RequiredValue(args, ref index, current);
                    break;
                case "--no-dashboard":
                    noDashboard = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--capture-content":
                    captureContent = true;
                    break;
                case "--ready-file":
                    readyFile = RequiredValue(args, ref index, current);
                    break;
                case "--stop-file":
                    stopFile = RequiredValue(args, ref index, current);
                    break;
                case "--generation":
                    generation = RequiredValue(args, ref index, current);
                    break;
                case "--help":
                case "-h":
                    return new MonitorOptions { Command = "help" };
                default:
                    throw new ArgumentException($"Unknown option '{current}'.");
            }
        }

        return new MonitorOptions
        {
            Command = command,
            MainLog = mainLog,
            SessionRoots = roots,
            TranscriptRoot = transcriptRoot,
            DataDirectory = dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CCCG",
                "monitor-data"),
            HistoryLines = history,
            Duration = duration,
            NoDashboard = noDashboard,
            Quiet = quiet,
            CaptureContent = captureContent,
            ReplayInput = replayInput,
            Label = label,
            ReadyFile = readyFile,
            StopFile = stopFile,
            Generation = generation
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
}
