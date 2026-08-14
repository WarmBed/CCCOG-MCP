using System.Collections.Concurrent;
using System.Text.Json;
using CCCG.Core.Monitoring;

namespace CCCG.Monitor;

internal sealed class LiveMonitor
{
    private readonly MonitorOptions options;
    private readonly MonitorSources sources;
    private readonly StablePseudonymizer pseudonymizer;
    private readonly ClaudeLogParser parser = new();
    private readonly EventDeduplicator deduplicator = new();
    private readonly TranscriptContentParser contentParser = new();
    private readonly TranscriptOperationParser operationParser = new();
    private readonly LiveDashboard dashboard = new();
    private readonly Dictionary<string, string> cliToSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ClaudeProcessSnapshot> processes = new();
    private readonly Dictionary<string, int> lastProcessCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string Kind)> toolsByOperationKey =
        new(StringComparer.Ordinal);

    public LiveMonitor(MonitorOptions options, MonitorSources sources)
    {
        this.options = options;
        this.sources = sources;
        pseudonymizer = StablePseudonymizer.LoadOrCreate(options.DataDirectory);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sources.MainLog) || !File.Exists(sources.MainLog))
        {
            Console.Error.WriteLine("No Claude Desktop main.log was found. Run 'cccg-monitor doctor'.");
            return 2;
        }

        var sourcePaths = new List<string> { sources.MainLog };
        sourcePaths.AddRange(sources.SessionRoots);
        if (sources.TranscriptRoot is { } transcriptRoot)
        {
            sourcePaths.Add(transcriptRoot);
        }

        using var dataset = MonitorDataset.Create(
            options.DataDirectory,
            sourcePaths,
            "watch",
            captureContent: options.CaptureContent);
        var useDashboard = !options.NoDashboard && !Console.IsOutputRedirected;
        var metadataScanner = new SessionMetadataScanner(sources.SessionRoots);
        using var transcriptWatcher = new TranscriptActivityWatcher(
            sources.TranscriptRoot,
            readAppendedLines: true);
        var tailer = new IncrementalFileTailer(sources.MainLog, startAtEnd: true);
        Directory.CreateDirectory(options.DataDirectory);
        var markerPath = Path.Combine(options.DataDirectory, "markers.jsonl");
        using (new FileStream(markerPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
        }

        var markerTailer = new IncrementalFileTailer(markerPath, startAtEnd: true);
        var start = DateTimeOffset.UtcNow;
        var nextMetadata = DateTimeOffset.MinValue;
        var nextProcesses = DateTimeOffset.MinValue;

        if (options.CaptureContent)
        {
            Console.Error.WriteLine(
                "CONTENT CAPTURE ENABLED: new user prompts and visible assistant text will be stored locally.");
            Console.Error.WriteLine("Thinking, tool inputs/results, attachments, cwd, and raw Desktop logs remain excluded.");
        }

        void Emit(MonitorEvent monitorEvent)
        {
            dataset.Write(monitorEvent);
            dashboard.Accept(monitorEvent);
            if (!useDashboard && !options.Quiet)
            {
                Console.WriteLine(
                    $"{monitorEvent.ObservedAtUtc:O} {monitorEvent.Event} {monitorEvent.SessionKey ?? "-"}");
            }
        }

        Emit(new MonitorEvent
        {
            Source = "cccg-monitor",
            Event = "monitor.started",
            Data = new Dictionary<string, object?>
            {
                ["mainLog"] = MonitorPathRedactor.Redact(sources.MainLog),
                ["sessionRootCount"] = sources.SessionRoots.Count,
                ["transcriptWatcher"] = transcriptWatcher.IsActive,
                ["contentCapture"] = options.CaptureContent,
                ["historyLines"] = options.HistoryLines
            }
        });

        if (options.HistoryLines > 0)
        {
            foreach (var line in SharedFileReader.ReadLines(sources.MainLog).TakeLast(options.HistoryLines))
            {
                TryEmitLog(line, historical: true, Emit);
            }
        }

        if (options.ReadyFile is { Length: > 0 } readyFile)
        {
            if (string.IsNullOrWhiteSpace(options.Generation))
            {
                throw new InvalidDataException("--generation is required with --ready-file.");
            }

            MonitorWorkerControl.WriteReady(readyFile, new MonitorWorkerReady
            {
                Generation = options.Generation,
                WorkerPid = Environment.ProcessId,
                RunDirectory = dataset.RunDirectory,
                ContentCapture = options.CaptureContent,
                MonitorVersion = typeof(LiveMonitor).Assembly.GetName().Version?.ToString()
            });
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (MonitorWorkerControl.IsStopRequested(options.StopFile))
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            if (options.Duration is { } duration && now - start >= duration)
            {
                break;
            }

            foreach (var line in tailer.ReadNewLines())
            {
                TryEmitLog(line, historical: false, Emit);
            }

            foreach (var line in markerTailer.ReadNewLines())
            {
                TryEmitMarker(line, Emit);
            }

            if (now >= nextMetadata)
            {
                foreach (var change in metadataScanner.Scan())
                {
                    var snapshot = change.Snapshot;
                    var sessionKey = pseudonymizer.Session(snapshot.RawSessionId);
                    if (!string.IsNullOrWhiteSpace(snapshot.RawCliSessionId))
                    {
                        cliToSession[snapshot.RawCliSessionId] = sessionKey;
                    }

                    Emit(new MonitorEvent
                    {
                        Source = "desktop-session-metadata",
                        Event = change.IsInitial ? "session.metadata_snapshot" : "session.metadata_changed",
                        SessionKey = sessionKey,
                        Historical = change.IsInitial,
                        Data = new Dictionary<string, object?>
                        {
                            ["lastActivityAtUnixMs"] = snapshot.LastActivityAt,
                            ["completedTurns"] = snapshot.CompletedTurns,
                            ["model"] = snapshot.Model,
                            ["effort"] = snapshot.Effort,
                            ["permissionMode"] = snapshot.PermissionMode,
                            ["bridgeSessionKeys"] = snapshot.RawBridgeSessionIds.Select(pseudonymizer.Session).ToArray(),
                            ["fileLength"] = snapshot.FileLength
                        }
                    });
                }

                nextMetadata = now.AddSeconds(1);
            }

            foreach (var activity in transcriptWatcher.Drain())
            {
                var rawCliId = Path.GetFileNameWithoutExtension(activity.Path);
                cliToSession.TryGetValue(rawCliId, out var sessionKey);
                var cliSessionKey = pseudonymizer.CliSession(rawCliId);
                if (sessionKey is not null)
                {
                    Emit(new MonitorEvent
                    {
                        Source = "cli-transcript-metadata",
                        Event = "session.transcript_activity",
                        SessionKey = sessionKey,
                        Data = new Dictionary<string, object?>
                        {
                            ["fileLength"] = activity.Length,
                            ["deltaBytes"] = activity.Delta
                        }
                    });
                }

                if (!options.CaptureContent)
                {
                    foreach (var line in activity.AppendedLines)
                    {
                        foreach (var operation in operationParser.Parse(line))
                        {
                            EmitOperation(operation, sessionKey, cliSessionKey, Emit);
                        }
                    }

                    continue;
                }

                foreach (var line in activity.AppendedLines)
                {
                    foreach (var operation in operationParser.Parse(line))
                    {
                        EmitOperation(operation, sessionKey, cliSessionKey, Emit);
                    }

                    foreach (var parsed in contentParser.Parse(line))
                    {
                        var record = ToContentRecord(parsed, sessionKey, cliSessionKey);
                        dataset.WriteContent(record);
                        Emit(new MonitorEvent
                        {
                            Source = "cli-transcript-content",
                            SourceTime = parsed.SourceTime,
                            Event = parsed.Kind == "user_prompt"
                                ? "session.prompt_captured"
                                : "session.response_captured",
                            SessionKey = sessionKey,
                            Data = new Dictionary<string, object?>
                            {
                                ["cliSessionKey"] = cliSessionKey,
                                ["entryKey"] = record.EntryKey,
                                ["blockIndex"] = parsed.BlockIndex,
                                ["characterCount"] = parsed.Text.Length,
                                ["isSidechain"] = parsed.IsSidechain
                            }
                        });
                    }
                }
            }

            if (now >= nextProcesses)
            {
                UpdateProcesses(ClaudeProcessProbe.Snapshot(), Emit);
                nextProcesses = now.AddSeconds(1);
            }

            if (useDashboard)
            {
                dashboard.Render(
                    dataset.RunDirectory,
                    dataset.EventCount,
                    options.CaptureContent,
                    dataset.ContentCount);
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Emit(new MonitorEvent
        {
            Source = "cccg-monitor",
            Event = "monitor.stopped",
            Data = new Dictionary<string, object?> { ["eventCountBeforeStop"] = dataset.EventCount }
        });
        if (useDashboard)
        {
            dashboard.Render(
                dataset.RunDirectory,
                dataset.EventCount,
                options.CaptureContent,
                dataset.ContentCount,
                force: true);
        }

        Console.Error.WriteLine($"Dataset: {dataset.RunDirectory}");
        return 0;
    }

    private MonitorContentRecord ToContentRecord(
        ParsedTranscriptContent parsed,
        string? sessionKey,
        string cliSessionKey) =>
        new()
        {
            SourceTime = parsed.SourceTime,
            Kind = parsed.Kind,
            SessionKey = sessionKey,
            CliSessionKey = cliSessionKey,
            EntryKey = CorrelationOrNull(parsed.RawEntryId),
            LogicalRecordKey = string.IsNullOrWhiteSpace(parsed.RawEntryId)
                ? null
                : pseudonymizer.Correlation(
                    $"{parsed.RawEntryId}\n{parsed.Kind}\n{parsed.BlockIndex}"),
            ParentEntryKey = CorrelationOrNull(parsed.RawParentEntryId),
            PromptKey = CorrelationOrNull(parsed.RawPromptId),
            RequestKey = CorrelationOrNull(parsed.RawRequestId),
            ProviderMessageKey = CorrelationOrNull(parsed.RawProviderMessageId),
            BlockIndex = parsed.BlockIndex,
            Model = parsed.Model,
            IsSidechain = parsed.IsSidechain,
            Text = parsed.Text
        };

    private string? CorrelationOrNull(string? rawId) =>
        string.IsNullOrWhiteSpace(rawId) ? null : pseudonymizer.Correlation(rawId);

    private void EmitOperation(
        ParsedTranscriptOperation operation,
        string? sessionKey,
        string cliSessionKey,
        Action<MonitorEvent> emit)
    {
        var operationKey = CorrelationOrNull(operation.RawOperationId);
        var toolName = operation.ToolName;
        var toolKind = operation.ToolKind;
        if (operation.Event == "session.tool_requested"
            && operationKey is not null
            && toolName is not null
            && toolKind is not null)
        {
            toolsByOperationKey[operationKey] = (toolName, toolKind);
        }
        else if (operation.Event == "session.tool_completed"
                 && operationKey is not null
                 && toolsByOperationKey.TryGetValue(operationKey, out var remembered))
        {
            toolName = remembered.Name;
            toolKind = remembered.Kind;
            toolsByOperationKey.Remove(operationKey);
        }

        emit(new MonitorEvent
        {
            Source = "cli-transcript-operation",
            SourceTime = operation.SourceTime,
            Event = operation.Event,
            SessionKey = sessionKey,
            Data = new Dictionary<string, object?>
            {
                ["cliSessionKey"] = cliSessionKey,
                ["operationKey"] = operationKey,
                ["toolName"] = toolName,
                ["toolKind"] = toolKind,
                ["inputKeys"] = operation.InputKeys,
                ["payloadCharacters"] = operation.PayloadCharacters,
                ["resultBlockCount"] = operation.ResultBlockCount,
                ["isError"] = operation.IsError,
                ["hookCount"] = operation.HookCount,
                ["hookInfoCount"] = operation.HookInfoCount,
                ["hookErrorCount"] = operation.HookErrorCount,
                ["preventedContinuation"] = operation.PreventedContinuation,
                ["hasOutput"] = operation.HasOutput,
                ["stopReason"] = operation.StopReason,
                ["isSidechain"] = operation.IsSidechain
            }
        });
    }

    private void TryEmitLog(string line, bool historical, Action<MonitorEvent> emit)
    {
        var now = DateTimeOffset.UtcNow;
        if (!deduplicator.ShouldEmit(line, now) || !parser.TryParse(line, out var signal) || signal is null)
        {
            return;
        }

        emit(new MonitorEvent
        {
            ObservedAtUtc = now,
            SourceTime = signal.SourceTime,
            Source = "desktop-main-log",
            Event = signal.Event,
            Level = signal.Level,
            SessionKey = signal.RawSessionId is null ? null : pseudonymizer.Session(signal.RawSessionId),
            Historical = historical,
            Data = signal.Data
        });
    }

    private static void TryEmitMarker(string line, Action<MonitorEvent> emit)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("label", out var labelElement)
                || labelElement.GetString() is not { Length: > 0 } label)
            {
                return;
            }

            var sourceTime = root.TryGetProperty("createdAtUtc", out var timeElement)
                && timeElement.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;
            emit(new MonitorEvent
            {
                Source = "user-test-marker",
                SourceTime = sourceTime,
                Event = "test.marker",
                Data = new Dictionary<string, object?> { ["label"] = label }
            });
        }
        catch (JsonException)
        {
        }
    }

    private void UpdateProcesses(
        IReadOnlyList<ClaudeProcessSnapshot> snapshots,
        Action<MonitorEvent> emit)
    {
        var next = snapshots.ToDictionary(snapshot => snapshot.ProcessId);
        foreach (var snapshot in snapshots.Where(snapshot => !processes.ContainsKey(snapshot.ProcessId)))
        {
            emit(ProcessEvent("process.started", snapshot));
        }

        foreach (var snapshot in processes.Values.Where(snapshot => !next.ContainsKey(snapshot.ProcessId)))
        {
            emit(ProcessEvent("process.exited", snapshot));
        }

        processes.Clear();
        foreach (var pair in next)
        {
            processes[pair.Key] = pair.Value;
        }

        var counts = snapshots
            .GroupBy(snapshot => snapshot.Role)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (counts.Count == lastProcessCounts.Count
            && counts.All(pair => lastProcessCounts.GetValueOrDefault(pair.Key) == pair.Value))
        {
            return;
        }

        lastProcessCounts.Clear();
        foreach (var pair in counts)
        {
            lastProcessCounts[pair.Key] = pair.Value;
        }

        emit(new MonitorEvent
        {
            Source = "windows-process-probe",
            Event = "process.snapshot",
            Data = new Dictionary<string, object?> { ["counts"] = counts }
        });
    }

    private MonitorEvent ProcessEvent(string eventName, ClaudeProcessSnapshot snapshot) =>
        new()
        {
            Source = "windows-process-probe",
            Event = eventName,
            Data = new Dictionary<string, object?>
            {
                ["pid"] = snapshot.ProcessId,
                ["parentPid"] = snapshot.ParentProcessId,
                ["name"] = snapshot.Name,
                ["role"] = snapshot.Role,
                ["path"] = snapshot.Path is null ? null : MonitorPathRedactor.Redact(snapshot.Path),
                ["pathKey"] = snapshot.Path is null ? null : pseudonymizer.ProcessPath(snapshot.Path),
                ["startedAt"] = snapshot.StartedAt
            }
        };
}
