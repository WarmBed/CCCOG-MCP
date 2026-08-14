using CCCG.Core.Monitoring;

namespace CCCG.Monitor;

internal sealed class LiveDashboard
{
    private readonly Dictionary<string, SessionRow> sessions = new(StringComparer.Ordinal);
    private readonly Queue<string> recentEvents = new();
    private readonly Dictionary<string, int> processCounts = new(StringComparer.Ordinal);
    private DateTimeOffset lastRender = DateTimeOffset.MinValue;

    public void Accept(MonitorEvent monitorEvent)
    {
        if (monitorEvent.SessionKey is { } key)
        {
            if (!sessions.TryGetValue(key, out var row))
            {
                row = new SessionRow(key);
                sessions[key] = row;
            }

            row.LastEvent = monitorEvent.Event;
            row.LastObservedAt = monitorEvent.ObservedAtUtc;
            row.State = StateFor(monitorEvent.Event, row.State);
            if (monitorEvent.Data.TryGetValue("model", out var model))
            {
                row.Model = model?.ToString() ?? "-";
            }

            if (monitorEvent.Data.TryGetValue("completedTurns", out var turns))
            {
                row.CompletedTurns = turns?.ToString() ?? "-";
            }

            if (monitorEvent.Data.TryGetValue("durationSeconds", out var duration))
            {
                row.Duration = duration + "s";
            }
        }

        if (monitorEvent.Event == "process.snapshot"
            && monitorEvent.Data.TryGetValue("counts", out var counts)
            && counts is IReadOnlyDictionary<string, int> typedCounts)
        {
            processCounts.Clear();
            foreach (var pair in typedCounts)
            {
                processCounts[pair.Key] = pair.Value;
            }
        }

        var shortEvent = $"{monitorEvent.ObservedAtUtc:HH:mm:ss} {monitorEvent.Event} {monitorEvent.SessionKey ?? "-"}";
        recentEvents.Enqueue(shortEvent);
        while (recentEvents.Count > 10)
        {
            recentEvents.Dequeue();
        }
    }

    public void Render(
        string runDirectory,
        long eventCount,
        bool contentCapture,
        long contentCount,
        bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - lastRender < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        lastRender = now;
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            return;
        }

        Console.WriteLine("CCCG LIVE MONITOR  read-only / stdout payload-free");
        Console.WriteLine(
            $"UTC {now:yyyy-MM-dd HH:mm:ss}  events={eventCount}  content={(contentCapture ? $"ON ({contentCount})" : "OFF")}  run={Path.GetFileName(runDirectory)}");
        Console.WriteLine("Processes: " + (processCounts.Count == 0
            ? "waiting for snapshot"
            : string.Join("  ", processCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))));
        Console.WriteLine();
        Console.WriteLine("SESSION         STATE            MODEL                    TURNS  DURATION  AGE");
        Console.WriteLine(new string('-', 88));

        foreach (var row in sessions.Values
                     .OrderByDescending(row => row.LastObservedAt)
                     .Take(18))
        {
            var age = Math.Max(0, (int)(now - row.LastObservedAt).TotalSeconds);
            Console.WriteLine(
                $"{Fit(row.Key, 15)} {Fit(row.State, 16)} {Fit(row.Model, 24)} {Fit(row.CompletedTurns, 6)} {Fit(row.Duration, 9)} {age,4}s");
        }

        if (sessions.Count == 0)
        {
            Console.WriteLine("waiting for session activity...");
        }

        Console.WriteLine();
        Console.WriteLine("RECENT EVENTS");
        foreach (var item in recentEvents)
        {
            Console.WriteLine(item);
        }

        Console.WriteLine();
        Console.WriteLine("Ctrl+C to stop. Dataset is flushed after every event.");
    }

    private static string StateFor(string eventName, string current) => eventName switch
    {
        "session.message_sent" => "MessageSent",
        "session.resuming" => "Resuming",
        "session.starting" => "Starting",
        "session.transcript_loaded" => "TranscriptLoaded",
        "session.transcript_activity" => "TranscriptActive",
        "session.tool_requested" => "ToolRunning",
        "session.tool_completed" => "ToolComplete",
        "session.hook_summary" => "HookComplete",
        "session.start_timing" => "FirstAssistant",
        "session.cycle_healthy" => "Healthy",
        "session.cycle_unhealthy" => "Unhealthy",
        "session.timeout" => "TimedOut",
        _ => current
    };

    private static string Fit(string value, int width) =>
        value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "…";

    private sealed class SessionRow(string key)
    {
        public string Key { get; } = key;

        public string State { get; set; } = "Observed";

        public string Model { get; set; } = "-";

        public string CompletedTurns { get; set; } = "-";

        public string Duration { get; set; } = "-";

        public string LastEvent { get; set; } = "-";

        public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
