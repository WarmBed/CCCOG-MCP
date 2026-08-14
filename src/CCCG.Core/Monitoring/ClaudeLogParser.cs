using System.Globalization;
using System.Text.RegularExpressions;

namespace CCCG.Core.Monitoring;

public sealed record ParsedClaudeSignal(
    string Event,
    string Level,
    DateTimeOffset SourceTime,
    string? RawSessionId,
    IReadOnlyDictionary<string, object?> Data);

public sealed partial class ClaudeLogParser
{
    [GeneratedRegex("^(?<time>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}) \\[(?<level>[^]]+)\\] (?<message>.*)$")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex("local_[0-9a-fA-F-]{36}")]
    private static partial Regex SessionRegex();

    [GeneratedRegex("\\((?<seconds>\\d+)s, hadFirstResponse=(?<first>true|false)(?:, reason=(?<reason>[^)]+))?\\)")]
    private static partial Regex CycleRegex();

    [GeneratedRegex("Loaded (?<count>\\d+) transcript messages")]
    private static partial Regex LoadedRegex();

    [GeneratedRegex("timed out after (?<seconds>\\d+)s of inactivity \\((?<details>[^)]*)\\)")]
    private static partial Regex TimeoutRegex();

    [GeneratedRegex("(?<key>[a-z_]+)=(?<value>\\d+)(?<unit>ms|s)?")]
    private static partial Regex MetricRegex();

    [GeneratedRegex("(?<count>\\d+) active background task")]
    private static partial Regex BackgroundTaskRegex();

    [GeneratedRegex("\\((?<reason>[a-z_]+)\\)$")]
    private static partial Regex PauseReasonRegex();

    public bool TryParse(string line, out ParsedClaudeSignal? signal)
    {
        signal = null;
        var prefix = PrefixRegex().Match(line);
        if (!prefix.Success
            || !DateTime.TryParseExact(
                prefix.Groups["time"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
        {
            return false;
        }

        var sourceTime = new DateTimeOffset(
            localTime,
            TimeZoneInfo.Local.GetUtcOffset(localTime));
        var level = prefix.Groups["level"].Value;
        var message = prefix.Groups["message"].Value;
        var sessionMatch = SessionRegex().Match(message);
        var rawSessionId = sessionMatch.Success ? sessionMatch.Value : null;

        if (message.Contains("Sending message to session", StringComparison.Ordinal))
        {
            signal = Signal("session.message_sent", level, sourceTime, rawSessionId);
            return true;
        }

        if (message.Contains("Resuming session", StringComparison.Ordinal))
        {
            signal = Signal("session.resuming", level, sourceTime, rawSessionId);
            return true;
        }

        if (message.Contains("Starting local session", StringComparison.Ordinal))
        {
            signal = Signal("session.starting", level, sourceTime, rawSessionId);
            return true;
        }

        if (message.Contains("[CCD] Warming session", StringComparison.Ordinal))
        {
            signal = Signal("session.warming", level, sourceTime, rawSessionId);
            return true;
        }

        if (message.Contains("[CCD] Pausing session", StringComparison.Ordinal))
        {
            var reason = PauseReasonRegex().Match(message);
            signal = Signal(
                "session.pausing",
                level,
                sourceTime,
                rawSessionId,
                ("reason", reason.Success ? reason.Groups["reason"].Value : null));
            return true;
        }

        if (message.Contains("[CCD] Skipping pause for session", StringComparison.Ordinal))
        {
            var tasks = BackgroundTaskRegex().Match(message);
            signal = Signal(
                "session.pause_skipped",
                level,
                sourceTime,
                rawSessionId,
                ("activeBackgroundTasks", tasks.Success ? ParseInt(tasks.Groups["count"].Value) : null));
            return true;
        }

        if (message.Contains("LocalSessions.setFocusedSession", StringComparison.Ordinal))
        {
            signal = Signal(
                rawSessionId is null ? "desktop.focus_cleared" : "session.focused",
                level,
                sourceTime,
                rawSessionId);
            return true;
        }

        if (message.Contains("torn down with", StringComparison.Ordinal))
        {
            var tasks = BackgroundTaskRegex().Match(message);
            signal = Signal(
                "session.torn_down",
                level,
                sourceTime,
                rawSessionId,
                ("liveBackgroundTasks", tasks.Success ? ParseInt(tasks.Groups["count"].Value) : null));
            return true;
        }

        var loaded = LoadedRegex().Match(message);
        if (loaded.Success)
        {
            signal = Signal(
                "session.transcript_loaded",
                level,
                sourceTime,
                rawSessionId,
                ("messageCount", ParseInt(loaded.Groups["count"].Value)));
            return true;
        }

        if (message.Contains("[CCD start-timing]", StringComparison.Ordinal))
        {
            var metrics = new Dictionary<string, object?>();
            foreach (Match metric in MetricRegex().Matches(message))
            {
                metrics[metric.Groups["key"].Value] = ParseInt(metric.Groups["value"].Value);
            }

            signal = new ParsedClaudeSignal(
                "session.start_timing",
                level,
                sourceTime,
                rawSessionId,
                metrics);
            return true;
        }

        if (message.Contains("[CCD CycleHealth]", StringComparison.Ordinal))
        {
            var cycle = CycleRegex().Match(message);
            if (!cycle.Success)
            {
                return false;
            }

            var healthy = message.Contains("healthy cycle", StringComparison.Ordinal)
                && !message.Contains("unhealthy cycle", StringComparison.Ordinal);
            var data = new Dictionary<string, object?>
            {
                ["durationSeconds"] = ParseInt(cycle.Groups["seconds"].Value),
                ["hadFirstResponse"] = bool.Parse(cycle.Groups["first"].Value)
            };
            if (cycle.Groups["reason"].Success)
            {
                data["reason"] = cycle.Groups["reason"].Value;
            }

            signal = new ParsedClaudeSignal(
                healthy ? "session.cycle_healthy" : "session.cycle_unhealthy",
                healthy ? level : "warn",
                sourceTime,
                rawSessionId,
                data);
            return true;
        }

        var timeout = TimeoutRegex().Match(message);
        if (timeout.Success)
        {
            var data = new Dictionary<string, object?>
            {
                ["inactivitySeconds"] = ParseInt(timeout.Groups["seconds"].Value)
            };
            foreach (var part in timeout.Groups["details"].Value.Split(','))
            {
                var pair = part.Trim().Split('=', 2);
                if (pair.Length == 2 && pair[0] is "hadFirstResponse" or "last_message_type" or "last_tool_name" or "seconds_since_stderr")
                {
                    data[ToCamel(pair[0])] = pair[1];
                }
            }

            signal = new ParsedClaudeSignal(
                "session.timeout",
                "warn",
                sourceTime,
                rawSessionId,
                data);
            return true;
        }

        return false;
    }

    private static ParsedClaudeSignal Signal(
        string eventName,
        string level,
        DateTimeOffset sourceTime,
        string? rawSessionId,
        params (string Key, object? Value)[] values) =>
        new(
            eventName,
            level,
            sourceTime,
            rawSessionId,
            values.ToDictionary(value => value.Key, value => value.Value));

    private static int ParseInt(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string ToCamel(string value)
    {
        var parts = value.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(
            part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

public sealed class EventDeduplicator
{
    private readonly TimeSpan window;
    private readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);

    public EventDeduplicator(TimeSpan? window = null)
    {
        this.window = window ?? TimeSpan.FromSeconds(2);
    }

    public bool ShouldEmit(string signature, DateTimeOffset observedAt)
    {
        if (seen.TryGetValue(signature, out var prior) && observedAt - prior <= window)
        {
            return false;
        }

        seen[signature] = observedAt;
        if (seen.Count > 4096)
        {
            var cutoff = observedAt - window - window;
            foreach (var key in seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            {
                seen.Remove(key);
            }
        }

        return true;
    }
}
