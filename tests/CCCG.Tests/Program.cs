using CCCG.Core;
using CCCG.Core.Dispatch;
using CCCG.Core.Monitoring;
using CCCG.Core.Providers;
using CCCG.Monitor;
using CCCG.Host;

if (args is ["--emit-utf8-grok-json"])
{
    const string payload = "{\"text\":\"繁體中文測試：你好，世界！\",\"thought\":\"clean English thought\"}";
    var stdoutBytes = System.Text.Encoding.UTF8.GetBytes(payload + Environment.NewLine);
    var stderrBytes = System.Text.Encoding.UTF8.GetBytes("診斷訊息：正常" + Environment.NewLine);
    Console.OpenStandardOutput().Write(stdoutBytes);
    Console.OpenStandardError().Write(stderrBytes);
    return 0;
}

if (args is ["--echo-utf8-stdin"])
{
    using var input = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    Console.OpenStandardOutput().Write(buffer.ToArray());
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("arguments parse split and inline values", ArgumentsParse),
    ("protocol parses user text", ProtocolParsesUser),
    ("protocol accepts UTF-8 BOM on first frame", ProtocolAcceptsBom),
    ("protocol parses set_model", ProtocolParsesSetModel),
    ("configuration resolves exact aliases", ConfigurationResolves),
    ("Luna experiment maps Haiku without renaming backend", LunaConfigurationResolves),
    ("configuration resolves relative passthrough beside config", ConfigurationResolvesRelativePassthrough),
    ("configuration rejects unsupported provider", ConfigurationRejectsProvider),
    ("process passthrough preserves stdin stdout stderr and exit code", ProcessPassthroughRoundTrips),
    ("codex adapter uses ordered read-only Sol lifecycle", CodexAdapterUsesOrderedSolLifecycle),
    ("codex adapter discloses backend reroutes", CodexAdapterDisclosesReroute),
    ("monitor parses healthy lifecycle", MonitorParsesHealthy),
    ("monitor parses startup timings", MonitorParsesTimings),
    ("monitor deduplicates duplicate log lines", MonitorDeduplicates),
    ("monitor metadata excludes content fields", MonitorMetadataIsMinimal),
    ("monitor content captures only visible user and assistant text", MonitorContentSelectsVisibleText),
    ("monitor operations correlate web search results and hooks", MonitorOperationsParse),
    ("monitor content dataset is explicit and separate", MonitorContentDatasetIsSeparate),
    ("monitor content watcher captures only appended transcript lines", MonitorContentWatcherCapturesAppend),
    ("monitor pseudonyms are stable", MonitorPseudonymsAreStable),
    ("monitor tailer reads appended lines", MonitorTailerReadsAppend),
    ("monitor worker control round-trips readiness and stop", MonitorWorkerControlRoundTrips),
    ("host stages immutable hash-addressed workers", HostStagesImmutableWorker),
    ("host parses supervised content-capture options", HostParsesOptions),
    ("host reports update only after retiring workers clear", HostRequiresStableActivation),
    ("grok directory treats every live writer as unavailable", GrokDirectoryRanksPeers),
    ("grok directory filters by cwd and inspects one peer", GrokDirectoryFiltersAndInspects),
    ("grok directory ignores dead active pids", GrokDirectoryIgnoresDeadPids),
    ("grok directory is empty when home is missing", GrokDirectoryEmptyHome),
    ("codex directory treats every held writer lock as unavailable", CodexDirectoryRanksParents),
    ("codex directory filters by cwd and inspects one peer", CodexDirectoryFiltersAndInspects),
    ("codex directory fills parent cwd from subagent rollouts", CodexDirectoryFillsParentFromSubagentOnly),
    ("claude directory lists transcripts and marks live Desktop writers", ClaudeDirectoryListsAndMarksLive),
    ("claude directory ignores dead session registry entries", ClaudeDirectoryIgnoresDeadRegistry),
    ("watch peers diffs status change", WatchPeersDiffsStatusChange),
    ("watch peers reports missing id", WatchPeersReportsMissingId),
    ("watch peers rejects empty list", WatchPeersRejectsEmptyList),
    ("peer selector skips unowned live peers and delivers to owned ones", PeerSelectorPrefersIdle),
    ("peer selector rejects a working session", PeerSelectorRejectsWorking),
    ("peer selector delivers to an explicit owned live peer and fails closed otherwise", PeerSelectorInsertsExplicitLivePeer),
    ("dispatch runner refuses keystroke injection for an unowned live window", DispatchRunnerRefusesUnownedLiveWindow),
    ("dispatch runner deliver does not wait for a held workspace lease", DispatchRunnerDeliverSkipsWorkspaceLease),
    ("live injector builds unicode key events and a trailing enter", LiveInjectorBuildsEnterRecords),
    ("live injector binds WriteConsoleInputW for unicode", LiveInjectorBindsWriteConsoleInputW),
    ("dispatch runner delivers through the owner daemon to a live-idle peer", DispatchRunnerDeliversToLiveIdlePeer),
    ("owner registry registers and detects a stale owner", OwnerRegistryDetectsStaleOwner),
    ("owner registry cleans stale registrations but keeps non-empty spools", OwnerRegistryCleansStaleRegistrations),
    ("owner daemon turns spooled messages into receipts", OwnerDaemonProcessesSpool),
    ("owner message round-trips model and effort", OwnerMessageRoundTripsModelAndEffort),
    ("owner daemon forwards per-message overrides", OwnerDaemonForwardsPerMessageOverrides),
    ("Codex owner transport falls back to constructor defaults", CodexOwnerTurnTransportFallsBackToConstructorDefaults),
    ("owner daemon writes a failed receipt when the provider turn fails", OwnerDaemonRecordsFailedTurn),
    ("owner spool preserves Chinese text byte-exactly through the turn roundtrip", OwnerSpoolPreservesChineseTextBytes),
    ("owner daemon fails abandoned processing turns as unknown outcome without re-running", OwnerDaemonFailsAbandonedProcessingTurns),
    ("second owner daemon in the same workspace fails fast", SecondOwnerInSameWorkspaceFailsFast),
    ("owner daemon exits after repeated transport failures so the lease releases", OwnerDaemonExitsAfterRepeatedTransportFailures),
    ("dispatch deliver fails when the owner dies mid-delivery", DispatchDeliverFailsWhenOwnerDies),
    ("dispatch deliver succeeds and posts a placeholder for an empty provider reply", DispatchDeliverEmptyReplySucceedsWithPlaceholder),
    ("grok resume read-back confirms the recorded turn", GrokResumeReadBackPasses),
    ("grok resume read-back fails when no turn is recorded", GrokResumeReadBackFails),
    ("provider command resumes grok codex and claude", ProviderCommandResumesPeers),
    ("provider command adds Codex model and effort", ProviderCommandAddsCodexModelAndEffort),
    ("provider command omits Codex model flags when unset", ProviderCommandOmitsCodexModelFlagsWhenUnset),
    ("provider command adds Grok model and effort", ProviderCommandAddsGrokModelAndEffort),
    ("provider command omits Grok model flags when unset", ProviderCommandOmitsGrokModelFlagsWhenUnset),
    ("dispatch job store round-trips model and effort", DispatchJobStoreRoundTripsModelAndEffort),
    ("dispatch job store ignores missing model fields", DispatchJobStoreIgnoresMissingModelFields),
    ("provider command preassigns a new Grok session id", ProviderCommandPreassignsGrokId),
    ("provider output parses Claude session and normalized answer", ProviderOutputParsesClaude),
    ("provider output strips Grok thought and usage metadata", ProviderOutputNormalizesGrok),
    ("file process launcher preserves UTF-8 provider output", FileProcessLauncherPreservesUtf8),
    ("file process launcher writes UTF-8 stdin bytes unchanged", FileProcessLauncherPreservesUtf8Stdin),
    ("dispatch runner enqueue copies model and effort", DispatchRunnerEnqueueCopiesModelAndEffort),
    ("dispatch runner passes overrides to Codex launch", DispatchRunnerPassesOverridesToCodexLaunch),
    ("dispatch runner rejects Claude overrides", DispatchRunnerRejectsClaudeOverrides),
    ("collect surfaces model without breaking shape", CollectSurfacesModelWithoutBreakingShape),
    ("dispatch runner creates and binds a Claude session", DispatchRunnerBindsNewClaudeSession),
    ("dispatch runner routes live Claude through Desktop session management", DispatchRunnerRoutesLiveClaude),
    ("provider output captures and binds a new Codex thread id", DispatchRunnerBindsNewCodexThread),
    ("dispatch runner starts a resumable job and collects success", DispatchRunnerCollectsSuccess),
    ("dispatch runners serialize the same managed peer across processes", DispatchRunnersSerializeManagedPeer),
    ("dispatch status fails a job whose worker exited", DispatchStatusFailsDeadWorker),
    ("binding store marks and prefers the bound peer", BindingStorePrefersBoundPeer),
    ("inbox ledger posts lists and acks for Claude", InboxLedgerRoundTrips),
    ("inbox ledger preserves concurrent posts", InboxLedgerPreservesConcurrentPosts),
    ("recursion context defaults and computes first job hop", RecursionContextDefaultsAndJobHop),
    ("recursion context allows nested hops", RecursionContextAllowsNestedHops),
    ("recursion context rejects exceeded hop", RecursionContextRejectsExceededHop),
    ("recursion context rejects malformed environment", RecursionContextRejectsMalformedEnvironment),
    ("dispatch job store round-trips hop metadata", DispatchJobStoreRoundTripsHopMetadata),
    ("dispatch runner injects hop environment on resume and create", DispatchRunnerInjectsHopEnvironment),
    ("owner provider child receives hop environment", OwnerProviderChildReceivesHopEnvironment),
    ("host worker preserves inherited hop environment", HostWorkerPreservesInheritedHopEnvironment),
    ("quota counts by source provider and date", QuotaCountsBySourceProviderAndDate),
    ("quota uses Claude and default overrides", QuotaUsesClaudeAndDefaultOverrides),
    ("quota rejects at limit with tomorrow reset", QuotaRejectsAtLimitWithTomorrowReset),
    ("quota resets across local date", QuotaResetsAcrossLocalDate),
    ("quota reservation rolls back", QuotaReservationRollsBack),
    ("owner message round-trips hop metadata", OwnerMessageRoundTripsHopMetadata),
    ("recursive enqueue posts a system audit", RecursiveEnqueuePostsSystemAudit),
    ("human root enqueue has no recursive audit", HumanRootEnqueueHasNoRecursiveAudit),
    ("audit failure fails closed", AuditFailureFailsClosed),
    ("owner daemon rebuilds transport when hop changes", OwnerDaemonRebuildsTransportWhenHopChanges),
    ("Claude child mode defaults to text-only", ClaudeChildModeDefaultsToTextOnly),
    ("Claude child tools mode allows web tools without MCP", ClaudeChildToolsModeAllowsWebWithoutMcp)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void ArgumentsParse()
{
    var parsed = ClaudeArguments.Parse(new[]
    {
        "--model=claude-haiku-4-5", "--permission-mode", "default",
        "--cccg-config", "routes.json", "--verbose"
    });

    Equal("claude-haiku-4-5", parsed.Model);
    Equal("default", parsed.PermissionMode);
    Equal("routes.json", parsed.ConfigPath);
    True(!parsed.ForwardedArguments.Contains("--cccg-config"));
    True(parsed.ForwardedArguments.Contains("--verbose"));
}

static void ProtocolParsesUser()
{
    var frame = ClaudeProtocol.ParseInput(
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}");
    Equal(ClaudeFrameKind.User, frame.Kind);
    Equal("hello", frame.Text);
}

static void ProtocolParsesSetModel()
{
    var frame = ClaudeProtocol.ParseInput(
        "{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"set_model\",\"model\":\"claude-sonnet-4-6\"}}");
    Equal(ClaudeFrameKind.SetModel, frame.Kind);
    Equal("claude-sonnet-4-6", frame.Model);
    Equal("r1", frame.RequestId);
}

static void ProtocolAcceptsBom()
{
    var frame = ClaudeProtocol.ParseInput(
        "\uFEFF{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}");
    Equal(ClaudeFrameKind.User, frame.Kind);
    Equal("hello", frame.Text);
}

static void ConfigurationResolves()
{
    var path = TempJson("""
        {"unmappedBehavior":"reject","routes":{"claude-haiku-4-5":{"provider":"mock","model":"gpt-5.6-sol"}}}
        """);
    try
    {
        var route = RouterConfiguration.Load(path).Resolve("claude-haiku-4-5");
        Equal("mock", route.NormalizedProvider);
        Equal("gpt-5.6-sol", route.Model);
    }
    finally
    {
        File.Delete(path);
    }
}

static void ConfigurationRejectsProvider()
{
    var path = TempJson("""
        {"unmappedBehavior":"reject","routes":{"x":{"provider":"unknown","model":"m"}}}
        """);
    try
    {
        Throws<InvalidDataException>(() => RouterConfiguration.Load(path));
    }
    finally
    {
        File.Delete(path);
    }
}

static void LunaConfigurationResolves()
{
    var path = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "config", "routes.luna.json"));
    var route = RouterConfiguration.Load(path).Resolve("claude-haiku-4-5");
    var datedRoute = RouterConfiguration.Load(path).Resolve(
        "claude-haiku-4-5-20251001");

    Equal("codex-app-server", route.NormalizedProvider);
    Equal("gpt-5.6-luna", route.Model);
    Equal("medium", route.ReasoningEffort);
    Equal("gpt-5.6-luna", datedRoute.Model);
}

static void ConfigurationResolvesRelativePassthrough()
{
    var directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "cccg-config-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = System.IO.Path.Combine(directory, "routes.json");
    File.WriteAllText(path, """
        {"unmappedBehavior":"passthrough","originalClaudePath":"native\\claude.exe","routes":{}}
        """);

    try
    {
        var configuration = RouterConfiguration.Load(path);
        True(configuration.UsesPassthrough);
        Equal(
            System.IO.Path.GetFullPath(
                System.IO.Path.Combine(directory, "native", "claude.exe")),
            configuration.OriginalClaudePath);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ProcessPassthroughRoundTrips()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var executable = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
    var inputBytes = System.Text.Encoding.UTF8.GetBytes("PING\n");
    using var input = new MemoryStream(inputBytes);
    using var output = new MemoryStream();
    using var error = new MemoryStream();

    var exitCode = ProcessPassthrough.RunAsync(
        executable,
        new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "$line=[Console]::In.ReadLine();"
            + "[Console]::Out.Write('OUT:'+$line);"
            + "[Console]::Error.Write('ERR:'+$line);"
            + "exit 7"
        },
        input,
        output,
        error).GetAwaiter().GetResult();

    Equal(7, exitCode);
    Equal("OUT:PING", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    Equal("ERR:PING", System.Text.Encoding.UTF8.GetString(error.ToArray()));
}

static void CodexAdapterUsesOrderedSolLifecycle()
{
    var transport = new ScriptedJsonLineTransport(new[]
    {
        "{\"id\":0,\"result\":{\"userAgent\":\"codex-test\",\"platformFamily\":\"windows\",\"platformOs\":\"windows\"}}",
        "{\"id\":1,\"result\":{\"thread\":{\"id\":\"thr_test\"},\"model\":\"gpt-5.6-sol\",\"modelProvider\":\"openai\",\"approvalPolicy\":\"never\",\"sandbox\":{\"type\":\"readOnly\"},\"cwd\":\"D:\\\\code\\\\CCCG\",\"approvalsReviewer\":\"user\"}}",
        "{\"id\":2,\"result\":{\"turn\":{\"id\":\"turn_test\",\"status\":\"inProgress\",\"items\":[],\"error\":null}}}",
        "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thr_test\",\"turnId\":\"turn_test\",\"completedAtMs\":1,\"item\":{\"id\":\"item_test\",\"type\":\"agentMessage\",\"text\":\"CCCG-CODEX-OK\"}}}",
        "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thr_test\",\"turn\":{\"id\":\"turn_test\",\"status\":\"completed\",\"items\":[],\"error\":null}}}"
    });
    var client = new CodexAppServerClient(_ => Task.FromResult<IJsonLineTransport>(transport));

    var result = client.CompleteAsync(
        "Reply only CCCG-CODEX-OK",
        "gpt-5.6-sol",
        "medium",
        @"D:\code\CCCG").GetAwaiter().GetResult();

    Equal("CCCG-CODEX-OK", result.Text);
    Equal("gpt-5.6-sol", result.RequestedModel);
    Equal("gpt-5.6-sol", result.ActualModel);
    True(!result.WasRerouted);
    Equal(4, transport.Writes.Count);

    using var initialize = System.Text.Json.JsonDocument.Parse(transport.Writes[0]);
    Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
    Equal("cccg_router", initialize.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

    using var initialized = System.Text.Json.JsonDocument.Parse(transport.Writes[1]);
    Equal("initialized", initialized.RootElement.GetProperty("method").GetString());

    using var threadStart = System.Text.Json.JsonDocument.Parse(transport.Writes[2]);
    var threadParams = threadStart.RootElement.GetProperty("params");
    Equal("thread/start", threadStart.RootElement.GetProperty("method").GetString());
    Equal("gpt-5.6-sol", threadParams.GetProperty("model").GetString());
    Equal("never", threadParams.GetProperty("approvalPolicy").GetString());
    Equal("read-only", threadParams.GetProperty("sandbox").GetString());
    Equal(true, threadParams.GetProperty("ephemeral").GetBoolean());

    using var turnStart = System.Text.Json.JsonDocument.Parse(transport.Writes[3]);
    Equal("turn/start", turnStart.RootElement.GetProperty("method").GetString());
    Equal("medium", turnStart.RootElement.GetProperty("params").GetProperty("effort").GetString());
}

static void CodexAdapterDisclosesReroute()
{
    var transport = new ScriptedJsonLineTransport(new[]
    {
        "{\"id\":0,\"result\":{}}",
        "{\"id\":1,\"result\":{\"thread\":{\"id\":\"thr_test\"},\"model\":\"gpt-5.6-sol\"}}",
        "{\"id\":2,\"result\":{\"turn\":{\"id\":\"turn_test\",\"status\":\"inProgress\"}}}",
        "{\"method\":\"model/rerouted\",\"params\":{\"threadId\":\"thr_test\",\"turnId\":\"turn_test\",\"fromModel\":\"gpt-5.6-sol\",\"toModel\":\"gpt-5.6-luna\",\"reason\":\"availability\"}}",
        "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thr_test\",\"turnId\":\"turn_test\",\"completedAtMs\":1,\"item\":{\"id\":\"item_test\",\"type\":\"agentMessage\",\"text\":\"fallback\"}}}",
        "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thr_test\",\"turn\":{\"id\":\"turn_test\",\"status\":\"completed\",\"items\":[],\"error\":null}}}"
    });
    var client = new CodexAppServerClient(_ => Task.FromResult<IJsonLineTransport>(transport));

    var result = client.CompleteAsync("test", "gpt-5.6-sol", "medium", @"D:\code\CCCG")
        .GetAwaiter().GetResult();

    Equal("gpt-5.6-luna", result.ActualModel);
    True(result.WasRerouted);
    True(ProviderDisclosure.Format(
            "codex-app-server",
            result.ActualModel,
            result.Text,
            result.RequestedModel)
        .StartsWith("[CCCG provider=codex-app-server model=gpt-5.6-luna rerouted-from=gpt-5.6-sol]", StringComparison.Ordinal));
}

static void MonitorParsesHealthy()
{
    var line = "2026-08-12 22:23:35 [info] [CCD CycleHealth] healthy cycle for local_7a70cec7-18e5-4154-b1bd-73c3ed6b5876 (43s, hadFirstResponse=true)";
    True(new ClaudeLogParser().TryParse(line, out var signal));
    Equal("session.cycle_healthy", signal!.Event);
    Equal("local_7a70cec7-18e5-4154-b1bd-73c3ed6b5876", signal.RawSessionId);
    Equal(43, signal.Data["durationSeconds"]);
    Equal(true, signal.Data["hadFirstResponse"]);
}

static void MonitorParsesTimings()
{
    var line = "2026-08-12 21:31:28 [info] [CCD start-timing] local_7a70cec7-18e5-4154-b1bd-73c3ed6b5876 preflight=8ms query=11ms enqueue=10ms init=9148ms first_assistant=6410ms total_to_assistant=15976ms";
    True(new ClaudeLogParser().TryParse(line, out var signal));
    Equal("session.start_timing", signal!.Event);
    Equal(6410, signal.Data["first_assistant"]);
    Equal(15976, signal.Data["total_to_assistant"]);
}

static void MonitorDeduplicates()
{
    var deduplicator = new EventDeduplicator();
    var now = DateTimeOffset.UtcNow;
    True(deduplicator.ShouldEmit("same", now));
    True(!deduplicator.ShouldEmit("same", now.AddMilliseconds(10)));
    True(deduplicator.ShouldEmit("same", now.AddSeconds(3)));
}

static void MonitorMetadataIsMinimal()
{
    var path = TempJson("""
        {
          "sessionId":"local_7a70cec7-18e5-4154-b1bd-73c3ed6b5876",
          "cliSessionId":"8ff1d57e-a21a-45ca-8168-2ca35a5b66a7",
          "lastActivityAt":123,
          "completedTurns":4,
          "model":"claude-haiku-4-5",
          "effort":"medium",
          "permissionMode":"default",
          "bridgeSessionIds":["bridge-secret"],
          "title":"must not be collected",
          "cwd":"must not be collected",
          "prompt":"must not be collected"
        }
        """);
    try
    {
        var snapshot = SessionMetadataReader.TryRead(path)!;
        Equal(4, snapshot.CompletedTurns);
        Equal("claude-haiku-4-5", snapshot.Model);
        Equal(1, snapshot.RawBridgeSessionIds.Count);
        True(!snapshot.ToString().Contains("must not be collected", StringComparison.Ordinal));
    }
    finally
    {
        File.Delete(path);
    }
}

static void MonitorContentSelectsVisibleText()
{
    var parser = new TranscriptContentParser();
    var user = parser.Parse("""
        {"type":"user","uuid":"u1","parentUuid":"p1","promptId":"prompt1","timestamp":"2026-08-12T23:00:00+08:00","message":{"role":"user","content":"hello"}}
        """);
    Equal(1, user.Count);
    Equal("user_prompt", user[0].Kind);
    Equal("hello", user[0].Text);

    var assistant = parser.Parse("""
        {"type":"assistant","uuid":"a1","requestId":"r1","message":{"id":"m1","role":"assistant","model":"claude-test","content":[{"type":"thinking","thinking":"private"},{"type":"text","text":"visible one"},{"type":"tool_use","name":"Read","input":{"secret":"no"}},{"type":"text","text":"visible two"}]}}
        """);
    Equal(2, assistant.Count);
    Equal("assistant_response", assistant[0].Kind);
    Equal("visible one", assistant[0].Text);
    Equal(1, assistant[0].BlockIndex);
    Equal("visible two", assistant[1].Text);
    Equal(3, assistant[1].BlockIndex);
    True(!assistant.Any(item => item.Text.Contains("private", StringComparison.Ordinal)));
    True(!assistant.Any(item => item.Text.Contains("secret", StringComparison.Ordinal)));

    var toolResult = parser.Parse("""
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"credential"}]}}
        """);
    Equal(0, toolResult.Count);
}

static void MonitorOperationsParse()
{
    var parser = new TranscriptOperationParser();
    var request = parser.Parse("""
        {"type":"assistant","timestamp":"2026-08-12T23:00:00+08:00","message":{"role":"assistant","content":[{"type":"tool_use","id":"tool-1","name":"WebSearch","input":{"query":"synthetic query"}}]}}
        """);
    Equal(1, request.Count);
    Equal("session.tool_requested", request[0].Event);
    Equal("tool-1", request[0].RawOperationId);
    Equal("WebSearch", request[0].ToolName);
    Equal("web_search", request[0].ToolKind);
    Equal("query", request[0].InputKeys.Single());
    True(request[0].PayloadCharacters > 0);

    var result = parser.Parse("""
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":[{"type":"text","text":"synthetic result"}],"is_error":false}]}}
        """);
    Equal(1, result.Count);
    Equal("session.tool_completed", result[0].Event);
    Equal("tool-1", result[0].RawOperationId);
    Equal(false, result[0].IsError);
    Equal(1, result[0].ResultBlockCount);

    var hook = parser.Parse("""
        {"type":"system","subtype":"stop_hook_summary","timestamp":"2026-08-12T23:00:01+08:00","hookCount":2,"hookInfos":[{}],"hookErrors":[{}],"preventedContinuation":true,"stopReason":"blocked","hasOutput":true}
        """);
    Equal(1, hook.Count);
    Equal("session.hook_summary", hook[0].Event);
    Equal(2, hook[0].HookCount);
    Equal(1, hook[0].HookInfoCount);
    Equal(1, hook[0].HookErrorCount);
    Equal(true, hook[0].PreventedContinuation);
    Equal(true, hook[0].HasOutput);
}

static void MonitorContentDatasetIsSeparate()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-content-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string runDirectory;
    try
    {
        using (var dataset = MonitorDataset.Create(directory, Array.Empty<string>(), "test", captureContent: true))
        {
            runDirectory = dataset.RunDirectory;
            dataset.WriteContent(new MonitorContentRecord
            {
                Kind = "user_prompt",
                CliSessionKey = "cs_test",
                Text = "local debug text"
            });
        }

        var manifest = File.ReadAllText(Path.Combine(runDirectory!, "manifest.json"));
        var content = File.ReadAllText(Path.Combine(runDirectory!, "content.jsonl"));
        var eventsPath = Path.Combine(runDirectory!, "events.jsonl");
        True(manifest.Contains("\"promptBodiesStored\": true", StringComparison.Ordinal));
        True(manifest.Contains("\"thinkingStored\": false", StringComparison.Ordinal));
        True(content.Contains("local debug text", StringComparison.Ordinal));
        Equal(string.Empty, File.ReadAllText(eventsPath));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void MonitorContentWatcherCapturesAppend()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-watcher-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "synthetic-session.jsonl");
    const string oldLine = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"old must stay out\"}}";
    const string newLine = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"new must be captured\"}}";
    File.WriteAllText(path, oldLine + Environment.NewLine);
    try
    {
        using var watcher = new TranscriptActivityWatcher(directory, readAppendedLines: true);
        File.AppendAllText(path, newLine + Environment.NewLine);

        var appended = new List<string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && appended.Count == 0)
        {
            Thread.Sleep(50);
            appended.AddRange(watcher.Drain().SelectMany(activity => activity.AppendedLines));
        }

        Equal(1, appended.Count);
        Equal(newLine, appended[0]);
        True(!appended.Any(line => line.Contains("old must stay out", StringComparison.Ordinal)));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void MonitorPseudonymsAreStable()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-pseudo-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var first = StablePseudonymizer.LoadOrCreate(directory).Session("raw-session-id");
        var second = StablePseudonymizer.LoadOrCreate(directory).Session("raw-session-id");
        Equal(first, second);
        True(!first.Contains("raw-session-id", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void MonitorTailerReadsAppend()
{
    var path = Path.Combine(Path.GetTempPath(), $"cccg-tail-{Guid.NewGuid():N}.log");
    File.WriteAllText(path, "old\n");
    try
    {
        var tailer = new IncrementalFileTailer(path, startAtEnd: true);
        File.AppendAllText(path, "new-one\nnew-two\n");
        var lines = tailer.ReadNewLines();
        Equal(2, lines.Count);
        Equal("new-one", lines[0]);
        Equal("new-two", lines[1]);
    }
    finally
    {
        File.Delete(path);
    }
}

static void MonitorWorkerControlRoundTrips()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-control-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var readyPath = Path.Combine(directory, "ready.json");
        var stopPath = Path.Combine(directory, "stop.signal");
        MonitorWorkerControl.WriteReady(readyPath, new MonitorWorkerReady
        {
            Generation = "gen-test",
            WorkerPid = 123,
            RunDirectory = directory,
            ContentCapture = true,
            MonitorVersion = "test"
        });
        var ready = MonitorWorkerControl.TryReadReady(readyPath)!;
        Equal("gen-test", ready.Generation);
        Equal(123, ready.WorkerPid);
        Equal(true, ready.ContentCapture);
        True(!MonitorWorkerControl.IsStopRequested(stopPath));
        MonitorWorkerControl.RequestStop(stopPath);
        True(MonitorWorkerControl.IsStopRequested(stopPath));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void HostStagesImmutableWorker()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-host-store-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var source = Path.Combine(directory, "candidate.exe");
    File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
    try
    {
        var store = new SupervisorStore(Path.Combine(directory, "state"));
        var first = store.StageAndRequest(source);
        var second = store.StageAndRequest(source);
        True(File.Exists(first.StagedPath));
        True(!string.Equals(source, first.StagedPath, StringComparison.OrdinalIgnoreCase));
        Equal(first.Sha256, second.Sha256);
        Equal(first.StagedPath, second.StagedPath);
        True(first.RequestId != second.RequestId);
        var desired = store.Read<DesiredWorker>(store.DesiredPath)!;
        Equal(second.RequestId, desired.RequestId);
        Equal(second.Sha256, desired.Sha256);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void HostParsesOptions()
{
    var state = Path.Combine(Path.GetTempPath(), "cccg-state-test");
    var data = Path.Combine(Path.GetTempPath(), "cccg-data-test");
    var parsed = HostOptions.Parse(new[]
    {
        "run", "--worker", "worker.exe", "--state-dir", state,
        "--data-dir", data, "--capture-content", "--history", "0",
        "--ready-timeout", "7", "--stop-timeout", "3", "--overlap-ms", "1500",
        "--no-dashboard"
    });
    Equal("run", parsed.Command);
    Equal("worker.exe", parsed.WorkerPath);
    Equal(true, parsed.CaptureContent);
    Equal(0, parsed.HistoryLines);
    Equal(TimeSpan.FromSeconds(7), parsed.ReadyTimeout);
    Equal(TimeSpan.FromSeconds(3), parsed.StopTimeout);
    Equal(TimeSpan.FromMilliseconds(1500), parsed.HandoffOverlap);
    Equal(true, parsed.NoDashboard);
}

static void HostRequiresStableActivation()
{
    var active = new ActiveWorker
    {
        RequestId = "request",
        Generation = "generation",
        Sha256 = "ABC",
        ConfigurationKey = "CONFIG",
        WorkerPath = "worker.exe",
        WorkerPid = 123,
        ReadyFile = "ready.json",
        StopFile = "stop.signal",
        RunDirectory = "run",
        ReadyAtUtc = DateTimeOffset.UtcNow,
        Retiring = new[]
        {
            new RetiringWorker
            {
                Generation = "old",
                WorkerPid = 122,
                WorkerPath = "old.exe",
                ReadyFile = "old-ready.json",
                StopFile = "old-stop.signal"
            }
        }
    };
    True(!SupervisorStore.IsStableActivation(active, "ABC"));
    True(SupervisorStore.IsStableActivation(
        active with { Retiring = Array.Empty<RetiringWorker>() },
        "abc"));
    True(!SupervisorStore.IsStableActivation(
        active with { Retiring = Array.Empty<RetiringWorker>() },
        "different"));
}

static string TempJson(string json)
{
    var path = Path.Combine(Path.GetTempPath(), $"cccg-test-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    return path;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static int IndexOf(IReadOnlyList<string> values, string value)
{
    for (var index = 0; index < values.Count; index++)
    {
        if (values[index] == value)
        {
            return index;
        }
    }

    return -1;
}

static void GrokDirectoryRanksPeers()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new GrokHomeFixture();
    home.WriteSummary("D:\\code\\app", "sess-idle", "Idle peer", "waiting", now.AddMinutes(-10));
    home.WriteSummary("D:\\code\\app", "sess-work", "Busy peer", "editing", now.AddSeconds(-20));
    home.WriteSummary("D:\\code\\app", "sess-sleep", "Old peer", "yesterday", now.AddDays(-1));
    home.WriteActive("sess-idle", 1001, "D:\\code\\app", now.AddHours(-1));
    home.WriteActive("sess-work", 1002, "D:\\code\\app", now.AddMinutes(-5));

    var listed = home.Open(pid => pid is 1001 or 1002, now).List();
    Equal(3, listed.TotalCount);
    Equal("sess-work", listed.Peers[0].SessionId);
    Equal(PeerStatus.LiveWorking, listed.Peers[0].Status);
    Equal("sess-idle", listed.Peers[1].SessionId);
    Equal(PeerStatus.LiveWorking, listed.Peers[1].Status);
    Equal("sess-sleep", listed.Peers[2].SessionId);
    Equal(PeerStatus.Resumable, listed.Peers[2].Status);
    True(listed.CanCreateNew);
}

static void GrokDirectoryFiltersAndInspects()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new GrokHomeFixture();
    home.WriteSummary("D:\\code\\app", "sess-app", "App peer", "in app", now);
    home.WriteSummary("D:\\code\\other", "sess-other", "Other peer", "elsewhere", now);

    var directory = home.Open(_ => false, now);
    var listed = directory.List("D:\\code\\app");
    Equal(1, listed.TotalCount);
    Equal("sess-app", listed.Peers[0].SessionId);
    Equal("D:\\code\\app", listed.Workspace);

    var inspected = directory.Inspect("sess-app");
    Equal("App peer", inspected?.Title);
    Equal("in app", inspected?.LastTurnSummary);
    True(directory.Inspect("missing") is null);
}

static void GrokDirectoryIgnoresDeadPids()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new GrokHomeFixture();
    home.WriteSummary("D:\\code\\app", "sess-dead", "Dead peer", "gone", now.AddHours(-2));
    home.WriteActive("sess-dead", 9, "D:\\code\\app", now);

    var listed = home.Open(_ => false, now).List();
    Equal(1, listed.TotalCount);
    Equal(PeerStatus.Resumable, listed.Peers[0].Status);
    True(listed.Peers[0].Pid is null);
}

static void GrokDirectoryEmptyHome()
{
    var listed = new GrokPeerDirectory(
        Path.Combine(Path.GetTempPath(), $"cccg-missing-grok-{Guid.NewGuid():N}"))
        .List();
    Equal(0, listed.TotalCount);
    True(listed.CanCreateNew);
}

static void CodexDirectoryRanksParents()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new CodexHomeFixture();
    home.WriteIndex("sess-idle", "Idle Codex");
    home.WriteIndex("sess-work", "Busy Codex");
    home.WriteIndex("sess-sleep", "Old Codex");
    home.WriteRollout("sess-idle", "D:\\code\\app", now.AddMinutes(-10), subagent: false);
    home.WriteRollout("sess-work", "D:\\code\\app", now.AddSeconds(-20), subagent: false);
    home.WriteRollout("sess-sleep", "D:\\code\\app", now.AddDays(-1), subagent: false);
    home.WriteRollout("sess-idle", "D:\\code\\app", now.AddMinutes(-9), subagent: true);
    var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        home.LockPath("sess-idle"),
        home.LockPath("sess-work")
    };

    var listed = home.Open(held.Contains, now).List();
    Equal(3, listed.TotalCount);
    Equal("sess-work", listed.Peers[0].SessionId);
    Equal(PeerStatus.LiveWorking, listed.Peers[0].Status);
    Equal("sess-idle", listed.Peers[1].SessionId);
    Equal(PeerStatus.LiveWorking, listed.Peers[1].Status);
    Equal("sess-sleep", listed.Peers[2].SessionId);
    Equal(PeerStatus.Resumable, listed.Peers[2].Status);
    Equal(3, listed.Peers.Count);
    Equal("D:\\code\\app", listed.Peers[0].Cwd);
}

static void CodexDirectoryFiltersAndInspects()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new CodexHomeFixture();
    home.WriteIndex("sess-app", "App Codex");
    home.WriteIndex("sess-other", "Other Codex");
    home.WriteRollout("sess-app", "D:\\code\\app", now, subagent: false);
    home.WriteRollout("sess-other", "D:\\code\\other", now, subagent: false);

    var directory = home.Open(_ => false, now);
    var listed = directory.List("D:\\code\\app");
    Equal(1, listed.TotalCount);
    Equal("sess-app", listed.Peers[0].SessionId);
    Equal("App Codex", listed.Peers[0].Title);
    True(listed.Peers[0].LastTurnSummary is null);

    var inspected = directory.Inspect("sess-app");
    Equal("D:\\code\\app", inspected?.Cwd);
    True(directory.Inspect("missing") is null);
}

static void CodexDirectoryFillsParentFromSubagentOnly()
{
    var now = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
    using var home = new CodexHomeFixture();
    home.WriteIndex("sess-live", "Live parent");
    home.WriteRollout("sess-live", "D:\\code\\openruterati", now.AddMinutes(-5), subagent: true);
    var listed = home.Open(path => path.Contains("sess-live", StringComparison.Ordinal), now).List();
    Equal(1, listed.TotalCount);
    Equal(PeerStatus.LiveWorking, listed.Peers[0].Status);
    Equal("D:\\code\\openruterati", listed.Peers[0].Cwd);
    Equal("openai", listed.Peers[0].Model);
}

static void ClaudeDirectoryListsAndMarksLive()
{
    using var home = new ClaudeHomeFixture();
    var liveId = "11111111-1111-4111-8111-111111111111";
    var oldId = "22222222-2222-4222-8222-222222222222";
    home.WriteTranscript(liveId, "D:\\code\\app", "Live Claude", "claude-sonnet-5");
    home.WriteTranscript(oldId, "D:\\code\\other", "Old Claude", "claude-opus-5");
    home.WriteActive(liveId, 777, "D:\\code\\app", "desktop-app");

    var directory = new ClaudePeerDirectory(home.Root, pid => pid == 777);
    var listed = directory.List("D:\\code\\app", 20);
    Equal(1, listed.TotalCount);
    Equal(liveId, listed.Peers[0].SessionId);
    Equal(PeerStatus.LiveWorking, listed.Peers[0].Status);
    Equal("Live Claude", listed.Peers[0].Title);
    Equal("claude-sonnet-5", listed.Peers[0].Model);
    Equal(777, listed.Peers[0].Pid);
    Equal(liveId, directory.Inspect(liveId)?.SessionId);
}

static void ClaudeDirectoryIgnoresDeadRegistry()
{
    using var home = new ClaudeHomeFixture();
    var id = "33333333-3333-4333-8333-333333333333";
    home.WriteTranscript(id, "D:\\code\\app", "Closed Claude", "claude-opus-5");
    home.WriteActive(id, 888, "D:\\code\\app", "stale");

    var peer = new ClaudePeerDirectory(home.Root, _ => false).List().Peers.Single();
    Equal(PeerStatus.Resumable, peer.Status);
    True(peer.Pid is null);
}

static void WatchPeersDiffsStatusChange()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-watch-{Guid.NewGuid():N}");
    try
    {
        var now = DateTimeOffset.Parse("2026-08-15T03:00:00Z");
        var peer = new Peer(
            "codex",
            "thread-1",
            PeerStatus.LiveWorking,
            "D:\\code\\app",
            "Worker",
            null,
            "gpt-5.6-luna",
            4242,
            null,
            now,
            4);
        var watcher = new PeerWatcher(
            (provider, sessionId) =>
                provider == "codex" && sessionId == peer.SessionId ? peer : null,
            root,
            () => now);

        var first = watcher.Watch("thread-1", "codex");
        Equal(1, first.Peers.Count);
        Equal(0, first.Changes.Count);
        True(File.Exists(Path.Combine(root, "last.json")));

        peer = peer with { Status = PeerStatus.LiveIdle };
        now = now.AddSeconds(5);
        var second = watcher.Watch("thread-1", "codex");
        Equal(1, second.Changes.Count);
        Equal("thread-1", second.Changes[0].SessionId);
        Equal("status", second.Changes[0].Field);
        Equal(PeerStatus.LiveWorking, second.Changes[0].From as string);
        Equal(PeerStatus.LiveIdle, second.Changes[0].To as string);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void WatchPeersReportsMissingId()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-watch-missing-{Guid.NewGuid():N}");
    try
    {
        var watcher = new PeerWatcher((_, _) => null, root);
        var result = watcher.Watch("missing", "codex");

        Equal(1, result.Peers.Count);
        Equal("missing", result.Peers[0].SessionId);
        Equal("codex", result.Peers[0].Provider);
        True(!result.Peers[0].Found);
        True(result.Peers[0].Status is null);
        True(result.Peers[0].Pid is null);
        Equal(0, result.Changes.Count);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void WatchPeersRejectsEmptyList()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-watch-empty-{Guid.NewGuid():N}");
    try
    {
        var watcher = new PeerWatcher((_, _) => null, root);
        Throws<ArgumentException>(() => watcher.Watch(" ,  , ", "all"));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void PeerSelectorPrefersIdle()
{
    var peers = new[]
    {
        new Peer("grok", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, 1, null, null, null),
        new Peer("grok", "idle", PeerStatus.LiveIdle, "D:\\code\\app", "Idle", null, null, 2, null, null, null),
        new Peer("grok", "old", PeerStatus.Resumable, "D:\\code\\app", "Old", null, null, null, null, null, null)
    };
    var picked = PeerSelector.Select("grok", peers, sessionId: null, cwd: "D:\\code\\app", allowNew: true);
    Equal("old", picked.SessionId);
    Equal(DispatchAction.Resume, picked.Action);

    var owned = PeerSelector.Select(
        "grok", peers, null, "D:\\code\\app", allowNew: true, boundSessionId: null, hasLiveOwner: _ => true);
    Equal("idle", owned.SessionId);
    Equal(DispatchAction.Deliver, owned.Action);

    var created = PeerSelector.Select("grok", Array.Empty<Peer>(), null, "D:\\code\\app", allowNew: true);
    Equal(DispatchAction.Create, created.Action);
}

static void PeerSelectorRejectsWorking()
{
    var peers = new[]
    {
        new Peer("claude", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, null, null, null, null)
    };
    Throws<InvalidOperationException>(() =>
        PeerSelector.Select("claude", peers, "busy", null, allowNew: false));
}

static void PeerSelectorInsertsExplicitLivePeer()
{
    var peers = new[]
    {
        new Peer("codex", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, 4242, null, null, null)
    };
    try
    {
        PeerSelector.Select("codex", peers, "busy", null, allowNew: false);
        throw new InvalidDataException("Expected the unowned live peer to fail closed.");
    }
    catch (InvalidOperationException exception)
    {
        True(exception.Message.Contains("not CCCG-owned", StringComparison.Ordinal));
        True(exception.Message.Contains("run-owner", StringComparison.Ordinal));
    }

    var picked = PeerSelector.Select(
        "codex", peers, "busy", null, allowNew: false, boundSessionId: null, hasLiveOwner: _ => true);
    Equal("busy", picked.SessionId);
    Equal(DispatchAction.Deliver, picked.Action);
    Equal(4242, picked.Pid);
    True(!picked.WaitForWriterFree);
    True(picked.Reason.Contains("CCCG-owned", StringComparison.Ordinal));
}

static void ProviderCommandResumesPeers()
{
    var grok = ProviderCommand.BuildGrok(
        DispatchAction.Resume, "sess-1", "D:\\code\\app", "D:\\tmp\\prompt.txt", grokHome: "D:\\tmp\\grok-home");
    True(grok.Arguments.Contains("-r"));
    True(grok.Arguments.Contains("sess-1"));
    True(grok.Arguments.Contains("--prompt-file"));

    var created = ProviderCommand.BuildGrok(
        DispatchAction.Create, null, "D:\\code\\app", "D:\\tmp\\prompt.txt", grokHome: "D:\\tmp\\grok-home");
    True(!created.Arguments.Contains("-r"));

    var codex = ProviderCommand.BuildCodex(
        DispatchAction.Attach, "thread-1", "D:\\code\\app", "D:\\tmp\\prompt.txt", "codex.cmd");
    Equal("codex.cmd", codex.FileName);
    True(codex.Arguments[0] == "exec");
    True(codex.Arguments.Contains("resume"));
    True(codex.Arguments.Contains("thread-1"));
    True(codex.Arguments.Contains("-"));
    True(codex.Arguments.Contains("--json"));

    var claude = ProviderCommand.BuildClaude(
        DispatchAction.Resume,
        "11111111-1111-4111-8111-111111111111",
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        "claude.exe");
    Equal("claude.exe", claude.FileName);
    True(claude.Arguments.Contains("--resume"));
    True(claude.Arguments.Contains("--safe-mode"));
    True(claude.Arguments.Contains("--disable-slash-commands"));
    True(claude.Arguments.Contains("--system-prompt"));
    True(claude.Arguments.Contains(ProviderCommand.ClaudeTextOnlySystemPrompt));
    True(claude.Arguments.Contains("--tools="));
    True(claude.Arguments.Contains("--strict-mcp-config"));
    True(claude.Arguments.Contains("--setting-sources="));
}

static void ProviderCommandAddsCodexModelAndEffort()
{
    var cmd = ProviderCommand.BuildCodex(
        DispatchAction.Resume,
        "thread-1",
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        "codex.cmd",
        model: "gpt-5.6-luna",
        reasoningEffort: "xhigh");
    Equal("codex.cmd", cmd.FileName);
    Equal("exec", cmd.Arguments[0]);
    Equal("--json", cmd.Arguments[1]);
    var modelAt = IndexOf(cmd.Arguments, "--model");
    True(modelAt > 0);
    Equal("gpt-5.6-luna", cmd.Arguments[modelAt + 1]);
    True(modelAt < IndexOf(cmd.Arguments, "resume"));
    var cAt = IndexOf(cmd.Arguments, "-c");
    Equal("model_reasoning_effort=xhigh", cmd.Arguments[cAt + 1]);
    True(cmd.Arguments.Contains("thread-1"));
    True(cmd.Arguments.Contains("-"));
}

static void ProviderCommandOmitsCodexModelFlagsWhenUnset()
{
    var cmd = ProviderCommand.BuildCodex(
        DispatchAction.Resume,
        "thread-1",
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        "codex.cmd");
    True(!cmd.Arguments.Contains("--model"));
    True(!cmd.Arguments.Contains("-c"));
    Equal("exec", cmd.Arguments[0]);
    Equal("--json", cmd.Arguments[1]);
    True(cmd.Arguments.Contains("resume"));
    True(cmd.Arguments.Contains("thread-1"));
    True(cmd.Arguments.Contains("-"));
}

static void ProviderCommandAddsGrokModelAndEffort()
{
    var cmd = ProviderCommand.BuildGrok(
        DispatchAction.Resume,
        "sess-1",
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        grokHome: "D:\\tmp\\grok-home",
        model: "grok-4.6",
        reasoningEffort: "high");
    True(cmd.Arguments.Contains("--model"));
    True(cmd.Arguments.Contains("grok-4.6"));
    True(cmd.Arguments.Contains("--reasoning-effort"));
    True(cmd.Arguments.Contains("high"));
    True(cmd.Arguments.Contains("-r"));
    True(cmd.Arguments.Contains("sess-1"));
}

static void ProviderCommandOmitsGrokModelFlagsWhenUnset()
{
    var cmd = ProviderCommand.BuildGrok(
        DispatchAction.Resume,
        "sess-1",
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        grokHome: "D:\\tmp\\grok-home");
    True(!cmd.Arguments.Contains("--model"));
    True(!cmd.Arguments.Contains("--reasoning-effort"));
    True(cmd.Arguments.Contains("-r"));
    True(cmd.Arguments.Contains("sess-1"));
    True(cmd.Arguments.Contains("--prompt-file"));
}

static void DispatchJobStoreRoundTripsModelAndEffort()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-job-model-{Guid.NewGuid():N}");
    try
    {
        var store = new DispatchJobStore(root);
        var selection = new DispatchSelection(
            "codex",
            "thread-1",
            "D:\\code\\app",
            null,
            DispatchAction.Resume,
            "test");
        var job = store.Create(selection, "hello");
        job.Model = "gpt-5.6-luna";
        job.ReasoningEffort = "xhigh";
        store.Write(job);

        var loaded = store.Require(job.JobId);
        Equal("gpt-5.6-luna", loaded.Model);
        Equal("xhigh", loaded.ReasoningEffort);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DispatchJobStoreIgnoresMissingModelFields()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-old-job-{Guid.NewGuid():N}");
    const string jobId = "pre-change-job";
    try
    {
        var directory = Path.Combine(root, jobId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "status.json"), $$"""
            {
              "jobId": "{{jobId}}",
              "provider": "codex",
              "action": "resume",
              "status": "queued",
              "createdAt": "2026-08-15T00:00:00+00:00",
              "promptChars": 5
            }
            """);

        var loaded = new DispatchJobStore(root).Require(jobId);
        True(loaded.Model is null);
        True(loaded.ReasoningEffort is null);
        Equal(DispatchJobStatus.Queued, loaded.Status);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderCommandPreassignsGrokId()
{
    var command = ProviderCommand.BuildGrok(
        DispatchAction.Create,
        null,
        "D:\\code\\app",
        "D:\\tmp\\prompt.txt",
        "D:\\tmp\\grok-home",
        "11111111-1111-4111-8111-111111111111");
    True(command.Arguments.Contains("--session-id"));
    True(command.Arguments.Contains("11111111-1111-4111-8111-111111111111"));
}

static void ProviderOutputParsesClaude()
{
    var path = Path.Combine(Path.GetTempPath(), $"cccg-claude-output-{Guid.NewGuid():N}.jsonl");
    try
    {
        File.WriteAllText(path,
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"11111111-1111-4111-8111-111111111111\"}\n"
            + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"draft\"}]}}\n"
            + "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"11111111-1111-4111-8111-111111111111\",\"result\":\"CLAUDE-OK\"}");
        Equal(
            "11111111-1111-4111-8111-111111111111",
            ProviderOutputParser.FindSessionId("claude", path));
        Equal("CLAUDE-OK", ProviderOutputParser.CollectResponse("claude", path));
        True(ProviderOutputParser.FindError("claude", path) is null);
    }
    finally
    {
        File.Delete(path);
    }
}

static void ProviderOutputNormalizesGrok()
{
    var path = Path.Combine(Path.GetTempPath(), $"cccg-grok-output-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(path, """
            {
              "text": "GROK-NORMALIZED-OK",
              "thought": "private chain text",
              "usage": { "input_tokens": 10 },
              "sessionId": "11111111-1111-4111-8111-111111111111"
            }
            """);
        Equal("GROK-NORMALIZED-OK", ProviderOutputParser.CollectResponse("grok", path));
    }
    finally
    {
        File.Delete(path);
    }
}

static void FileProcessLauncherPreservesUtf8()
{
    const string expected = "繁體中文測試：你好，世界！";
    const string expectedError = "診斷訊息：正常";
    var root = Path.Combine(Path.GetTempPath(), $"cccg-utf8-process-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var stdoutPath = Path.Combine(root, "stdout.log");
    var stderrPath = Path.Combine(root, "stderr.log");

    try
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process executable is unavailable.");
        var childArguments = Path.GetFileNameWithoutExtension(executable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { System.Reflection.Assembly.GetExecutingAssembly().Location, "--emit-utf8-grok-json" }
            : new[] { "--emit-utf8-grok-json" };

        var launcher = new FileProcessLauncher();
        launcher.Start(
            new LaunchCommand(executable, childArguments, root),
            stdoutPath,
            stderrPath,
            stdinPath: null,
            out var waiter);

        Equal(0, waiter.Wait(TimeSpan.FromSeconds(15)));
        var stdoutBytes = File.ReadAllBytes(stdoutPath);
        True(stdoutBytes.Length > 0 && stdoutBytes[0] == (byte)'{');
        Equal(expected, ProviderOutputParser.CollectResponse("grok", stdoutPath));
        Equal(expectedError, File.ReadAllText(stderrPath, System.Text.Encoding.UTF8).Trim());
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void FileProcessLauncherPreservesUtf8Stdin()
{
    const string expected = "編碼測試通過，繁體中文顯示正常";
    var root = Path.Combine(Path.GetTempPath(), $"cccg-utf8-stdin-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var stdinPath = Path.Combine(root, "prompt.txt");
    var stdoutPath = Path.Combine(root, "stdout.log");
    var stderrPath = Path.Combine(root, "stderr.log");
    File.WriteAllText(stdinPath, expected, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    try
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process executable is unavailable.");
        var childArguments = Path.GetFileNameWithoutExtension(executable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { System.Reflection.Assembly.GetExecutingAssembly().Location, "--echo-utf8-stdin" }
            : new[] { "--echo-utf8-stdin" };

        var launcher = new FileProcessLauncher();
        launcher.Start(
            new LaunchCommand(executable, childArguments, root),
            stdoutPath,
            stderrPath,
            stdinPath,
            out var waiter);

        Equal(0, waiter.Wait(TimeSpan.FromSeconds(15)));
        Equal(expected, File.ReadAllText(stdoutPath, System.Text.Encoding.UTF8).TrimEnd('\r', '\n'));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void DispatchRunnerEnqueueCopiesModelAndEffort()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-enqueue-model-{Guid.NewGuid():N}");
    try
    {
        var store = new DispatchJobStore(Path.Combine(root, "jobs"));
        var runner = new DispatchRunner(
            store,
            _ => Array.Empty<Peer>(),
            owners: new OwnerRegistry(Path.Combine(root, "owners")));

        var job = runner.Enqueue(
            "codex",
            "hello",
            cwd: "D:\\code\\app",
            model: "  gpt-5.6-luna  ",
            reasoningEffort: "  medium  ");
        var loaded = store.Require(job.JobId);
        Equal("gpt-5.6-luna", loaded.Model);
        Equal("medium", loaded.ReasoningEffort);

        var omitted = runner.Enqueue(
            "codex",
            "hello again",
            cwd: "D:\\code\\app",
            model: "  ",
            reasoningEffort: null);
        loaded = store.Require(omitted.JobId);
        True(loaded.Model is null);
        True(loaded.ReasoningEffort is null);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DispatchRunnerPassesOverridesToCodexLaunch()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-launch-model-{Guid.NewGuid():N}");
    try
    {
        var peers = new[]
        {
            new Peer("codex", "thread-1", PeerStatus.Resumable, "D:\\code\\app", "Peer", null, null, null, null, null, null)
        };
        const string output =
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"CODEX-OVERRIDE-OK\"}}";
        var launcher = new RecordingProcessLauncher(output);
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ => peers,
            launcher,
            codexCommand: "codex.exe",
            owners: new OwnerRegistry(Path.Combine(root, "owners")));

        var job = runner.Enqueue(
            "codex",
            "hello",
            "thread-1",
            "D:\\code\\app",
            allowNew: false,
            model: "gpt-5.6-luna",
            reasoningEffort: "xhigh");
        var done = runner.Run(job.JobId);
        Equal(DispatchJobStatus.Succeeded, done.Status);

        var command = launcher.LastCommand
            ?? throw new InvalidOperationException("Expected a captured launch command.");
        var modelAt = IndexOf(command.Arguments, "--model");
        Equal("gpt-5.6-luna", command.Arguments[modelAt + 1]);
        True(modelAt < IndexOf(command.Arguments, "resume"));
        var effortAt = IndexOf(command.Arguments, "-c");
        Equal("model_reasoning_effort=xhigh", command.Arguments[effortAt + 1]);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DispatchRunnerRejectsClaudeOverrides()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-claude-model-{Guid.NewGuid():N}");
    try
    {
        var launcher = new CountingProcessLauncher("SHOULD-NOT-LAUNCH");
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ => Array.Empty<Peer>(),
            launcher,
            claudeCommand: "claude.exe",
            owners: new OwnerRegistry(Path.Combine(root, "owners")));

        var modelJob = runner.Enqueue(
            "claude",
            "hello",
            cwd: "D:\\code\\app",
            model: "gpt-5.6-luna");
        var modelDone = runner.Run(modelJob.JobId);
        Equal(DispatchJobStatus.Failed, modelDone.Status);
        True(modelDone.Error?.Contains("claude", StringComparison.OrdinalIgnoreCase) == true);
        True(modelDone.Error?.Contains("model", StringComparison.Ordinal) == true);
        True(modelDone.Error?.Contains("reasoningEffort", StringComparison.Ordinal) == true);

        var effortJob = runner.Enqueue(
            "claude",
            "hello again",
            cwd: "D:\\code\\app",
            reasoningEffort: "xhigh");
        var effortDone = runner.Run(effortJob.JobId);
        Equal(DispatchJobStatus.Failed, effortDone.Status);
        True(effortDone.Error?.Contains("claude", StringComparison.OrdinalIgnoreCase) == true);
        True(effortDone.Error?.Contains("model", StringComparison.Ordinal) == true);
        True(effortDone.Error?.Contains("reasoningEffort", StringComparison.Ordinal) == true);
        Equal(0, launcher.Starts);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CollectSurfacesModelWithoutBreakingShape()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-collect-model-{Guid.NewGuid():N}");
    try
    {
        var peers = new[]
        {
            new Peer("grok", "idle", PeerStatus.Resumable, "D:\\code\\app", "Idle", null, null, null, null, null, null)
        };
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ => peers,
            new FakeProcessLauncher("DISPATCH-OK"),
            owners: new OwnerRegistry(Path.Combine(root, "owners")));
        var job = runner.Enqueue(
            "grok",
            "hello",
            "idle",
            "D:\\code\\app",
            allowNew: false,
            model: "grok-4.6",
            reasoningEffort: "high");
        var done = runner.Run(job.JobId);
        Equal(DispatchJobStatus.Succeeded, done.Status);

        var collected = System.Text.Json.JsonSerializer.SerializeToElement(runner.Collect(job.JobId));
        True(collected.TryGetProperty("JobId", out _));
        True(collected.TryGetProperty("Status", out _));
        Equal("DISPATCH-OK", collected.GetProperty("response").GetString());
        Equal("grok-4.6", collected.GetProperty("Model").GetString());
        Equal("high", collected.GetProperty("ReasoningEffort").GetString());
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DispatchRunnerBindsNewClaudeSession()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-new-claude-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(Path.Combine(root, "jobs"));
    var bindings = new DispatchBindingStore(Path.Combine(root, "bindings"));
    var runner = new DispatchRunner(
        store,
        _ => Array.Empty<Peer>(),
        new ClaudeEchoProcessLauncher(),
        claudeCommand: "claude.exe",
        bindings: bindings);
    var job = runner.Enqueue("claude", "Reply CLAUDE-BOUND-OK", cwd: "D:\\code\\app");
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    True(Guid.TryParse(done.SessionId, out _));
    Equal(done.SessionId, bindings.Load("claude", "D:\\code\\app")?.SessionId);
    var collected = System.Text.Json.JsonSerializer.Serialize(runner.Collect(job.JobId));
    True(collected.Contains("CLAUDE-BOUND-OK", StringComparison.Ordinal));
}

static void DispatchRunnerRoutesLiveClaude()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-live-claude-{Guid.NewGuid():N}");
    var peers = new[]
    {
        new Peer(
            "claude",
            "11111111-1111-4111-8111-111111111111",
            PeerStatus.LiveWorking,
            "D:\\code\\app",
            "Doc peer",
            null,
            "claude-sonnet-5",
            123,
            null,
            null,
            4)
    };
    var runner = new DispatchRunner(new DispatchJobStore(root), _ => peers);
    try
    {
        runner.Enqueue(
            "claude",
            "hello",
            "11111111-1111-4111-8111-111111111111",
            "D:\\code\\app",
            allowNew: false);
        throw new InvalidOperationException("Expected live Claude routing refusal.");
    }
    catch (InvalidOperationException exception)
    {
        True(exception.Message.Contains("mcp__ccd_session_mgmt__list_sessions", StringComparison.Ordinal));
        True(exception.Message.Contains("mcp__ccd_session_mgmt__send_message", StringComparison.Ordinal));
    }
}

static void DispatchRunnerBindsNewCodexThread()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-new-codex-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(Path.Combine(root, "jobs"));
    var bindings = new DispatchBindingStore(Path.Combine(root, "bindings"));
    const string output = "{\"type\":\"thread.started\",\"thread_id\":\"thread-new\"}\n"
        + "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"NEW-CODEX-OK\"}}";
    var runner = new DispatchRunner(
        store,
        _ => Array.Empty<Peer>(),
        new FakeProcessLauncher(output),
        codexCommand: "codex.exe",
        bindings: bindings);
    var job = runner.Enqueue("codex", "Reply NEW-CODEX-OK", cwd: "D:\\code\\app");
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    Equal("thread-new", done.SessionId);
    Equal("thread-new", bindings.Load("codex", "D:\\code\\app")?.SessionId);
    True(bindings.Load("codex", "D:\\code\\app")?.ManagedByCccg == true);
    var collected = System.Text.Json.JsonSerializer.Serialize(runner.Collect(job.JobId));
    True(collected.Contains("NEW-CODEX-OK", StringComparison.Ordinal));
}

static void DispatchRunnerCollectsSuccess()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cccg-jobs-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(directory);
    var peers = new[]
    {
        new Peer("grok", "idle", PeerStatus.Resumable, "D:\\code\\app", "Idle", null, null, null, null, null, null)
    };
    var runner = new DispatchRunner(store, _ => peers, new FakeProcessLauncher("DISPATCH-OK"));
    var job = runner.Dispatch("grok", "Reply only DISPATCH-OK", "idle", "D:\\code\\app", allowNew: false);
    True(!string.IsNullOrWhiteSpace(job.JobId));
    var deadline = DateTime.UtcNow.AddSeconds(2);
    DispatchJob done;
    do
    {
        done = runner.Status(job.JobId);
        if (done.Status is DispatchJobStatus.Succeeded or DispatchJobStatus.Failed)
        {
            break;
        }

        Thread.Sleep(20);
    }
    while (DateTime.UtcNow < deadline);

    Equal(DispatchJobStatus.Succeeded, done.Status);
    var collected = runner.Collect(job.JobId);
    var json = System.Text.Json.JsonSerializer.Serialize(collected);
    True(json.Contains("DISPATCH-OK", StringComparison.Ordinal));
}

static void DispatchRunnerRefusesUnownedLiveWindow()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-live-refuse-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(Path.Combine(root, "jobs"));
    var launcher = new CountingProcessLauncher("SHOULD-NOT-LAUNCH");
    var injector = new RecordingLiveInputInjector();
    var runner = new DispatchRunner(
        store,
        _ =>
        [
            new Peer("codex", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, 4242, null, null, null)
        ],
        launcher,
        codexCommand: "codex.exe",
        injector: injector,
        owners: new OwnerRegistry(Path.Combine(root, "owners")));
    try
    {
        runner.Enqueue("codex", "hello live window", "busy", "D:\\code\\app", allowNew: false);
        throw new InvalidDataException("Expected the unowned live peer to fail closed.");
    }
    catch (InvalidOperationException exception)
    {
        True(exception.Message.Contains("not CCCG-owned", StringComparison.Ordinal));
    }

    Equal(0, launcher.Starts);
    Equal(0, injector.Calls.Count);
}

static void DispatchRunnerDeliverSkipsWorkspaceLease()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-live-nolease-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(Path.Combine(root, "jobs"));
    var affinity = "grok|cwd|" + Path.GetFullPath("D:\\code\\app")
        .TrimEnd(Path.DirectorySeparatorChar)
        .ToLowerInvariant();
    var gatePath = Path.Combine(
        store.Root,
        "..",
        "leases",
        CrossProcessFileGate.Key(affinity) + ".lock");
    using var held = CrossProcessFileGate.Acquire(gatePath, TimeSpan.FromSeconds(2));
    var registry = new OwnerRegistry(Path.Combine(root, "owners"));
    var transport = new EchoTurnTransport(text => "OWNED-OK");
    using var owner = new BackgroundOwner(new OwnerDaemon(
        "grok", "D:\\code\\app", "busy", transport, registry, TimeSpan.FromMilliseconds(20)));
    var injector = new RecordingLiveInputInjector();
    var runner = new DispatchRunner(
        store,
        _ =>
        [
            new Peer("grok", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, 11, null, null, null)
        ],
        new FakeProcessLauncher("SHOULD-NOT-LAUNCH"),
        injector: injector,
        owners: registry,
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        deliverWaitTimeout: TimeSpan.FromSeconds(10));
    var job = runner.Enqueue("grok", "hello", "busy", "D:\\code\\app", allowNew: false);
    Equal("deliver", job.Action);
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    Equal(0, injector.Calls.Count);
    Equal("delivered", done.ReceiptStatus);
}

static void LiveInjectorBindsWriteConsoleInputW()
{
    Equal("WriteConsoleInputW", WindowsLiveInputInjector.WriteConsoleInputEntryPoint);
}

static void LiveInjectorBuildsEnterRecords()
{
    Equal(
        "[CCCG dispatch from Claude Desktop to grok] hello",
        WindowsLiveInputInjector.FlattenForTyping(
            "[CCCG dispatch from Claude Desktop to grok]\n\nhello\n"));
    var records = WindowsLiveInputInjector.BuildKeyEvents("你好");
    True(records.Length >= 6);
    Equal(WindowsLiveInputInjector.KeyEvent, records[0].EventType);
    Equal('你', records[0].KeyEvent.UnicodeChar);
    Equal('\r', records[^1].KeyEvent.UnicodeChar);
    Equal(WindowsLiveInputInjector.VkReturn, records[^1].KeyEvent.wVirtualKeyCode);
    var typed = WindowsLiveInputInjector.BuildSendInput("a\n\nb");
    Equal(6, typed.Length);
    var submit = WindowsLiveInputInjector.BuildSubmitInput();
    True(submit.Length >= 8);
    True(WindowsLiveInputInjector.EnumerateProcessChain(Environment.ProcessId).Contains(Environment.ProcessId));
}

static void DispatchRunnerDeliversToLiveIdlePeer()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-live-idle-{Guid.NewGuid():N}");
    var peers = new[]
    {
        new Peer("grok", "idle", PeerStatus.LiveIdle, "D:\\code\\app", "Idle", null, null, 9, null, null, null)
    };
    var registry = new OwnerRegistry(Path.Combine(root, "owners"));
    var transport = new EchoTurnTransport(text => "OWNED-REPLY::" + text);
    using var owner = new BackgroundOwner(new OwnerDaemon(
        "grok", "D:\\code\\app", "idle", transport, registry, TimeSpan.FromMilliseconds(20)));
    var injector = new RecordingLiveInputInjector();
    var runner = new DispatchRunner(
        new DispatchJobStore(Path.Combine(root, "jobs")),
        _ => peers,
        new FakeProcessLauncher("SHOULD-NOT-LAUNCH"),
        injector: injector,
        owners: registry,
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        deliverWaitTimeout: TimeSpan.FromSeconds(10));
    var job = runner.Enqueue(
        "grok",
        "hello owned peer",
        cwd: "D:\\code\\app",
        allowNew: false,
        model: "grok-4.6",
        reasoningEffort: "high");
    True(job.Reason?.Contains("CCCG-owned", StringComparison.Ordinal) == true);
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    Equal(0, injector.Calls.Count);
    Equal("delivered", done.ReceiptStatus);
    True(done.DeliveredAt is not null);
    var json = System.Text.Json.JsonSerializer.Serialize(runner.Collect(job.JobId));
    True(json.Contains("OWNED-REPLY", StringComparison.Ordinal));
    Equal(1, transport.Turns.Count);
    True(transport.Turns[0].StartsWith("[CCCG message from claude]", StringComparison.Ordinal));
    True(transport.Turns[0].Contains("hello owned peer", StringComparison.Ordinal));
    // Exactly ONE sender label: the DispatchJobStore prompt.txt header must
    // not be wrapped a second time by the owner daemon.
    True(!transport.Turns[0].Contains("[CCCG dispatch from", StringComparison.Ordinal));
    Equal("[CCCG message from claude]\n\nhello owned peer", transport.Turns[0]);
    Equal("grok-4.6", transport.LastModel);
    Equal("high", transport.LastReasoningEffort);
}

static void DispatchRunnersSerializeManagedPeer()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-serial-{Guid.NewGuid():N}");
    var jobs = Path.Combine(root, "jobs");
    var bindings = new DispatchBindingStore(Path.Combine(root, "bindings"));
    bindings.Save("codex", "managed-peer", "D:\\code\\app");
    var peers = new[]
    {
        new Peer("codex", "managed-peer", PeerStatus.LiveWorking, "D:\\code\\app", "Managed", null, null, null, null, null, null)
    };
    var launcher = new TrackingProcessLauncher();
    var first = new DispatchRunner(
        new DispatchJobStore(jobs),
        _ => peers,
        launcher,
        codexCommand: "codex.exe",
        bindings: bindings);
    var second = new DispatchRunner(
        new DispatchJobStore(jobs),
        _ => peers,
        launcher,
        codexCommand: "codex.exe",
        bindings: bindings);
    var job1 = first.Enqueue("codex", "one", "managed-peer", "D:\\code\\app", false);
    var job2 = second.Enqueue("codex", "two", "managed-peer", "D:\\code\\app", false);
    Task.WaitAll(
        Task.Run(() => first.Run(job1.JobId)),
        Task.Run(() => second.Run(job2.JobId)));
    Equal(1, launcher.MaxConcurrent);
    Equal(DispatchJobStatus.Succeeded, first.Status(job1.JobId).Status);
    Equal(DispatchJobStatus.Succeeded, second.Status(job2.JobId).Status);
}

static void DispatchStatusFailsDeadWorker()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-dead-worker-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(root);
    var peers = new[]
    {
        new Peer("codex", "old", PeerStatus.Resumable, "D:\\code\\app", "Old", null, null, null, null, null, null)
    };
    var runner = new DispatchRunner(store, _ => peers, new FakeProcessLauncher("unused"));
    var job = runner.Enqueue("codex", "test", "old", "D:\\code\\app", false);
    runner.MarkWorker(job.JobId, int.MaxValue);
    var failed = runner.Status(job.JobId);
    Equal(DispatchJobStatus.Failed, failed.Status);
    True(failed.Error?.Contains("worker exited", StringComparison.OrdinalIgnoreCase) == true);
}

static void BindingStorePrefersBoundPeer()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-bind-{Guid.NewGuid():N}");
    var store = new DispatchBindingStore(root);
    store.Save("codex", "bound-id", "D:\\code\\app");
    var peers = new[]
    {
        new Peer("codex", "other", PeerStatus.LiveIdle, "D:\\code\\app", "Other", null, null, null, null, null, null),
        new Peer("codex", "bound-id", PeerStatus.Resumable, "D:\\code\\app", "Bound", null, null, null, null, null, null)
    };
    var marked = store.Mark(peers, "codex", "D:\\code\\app");
    True(marked.Single(peer => peer.SessionId == "bound-id").Bound);
    var picked = PeerSelector.Select("codex", marked, null, "D:\\code\\app", true, store.Load("codex", "D:\\code\\app")?.SessionId);
    Equal("bound-id", picked.SessionId);
}

static void InboxLedgerRoundTrips()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-inbox-{Guid.NewGuid():N}");
    var inbox = new InboxLedger(root);
    inbox.Post("claude", "codex", "Please review", toProvider: "codex", toSessionId: "s1");
    inbox.Post("codex", "claude", "Done", fromProvider: "codex", fromSessionId: "s1");
    var listed = inbox.List();
    Equal(2, listed.Count);
    Equal("pending", listed[0].Status);
    var acked = inbox.Ack(listed[1].Id);
    Equal("read", acked?.Status);
    Equal(1, inbox.List(unreadOnly: true).Count);
}

static void InboxLedgerPreservesConcurrentPosts()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-inbox-race-{Guid.NewGuid():N}");
    var first = new InboxLedger(root);
    var second = new InboxLedger(root);
    Parallel.For(0, 80, index =>
    {
        var ledger = index % 2 == 0 ? first : second;
        ledger.Post("claude", "codex", "message-" + index);
    });
    Equal(80, first.List(limit: 100).Count);
}

static void OwnerRegistryDetectsStaleOwner()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owners-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var key = OwnerRegistry.Key("codex", "D:\\code\\app", "sess-1");
    var lease = registry.AcquireLease(key);
    try
    {
        registry.Register(key, new OwnerRegistration(
            1, "codex", "sess-1", "D:\\code\\app", Environment.ProcessId,
            registry.SpoolDirectory(key), DateTimeOffset.UtcNow));
        Equal("sess-1", registry.TryFind("codex", "sess-1", null)?.SessionId);
        Equal("sess-1", registry.TryFind("codex", null, "D:\\code\\app")?.SessionId);
        True(registry.TryFind("grok", "sess-1", null) is null);
        True(registry.TryFind("codex", "sess-2", null) is null);
        True(registry.LeaseIsHeld(key));
    }
    finally
    {
        lease.Dispose();
    }

    True(!registry.LeaseIsHeld(key));
    True(registry.TryFind("codex", "sess-1", null) is null);
    // F9: the stale registration is cleaned up by the failed lookup.
    True(!File.Exists(registry.RegistrationPath(key)));
}

static void OwnerRegistryCleansStaleRegistrations()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owners-stale-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var staleKey = OwnerRegistry.Key("codex", "D:\\code\\app", "gone");
    var staleLease = registry.AcquireLease(staleKey);
    registry.Register(staleKey, new OwnerRegistration(
        1, "codex", "gone", "D:\\code\\app", Environment.ProcessId,
        registry.SpoolDirectory(staleKey), DateTimeOffset.UtcNow));
    Directory.CreateDirectory(registry.SpoolDirectory(staleKey)); // empty spool
    staleLease.Dispose(); // the owner dies

    // A stale registration with unconsumed spool files keeps its spool.
    var busyKey = OwnerRegistry.Key("codex", "D:\\code\\busy", "kept");
    var busyLease = registry.AcquireLease(busyKey);
    registry.Register(busyKey, new OwnerRegistration(
        1, "codex", "kept", "D:\\code\\busy", Environment.ProcessId,
        registry.SpoolDirectory(busyKey), DateTimeOffset.UtcNow));
    var busySpool = new OwnerSpool(registry.SpoolDirectory(busyKey));
    busySpool.Post(new OwnerMessage("m-pending", "claude", null, "still queued", DateTimeOffset.UtcNow));
    busyLease.Dispose();

    True(registry.TryFind("codex", null, "D:\\code\\app") is null);
    True(!File.Exists(registry.RegistrationPath(staleKey)));
    True(!Directory.Exists(Path.Combine(registry.Root, staleKey)));
    // The dead-but-nonempty spool survives (evidence is never destroyed) even
    // though its registration JSON is gone.
    True(!File.Exists(registry.RegistrationPath(busyKey)));
    Equal(1, busySpool.ReadIncoming().Count);
}

static void OwnerDaemonProcessesSpool()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-daemon-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var transport = new EchoTurnTransport(text => "ECHO::" + text, sessionId: "thread-real");
    using var daemon = new OwnerDaemon("codex", "D:\\code\\app", sessionId: null, transport, registry);
    var registration = daemon.Start();
    Equal(Environment.ProcessId, registration.OwnerPid);
    var spool = new OwnerSpool(registration.SpoolDir);
    spool.Post(new OwnerMessage("m1", "claude", "sess-9", "please review", DateTimeOffset.UtcNow));

    True(spool.TryReadReceipt("m1") is null);
    Equal(1, daemon.ProcessPendingOnce());

    var receipt = spool.TryReadReceipt("m1")!;
    Equal(OwnerReceiptStatus.Delivered, receipt.Status);
    True(receipt.DeliveredAt is not null);
    True(receipt.ResponseText!.Contains("please review", StringComparison.Ordinal));
    Equal(1, transport.Turns.Count);
    True(transport.Turns[0].StartsWith("[CCCG message from claude sess-9]", StringComparison.Ordinal));
    Equal(0, spool.ReadIncoming().Count);
    Equal(0, daemon.ProcessPendingOnce());
    Equal("thread-real", registry.TryFind("codex", "thread-real", null)?.SessionId);
}

static void OwnerMessageRoundTripsModelAndEffort()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-message-{Guid.NewGuid():N}");
    try
    {
        var spool = new OwnerSpool(root);
        var now = DateTimeOffset.UtcNow;
        spool.Post(new OwnerMessage(
            "with-overrides",
            "claude",
            "sender-1",
            "hello",
            now,
            Model: "gpt-5.6-luna",
            ReasoningEffort: "xhigh"));
        spool.Post(new OwnerMessage(
            "old-shape",
            "claude",
            null,
            "hello again",
            now.AddMilliseconds(1)));

        var messages = spool.ReadIncoming();
        var withOverrides = messages.Single(item => item.Message.MessageId == "with-overrides").Message;
        Equal("gpt-5.6-luna", withOverrides.Model);
        Equal("xhigh", withOverrides.ReasoningEffort);

        var oldShape = messages.Single(item => item.Message.MessageId == "old-shape").Message;
        True(oldShape.Model is null);
        True(oldShape.ReasoningEffort is null);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void OwnerDaemonForwardsPerMessageOverrides()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-overrides-{Guid.NewGuid():N}");
    try
    {
        var registry = new OwnerRegistry(root);
        var transport = new EchoTurnTransport(text => "ECHO::" + text);
        using (var daemon = new OwnerDaemon(
                   "codex", "D:\\code\\app", "thread-1", transport, registry))
        {
            var registration = daemon.Start();
            var spool = new OwnerSpool(registration.SpoolDir);
            var now = DateTimeOffset.UtcNow;
            spool.Post(new OwnerMessage(
                "override-turn",
                "claude",
                null,
                "please review",
                now,
                Model: "gpt-5.6-luna",
                ReasoningEffort: "xhigh"));

            Equal(1, daemon.ProcessPendingOnce());
            Equal("gpt-5.6-luna", transport.LastModel);
            Equal("xhigh", transport.LastReasoningEffort);
            Equal(
                OwnerReceiptStatus.Delivered,
                spool.TryReadReceipt("override-turn")!.Status);

            spool.Post(new OwnerMessage(
                "default-turn",
                "claude",
                null,
                "use defaults",
                now.AddMilliseconds(1)));
            Equal(1, daemon.ProcessPendingOnce());
            True(transport.LastModel is null);
            True(transport.LastReasoningEffort is null);
            Equal(
                OwnerReceiptStatus.Delivered,
                spool.TryReadReceipt("default-turn")!.Status);
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CodexOwnerTurnTransportFallsBackToConstructorDefaults()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-codex-{Guid.NewGuid():N}");
    var jsonTransport = new ScriptedJsonLineTransport(new[]
    {
        "{\"id\":1,\"result\":{}}",
        "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
        "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}",
        "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"item\":{\"type\":\"agentMessage\",\"text\":\"DEFAULT\"}}}",
        "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\"}}}",
        "{\"id\":4,\"result\":{\"turn\":{\"id\":\"turn-2\"}}}",
        "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-2\",\"item\":{\"type\":\"agentMessage\",\"text\":\"OVERRIDE\"}}}",
        "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-2\",\"status\":\"completed\"}}}"
    });
    var client = new PersistentCodexAppServerClient(
        "owner-defaults",
        new CodexThreadBindingStore(Path.Combine(root, "bindings")),
        _ => Task.FromResult<IJsonLineTransport>(jsonTransport));
    var transport = new CodexOwnerTurnTransport(
        client,
        "gpt-5.6-luna",
        "medium",
        "D:\\code\\app");
    try
    {
        Equal("DEFAULT", transport.RunTurnAsync("first").GetAwaiter().GetResult());
        Equal(
            "OVERRIDE",
            transport.RunTurnAsync(
                "second",
                model: "gpt-5.6-terra",
                reasoningEffort: "high").GetAwaiter().GetResult());

        Equal(5, jsonTransport.Writes.Count);
        using var defaultTurn = System.Text.Json.JsonDocument.Parse(jsonTransport.Writes[3]);
        var defaultParams = defaultTurn.RootElement.GetProperty("params");
        Equal("turn/start", defaultTurn.RootElement.GetProperty("method").GetString());
        Equal("gpt-5.6-luna", defaultParams.GetProperty("model").GetString());
        Equal("medium", defaultParams.GetProperty("effort").GetString());

        using var overrideTurn = System.Text.Json.JsonDocument.Parse(jsonTransport.Writes[4]);
        var overrideParams = overrideTurn.RootElement.GetProperty("params");
        Equal("turn/start", overrideTurn.RootElement.GetProperty("method").GetString());
        Equal("gpt-5.6-terra", overrideParams.GetProperty("model").GetString());
        Equal("high", overrideParams.GetProperty("effort").GetString());
    }
    finally
    {
        transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void OwnerDaemonRecordsFailedTurn()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-fail-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var transport = new EchoTurnTransport(_ => throw new InvalidOperationException("PROVIDER-DOWN"));
    using var daemon = new OwnerDaemon("codex", "D:\\code\\app", "sess-1", transport, registry);
    var registration = daemon.Start();
    var spool = new OwnerSpool(registration.SpoolDir);
    spool.Post(new OwnerMessage("m1", "claude", null, "hello", DateTimeOffset.UtcNow));

    Equal(1, daemon.ProcessPendingOnce());
    var receipt = spool.TryReadReceipt("m1")!;
    Equal(OwnerReceiptStatus.Failed, receipt.Status);
    True(receipt.DeliveredAt is null);
    True(receipt.Error!.Contains("PROVIDER-DOWN", StringComparison.Ordinal));
    Equal(0, spool.ReadIncoming().Count);
}

static void OwnerSpoolPreservesChineseTextBytes()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-zh-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    const string chinese = "繁體中文測試:收到訊息了嗎?請確認「編碼」沒有壞掉。";
    var transport = new EchoTurnTransport(text => "回覆:" + text);
    using var daemon = new OwnerDaemon("codex", "D:\\code\\app", "sess-zh", transport, registry);
    var registration = daemon.Start();
    var spool = new OwnerSpool(registration.SpoolDir);
    spool.Post(new OwnerMessage("m-zh", "claude", null, chinese, DateTimeOffset.UtcNow));

    Equal(1, daemon.ProcessPendingOnce());
    Equal("[CCCG message from claude]\n\n" + chinese, transport.Turns[0]);

    var receipt = spool.TryReadReceipt("m-zh")!;
    Equal(OwnerReceiptStatus.Delivered, receipt.Status);
    var expectedReply = "回覆:[CCCG message from claude]\n\n" + chinese;
    Equal(expectedReply, receipt.ResponseText);
    True(System.Text.Encoding.UTF8.GetBytes(expectedReply)
        .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(receipt.ResponseText!)));
}

static void OwnerDaemonFailsAbandonedProcessingTurns()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-abandoned-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var key = OwnerRegistry.Key("codex", "D:\\code\\app", "sess-1");
    var spool = new OwnerSpool(registry.SpoolDirectory(key));
    // Simulate the previous owner: it claimed the message into processing\
    // and died before writing a receipt.
    var incomingPath = spool.Post(new OwnerMessage(
        "m-lost", "claude", null, "half-finished turn", DateTimeOffset.UtcNow));
    var processingPath = spool.MoveToProcessing(incomingPath);
    True(File.Exists(processingPath));

    var transport = new EchoTurnTransport(_ => "SHOULD-NOT-RUN");
    using var daemon = new OwnerDaemon("codex", "D:\\code\\app", "sess-1", transport, registry);
    daemon.Start();

    var receipt = spool.TryReadReceipt("m-lost")!;
    Equal(OwnerReceiptStatus.Failed, receipt.Status);
    True(receipt.Error!.Contains("unknown outcome", StringComparison.Ordinal));
    True(!File.Exists(processingPath));
    Equal(0, daemon.ProcessPendingOnce());
    Equal(0, transport.Turns.Count);
}

static void SecondOwnerInSameWorkspaceFailsFast()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-dup-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    using var first = new OwnerDaemon(
        "codex", "D:\\code\\app", "sess-1", new EchoTurnTransport(_ => "ok"), registry);
    first.Start();

    // Different session id, same provider + cwd: the per-session keys differ,
    // so only the workspace lease can make these two collide.
    using var second = new OwnerDaemon(
        "codex", "D:\\code\\app", "sess-2", new EchoTurnTransport(_ => "ok"), registry);
    try
    {
        second.Start();
        throw new InvalidDataException("Expected the second same-workspace owner to fail fast.");
    }
    catch (InvalidOperationException exception)
    {
        True(exception.Message.Contains("already running", StringComparison.Ordinal));
        True(exception.Message.Contains("app", StringComparison.OrdinalIgnoreCase));
    }

    // A different workspace is unaffected.
    using var elsewhere = new OwnerDaemon(
        "codex", "D:\\code\\other", "sess-3", new EchoTurnTransport(_ => "ok"), registry);
    elsewhere.Start();
}

static void OwnerDaemonExitsAfterRepeatedTransportFailures()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-transport-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(root);
    var transport = new EchoTurnTransport(_ =>
        throw new ProviderTransportException("codex app-server died", new EndOfStreamException()));
    using var daemon = new OwnerDaemon(
        "codex", "D:\\code\\app", "sess-1", transport, registry,
        TimeSpan.FromMilliseconds(10), transportFailureLimit: 2);
    var registration = daemon.Start();
    var spool = new OwnerSpool(registration.SpoolDir);
    spool.Post(new OwnerMessage("t1", "claude", null, "first", DateTimeOffset.UtcNow));
    spool.Post(new OwnerMessage("t2", "claude", null, "second", DateTimeOffset.UtcNow.AddMilliseconds(1)));

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Throws<ProviderTransportException>(() => daemon.Run(stop.Token));
    True(!stop.IsCancellationRequested); // exited by policy, not by the timeout
    Equal(2, daemon.ConsecutiveTransportFailures);
    Equal(OwnerReceiptStatus.Failed, spool.TryReadReceipt("t1")!.Status);
    Equal(OwnerReceiptStatus.Failed, spool.TryReadReceipt("t2")!.Status);

    // Disposing (what run-owner does on exit) releases the lease so TryFind
    // no longer routes to this dead owner and resume fallback works.
    var key = OwnerRegistry.Key("codex", "D:\\code\\app", "sess-1");
    True(registry.LeaseIsHeld(key));
    daemon.Dispose();
    True(!registry.LeaseIsHeld(key));
    True(registry.TryFind("codex", "sess-1", null) is null);
}

static void DispatchDeliverFailsWhenOwnerDies()
{
    // Real death semantics: the owner is fully registered and its OWN pid is
    // alive (this test process), but the DeleteOnClose lease is released
    // mid-delivery — exactly what happens when the owner process dies and its
    // PID gets reused. The lease, not the PID, must drive the fast failure.
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-dead-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(Path.Combine(root, "owners"));
    var key = OwnerRegistry.Key("codex", "D:\\code\\app", "busy");
    var lease = registry.AcquireLease(key);
    registry.Register(key, new OwnerRegistration(
        1, "codex", "busy", "D:\\code\\app", Environment.ProcessId,
        registry.SpoolDirectory(key), DateTimeOffset.UtcNow));

    var runner = new DispatchRunner(
        new DispatchJobStore(Path.Combine(root, "jobs")),
        _ =>
        [
            new Peer("codex", "busy", PeerStatus.LiveWorking, "D:\\code\\app", "Busy", null, null, 4242, null, null, null)
        ],
        new FakeProcessLauncher("unused"),
        codexCommand: "codex.exe",
        owners: registry,
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        deliverWaitTimeout: TimeSpan.FromSeconds(10));
    var job = runner.Enqueue("codex", "hello", "busy", "D:\\code\\app", allowNew: false);
    Equal("deliver", job.Action);
    var run = Task.Run(() => runner.Run(job.JobId));
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (runner.Status(job.JobId).Status != DispatchJobStatus.Running
        && DateTime.UtcNow < deadline)
    {
        Thread.Sleep(10);
    }

    lease.Dispose(); // the owner dies without writing a receipt
    var done = run.GetAwaiter().GetResult();
    Equal(DispatchJobStatus.Failed, done.Status);
    True(done.Error?.Contains("exited before", StringComparison.Ordinal) == true);
    True(done.Error?.Contains("unknown", StringComparison.Ordinal) == true);
}

static void DispatchDeliverEmptyReplySucceedsWithPlaceholder()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-empty-reply-{Guid.NewGuid():N}");
    var registry = new OwnerRegistry(Path.Combine(root, "owners"));
    var transport = new EchoTurnTransport(_ => "   ");
    using var owner = new BackgroundOwner(new OwnerDaemon(
        "codex", "D:\\code\\app", "sess-1", transport, registry, TimeSpan.FromMilliseconds(20)));
    var inbox = new InboxLedger(Path.Combine(root, "inbox"));
    var runner = new DispatchRunner(
        new DispatchJobStore(Path.Combine(root, "jobs")),
        _ =>
        [
            new Peer("codex", "sess-1", PeerStatus.LiveIdle, "D:\\code\\app", "Idle", null, null, 9, null, null, null)
        ],
        new FakeProcessLauncher("SHOULD-NOT-LAUNCH"),
        inbox: inbox,
        owners: registry,
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        deliverWaitTimeout: TimeSpan.FromSeconds(10));
    var job = runner.Enqueue("codex", "hello", "sess-1", "D:\\code\\app", allowNew: false);
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    Equal("delivered", done.ReceiptStatus);
    Equal(DispatchJobStatus.Succeeded, runner.Status(job.JobId).Status);
    var posted = inbox.List().Single(message => message.FromRole == "codex");
    Equal("(empty response)", posted.Content);
}

static void RecursionContextDefaultsAndJobHop()
{
    var context = RecursionContext.FromEnvironment(_ => null);
    Equal(0, context.ProcessHop);
    Equal(2, context.MaxHop);
    Equal(1, context.JobHop);
    True(context.HopSource.StartsWith("user:", StringComparison.Ordinal));
    Equal("human", context.HopChain);
    Equal("1", context.ProviderEnvironment("claude")["CCCG_HOP"]);
}

static void RecursionContextAllowsNestedHops()
{
    var context = new RecursionContext(
        processHop: 1,
        maxHop: 2,
        hopSource: "user:test@machine",
        hopChain: "human>codex");
    Equal(2, context.JobHop);
    var environment = context.ProviderEnvironment("claude");
    Equal("2", environment["CCCG_HOP"]);
    Equal("user:test@machine", environment["CCCG_HOP_SOURCE"]);
    Equal("human>codex>claude", environment["CCCG_HOP_CHAIN"]);
}

static void RecursionContextRejectsExceededHop()
{
    var context = new RecursionContext(
        processHop: 2,
        maxHop: 2,
        hopSource: "user:test@machine",
        hopChain: "human>claude>codex");
    var exception = Throws<InvalidOperationException>(() => context.ValidateNewJob());
    True(exception.Message.Contains("hopCount=3", StringComparison.Ordinal));
    True(exception.Message.Contains("maxHop=2", StringComparison.Ordinal));
    True(exception.Message.Contains("human>claude>codex", StringComparison.Ordinal));
}

static void RecursionContextRejectsMalformedEnvironment()
{
    var values = new Dictionary<string, string?>
    {
        ["CCCG_HOP"] = "not-an-int"
    };
    var exception = Throws<InvalidOperationException>(() =>
        RecursionContext.FromEnvironment(name => values.GetValueOrDefault(name)));
    True(exception.Message.Contains("CCCG_HOP", StringComparison.Ordinal));

    values["CCCG_HOP"] = "0";
    values["CCCG_MAX_HOP"] = "0";
    exception = Throws<InvalidOperationException>(() =>
        RecursionContext.FromEnvironment(name => values.GetValueOrDefault(name)));
    True(exception.Message.Contains("CCCG_MAX_HOP", StringComparison.Ordinal));
}

static void DispatchJobStoreRoundTripsHopMetadata()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-hop-job-{Guid.NewGuid():N}");
    var store = new DispatchJobStore(root);
    try
    {
        var job = new DispatchJob
        {
            JobId = "hop-job",
            Provider = "claude",
            HopCount = 2,
            HopSource = "user:test@machine",
            HopChain = "human>codex",
            CreatedAt = DateTimeOffset.UtcNow
        };
        store.Write(job);
        var loaded = store.Require(job.JobId);
        Equal(2, loaded.HopCount);
        Equal("user:test@machine", loaded.HopSource);
        Equal("human>codex", loaded.HopChain);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void DispatchRunnerInjectsHopEnvironment()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-hop-launch-{Guid.NewGuid():N}");
    try
    {
        var peers = new[]
        {
            new Peer("codex", "thread-1", PeerStatus.Resumable, "D:\\code\\app", "Peer", null, null, null, null, null, null)
        };
        var launcher = new RecordingProcessLauncher(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"OK\"}}" );
        var context = new RecursionContext(
            processHop: 0,
            maxHop: 2,
            hopSource: "user:test@machine",
            hopChain: "human",
            quotaRoot: Path.Combine(root, "quotas"));
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ => peers,
            launcher,
            codexCommand: "codex.exe",
            owners: new OwnerRegistry(Path.Combine(root, "owners")),
            recursion: context);

        var resume = runner.Enqueue("codex", "resume", "thread-1", "D:\\code\\app", allowNew: false);
        Equal(1, resume.HopCount);
        runner.Run(resume.JobId);
        Equal("1", launcher.LastCommand!.Environment![RecursionContext.HopVariable]);
        Equal("user:test@machine", launcher.LastCommand.Environment[RecursionContext.SourceVariable]);
        True(launcher.LastCommand.Environment[RecursionContext.ChainVariable].EndsWith(">codex", StringComparison.Ordinal));

        var createRoot = Path.Combine(root, "create");
        var createLauncher = new RecordingProcessLauncher(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"OK\"}}" );
        var createRunner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(createRoot, "jobs")),
            _ => Array.Empty<Peer>(),
            createLauncher,
            codexCommand: "codex.exe",
            owners: new OwnerRegistry(Path.Combine(createRoot, "owners")),
            recursion: new RecursionContext(
                0, 2, "user:test@machine", "human", Path.Combine(createRoot, "quotas")));
        // A create has no existing peer, so the command is exercised by the
        // same runner path after selection accepts a new session.
        var created = createRunner.Enqueue("codex", "create", cwd: "D:\\code\\app");
        var createdDone = createRunner.Run(created.JobId);
        Equal(DispatchJobStatus.Failed, createdDone.Status);
        // The fake output intentionally lacks a Codex thread id; the launch
        // still happened and captured the same child environment.
        Equal("1", createLauncher.LastCommand!.Environment![RecursionContext.HopVariable]);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void OwnerProviderChildReceivesHopEnvironment()
{
    var info = ProcessJsonLineTransport.BuildStartInfo(
        "codex",
        new Dictionary<string, string>
        {
            [RecursionContext.HopVariable] = "1",
            [RecursionContext.SourceVariable] = "user:test@machine",
            [RecursionContext.ChainVariable] = "human>codex"
        });
    Equal("1", info.Environment[RecursionContext.HopVariable]);
    Equal("user:test@machine", info.Environment[RecursionContext.SourceVariable]);
    Equal("human>codex", info.Environment[RecursionContext.ChainVariable]);
    True(info.ArgumentList.SequenceEqual(new[] { "app-server", "--listen", "stdio://" }));
}

static void HostWorkerPreservesInheritedHopEnvironment()
{
    var info = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "worker.exe",
        UseShellExecute = false
    };
    RecursionContext.ApplyEnvironment(
        info,
        new Dictionary<string, string>
        {
            [RecursionContext.HopVariable] = "1",
            [RecursionContext.SourceVariable] = "user:test@machine",
            [RecursionContext.ChainVariable] = "human>claude"
        });
    Equal("1", info.Environment[RecursionContext.HopVariable]);
    Equal("user:test@machine", info.Environment[RecursionContext.SourceVariable]);
    Equal("human>claude", info.Environment[RecursionContext.ChainVariable]);
}

static void QuotaCountsBySourceProviderAndDate()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-quota-{Guid.NewGuid():N}");
    var now = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(8));
    try
    {
        var ledger = new QuotaLedger(root, () => now, claudeLimit: 2, defaultLimit: 3);
        var first = ledger.TryConsume("user:a@pc", "claude");
        var second = ledger.TryConsume("user:a@pc", "claude");
        var otherProvider = ledger.TryConsume("user:a@pc", "codex");
        var otherSource = ledger.TryConsume("user:b@pc", "claude");
        True(first.Allowed);
        Equal(1, first.Count);
        True(second.Allowed);
        Equal(2, second.Count);
        True(otherProvider.Allowed);
        True(otherSource.Allowed);
        Equal(3, Directory.EnumerateFiles(Path.Combine(root, "2026-08-15"), "*.json").Count());
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void QuotaUsesClaudeAndDefaultOverrides()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-quota-override-{Guid.NewGuid():N}");
    try
    {
        var ledger = new QuotaLedger(
            root,
            () => DateTimeOffset.UtcNow,
            claudeLimit: 1,
            defaultLimit: 2);
        True(ledger.TryConsume("user:a@pc", "claude").Allowed);
        True(!ledger.TryConsume("user:a@pc", "claude").Allowed);
        True(ledger.TryConsume("user:a@pc", "grok").Allowed);
        True(ledger.TryConsume("user:a@pc", "grok").Allowed);
        True(!ledger.TryConsume("user:a@pc", "grok").Allowed);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void QuotaRejectsAtLimitWithTomorrowReset()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-quota-limit-{Guid.NewGuid():N}");
    try
    {
        var ledger = new QuotaLedger(root, () => DateTimeOffset.UtcNow, claudeLimit: 1, defaultLimit: 1);
        True(ledger.TryConsume("user:a@pc", "claude").Allowed);
        var rejected = ledger.TryConsume("user:a@pc", "claude");
        True(!rejected.Allowed);
        Equal(1, rejected.Count);
        Equal(1, rejected.Limit);
        True(rejected.Message.Contains("resets tomorrow", StringComparison.Ordinal));
        True(rejected.Message.Contains("claude", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void QuotaResetsAcrossLocalDate()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-quota-date-{Guid.NewGuid():N}");
    var now = new DateTimeOffset(2026, 8, 15, 23, 59, 0, TimeSpan.FromHours(8));
    try
    {
        var ledger = new QuotaLedger(root, () => now, claudeLimit: 1, defaultLimit: 1);
        True(ledger.TryConsume("user:a@pc", "claude").Allowed);
        True(!ledger.TryConsume("user:a@pc", "claude").Allowed);
        now = now.AddMinutes(2);
        var nextDay = ledger.TryConsume("user:a@pc", "claude");
        True(nextDay.Allowed);
        Equal(1, nextDay.Count);
        True(File.Exists(Path.Combine(root, "2026-08-16", nextDay.FileName)));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void QuotaReservationRollsBack()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-quota-release-{Guid.NewGuid():N}");
    try
    {
        var ledger = new QuotaLedger(root, () => DateTimeOffset.UtcNow, claudeLimit: 1, defaultLimit: 1);
        var reservation = ledger.TryConsume("user:a@pc", "claude");
        True(reservation.Allowed);
        ledger.Release(reservation);
        var retry = ledger.TryConsume("user:a@pc", "claude");
        True(retry.Allowed);
        Equal(1, retry.Count);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void OwnerMessageRoundTripsHopMetadata()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-hop-message-{Guid.NewGuid():N}");
    try
    {
        var spool = new OwnerSpool(root);
        spool.Post(new OwnerMessage(
            "hop-message",
            "claude",
            null,
            "hello",
            DateTimeOffset.UtcNow,
            Model: "gpt-5.6-luna",
            ReasoningEffort: "high",
            HopCount: 2,
            HopSource: "user:test@machine",
            HopChain: "human>claude"));
        var loaded = spool.ReadIncoming().Single().Message;
        Equal(2, loaded.HopCount);
        Equal("user:test@machine", loaded.HopSource);
        Equal("human>claude", loaded.HopChain);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void RecursiveEnqueuePostsSystemAudit()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-audit-{Guid.NewGuid():N}");
    try
    {
        var inbox = new InboxLedger(Path.Combine(root, "inbox"));
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ =>
            [
                new Peer("codex", "thread-1", PeerStatus.Resumable, "D:\\code\\app", "Peer", null, null, null, null, null, null)
            ],
            new FakeProcessLauncher("unused"),
            inbox: inbox,
            codexCommand: "codex.exe",
            owners: new OwnerRegistry(Path.Combine(root, "owners")),
            recursion: new RecursionContext(
                1, 2, "user:test@machine", "human>claude", Path.Combine(root, "quotas")));

        var job = runner.Enqueue(
            "codex",
            "secret prompt must not be audited",
            "thread-1",
            "D:\\code\\app",
            allowNew: false,
            model: "gpt-5.6-luna");
        var audit = inbox.List().Single(message => message.FromRole == "system");
        Equal("claude", audit.ToRole);
        Equal("cccg", audit.FromProvider);
        Equal("codex", audit.ToProvider);
        Equal(job.JobId, audit.JobId);
        True(audit.Content.Contains("source=user:test@machine", StringComparison.Ordinal));
        True(audit.Content.Contains("cwd=D:\\code\\app", StringComparison.Ordinal));
        True(audit.Content.Contains("model=gpt-5.6-luna", StringComparison.Ordinal));
        True(audit.Content.Contains("called provider=codex", StringComparison.Ordinal));
        True(!audit.Content.Contains("secret prompt", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void HumanRootEnqueueHasNoRecursiveAudit()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-no-audit-{Guid.NewGuid():N}");
    try
    {
        var inbox = new InboxLedger(Path.Combine(root, "inbox"));
        var runner = new DispatchRunner(
            new DispatchJobStore(Path.Combine(root, "jobs")),
            _ => Array.Empty<Peer>(),
            inbox: inbox,
            recursion: new RecursionContext(
                0, 2, "user:test@machine", "human", Path.Combine(root, "quotas")));
        runner.Enqueue("codex", "human root", cwd: "D:\\code\\app");
        True(!inbox.List().Any(message => message.FromRole == "system"));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void AuditFailureFailsClosed()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-audit-failure-{Guid.NewGuid():N}");
    try
    {
        var jobs = new DispatchJobStore(Path.Combine(root, "jobs"));
        var runner = new DispatchRunner(
            jobs,
            _ => Array.Empty<Peer>(),
            recursion: new RecursionContext(
                1, 2, "user:test@machine", "human>claude", Path.Combine(root, "quotas")));
        var exception = Throws<InvalidOperationException>(() =>
            runner.Enqueue("codex", "must be audited", cwd: "D:\\code\\app"));
        True(exception.Message.Contains("audit", StringComparison.OrdinalIgnoreCase));
        var status = Directory.EnumerateFiles(Path.Combine(root, "jobs"), "status.json", SearchOption.AllDirectories)
            .Select(path => System.Text.Json.JsonSerializer.Deserialize<DispatchJob>(File.ReadAllText(path)))
            .OfType<DispatchJob>()
            .Single();
        Equal(DispatchJobStatus.Failed, status.Status);
        True(status.Error?.Contains("audit", StringComparison.OrdinalIgnoreCase) == true);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void OwnerDaemonRebuildsTransportWhenHopChanges()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-owner-hop-switch-{Guid.NewGuid():N}");
    try
    {
        var registry = new OwnerRegistry(Path.Combine(root, "owners"));
        var created = new List<HopRecordingTransport>();
        var initial = new HopRecordingTransport(1);
        using var daemon = new OwnerDaemon(
            "codex",
            "D:\\code\\app",
            "thread-1",
            initial,
            registry,
            transportFactory: hop =>
            {
                var next = new HopRecordingTransport(hop);
                created.Add(next);
                return next;
            },
            transportHop: 1);
        var registration = daemon.Start();
        var spool = new OwnerSpool(registration.SpoolDir);
        spool.Post(new OwnerMessage("h1", "claude", null, "one", DateTimeOffset.UtcNow, HopCount: 1));
        Equal(1, daemon.ProcessPendingOnce());
        Equal(0, created.Count);
        spool.Post(new OwnerMessage("h2", "claude", null, "two", DateTimeOffset.UtcNow.AddMilliseconds(1), HopCount: 2));
        Equal(1, daemon.ProcessPendingOnce());
        Equal(1, created.Count);
        Equal(2, created[0].Hop);
        Equal(1, initial.Turns.Count);
        Equal(1, created[0].Turns.Count);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ClaudeChildModeDefaultsToTextOnly()
{
    var previous = Environment.GetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE");
    try
    {
        Environment.SetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE", null);
        var command = ProviderCommand.BuildClaude(
            DispatchAction.Create,
            null,
            "D:\\code\\app",
            "D:\\tmp\\prompt.txt",
            "claude.exe",
            "11111111-1111-4111-8111-111111111111");
        True(command.Arguments.Contains("--tools="));
        True(command.Arguments.Contains(ProviderCommand.ClaudeTextOnlySystemPrompt));
        True(command.Arguments.Contains("--safe-mode"));
        True(command.Arguments.Contains("--strict-mcp-config"));
        True(command.Arguments.Contains("--setting-sources="));
    }
    finally
    {
        Environment.SetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE", previous);
    }
}

static void ClaudeChildToolsModeAllowsWebWithoutMcp()
{
    var previous = Environment.GetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE");
    try
    {
        Environment.SetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE", "tools");
        var command = ProviderCommand.BuildClaude(
            DispatchAction.Create,
            null,
            "D:\\code\\app",
            "D:\\tmp\\prompt.txt",
            "claude.exe",
            "11111111-1111-4111-8111-111111111111");
        var toolsIndex = IndexOf(command.Arguments, "--tools");
        True(toolsIndex >= 0);
        Equal("WebSearch,WebFetch", command.Arguments[toolsIndex + 1]);
        True(!command.Arguments.Contains("--tools="));
        True(!command.Arguments.Contains("--mcp-config"));
        True(command.Arguments.Contains("--safe-mode"));
        True(command.Arguments.Contains("--strict-mcp-config"));
        True(command.Arguments.Contains("--setting-sources="));
        True(command.Arguments.Contains("--disable-slash-commands"));
        True(!command.Arguments.Contains(ProviderCommand.ClaudeTextOnlySystemPrompt));
    }
    finally
    {
        Environment.SetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE", previous);
    }
}

static void GrokResumeReadBackPasses()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-readback-ok-{Guid.NewGuid():N}");
    var count = 4;
    var launcher = new CallbackProcessLauncher("GROK-DONE", () => count = 5);
    var runner = new DispatchRunner(
        new DispatchJobStore(Path.Combine(root, "jobs")),
        _ =>
        [
            new Peer("grok", "idle", PeerStatus.Resumable, "D:\\code\\app", "Idle", null, null, null, null, null, count)
        ],
        launcher,
        owners: new OwnerRegistry(Path.Combine(root, "owners")),
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        readBackTimeout: TimeSpan.FromSeconds(2));
    var job = runner.Enqueue("grok", "hello", "idle", "D:\\code\\app", allowNew: false);
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Succeeded, done.Status);
    Equal(4, done.PeerTurnsBefore);
    Equal(5, done.PeerTurnsAfter);
}

static void GrokResumeReadBackFails()
{
    var root = Path.Combine(Path.GetTempPath(), $"cccg-readback-bad-{Guid.NewGuid():N}");
    var runner = new DispatchRunner(
        new DispatchJobStore(Path.Combine(root, "jobs")),
        _ =>
        [
            new Peer("grok", "idle", PeerStatus.Resumable, "D:\\code\\app", "Idle", null, null, null, null, null, 4)
        ],
        new FakeProcessLauncher("GROK-DONE"),
        owners: new OwnerRegistry(Path.Combine(root, "owners")),
        writerPollInterval: TimeSpan.FromMilliseconds(20),
        readBackTimeout: TimeSpan.FromMilliseconds(200));
    var job = runner.Enqueue("grok", "hello", "idle", "D:\\code\\app", allowNew: false);
    var done = runner.Run(job.JobId);
    Equal(DispatchJobStatus.Failed, done.Status);
    True(done.Error?.Contains("was not recorded", StringComparison.Ordinal) == true);
    Equal(4, done.PeerTurnsBefore);
    Equal(4, done.PeerTurnsAfter);
}

static T Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class GrokHomeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"cccg-grok-{Guid.NewGuid():N}");

    private readonly List<string> activeRows = new();

    public GrokHomeFixture() => System.IO.Directory.CreateDirectory(Root);

    public GrokPeerDirectory Open(Func<int, bool> alive, DateTimeOffset now) =>
        new(Root, alive, () => now, TimeSpan.FromSeconds(90));

    public void WriteActive(string sessionId, int pid, string cwd, DateTimeOffset openedAt)
    {
        activeRows.Add(
            $"{{\"session_id\":\"{sessionId}\",\"pid\":{pid},\"cwd\":{System.Text.Json.JsonSerializer.Serialize(cwd)},\"opened_at\":\"{openedAt:O}\"}}");
        File.WriteAllText(Path.Combine(Root, "active_sessions.json"), "[" + string.Join(",", activeRows) + "]");
    }

    public void WriteSummary(
        string cwd,
        string sessionId,
        string title,
        string lastTurn,
        DateTimeOffset updated)
    {
        var group = Path.Combine(Root, "sessions", Uri.EscapeDataString(cwd), sessionId);
        System.IO.Directory.CreateDirectory(group);
        File.WriteAllText(Path.Combine(group, "summary.json"), $$"""
            {
              "info": { "id": "{{sessionId}}", "cwd": {{System.Text.Json.JsonSerializer.Serialize(cwd)}} },
              "generated_title": {{System.Text.Json.JsonSerializer.Serialize(title)}},
              "last_turn_summary": {{System.Text.Json.JsonSerializer.Serialize(lastTurn)}},
              "current_model_id": "grok-4.6",
              "created_at": "{{updated:O}}",
              "updated_at": "{{updated:O}}",
              "last_active_at": "{{updated:O}}",
              "num_messages": 4
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

sealed class CodexHomeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"cccg-codex-{Guid.NewGuid():N}");

    public CodexHomeFixture() => System.IO.Directory.CreateDirectory(Root);

    public CodexPeerDirectory Open(Func<string, bool> lockHeld, DateTimeOffset now) =>
        new(Root, lockHeld, () => now, TimeSpan.FromSeconds(90));

    public string LockPath(string sessionId) =>
        Path.Combine(Root, "thread-writer-locks", sessionId + ".lock");

    public void WriteIndex(string sessionId, string title)
    {
        File.AppendAllText(
            Path.Combine(Root, "session_index.jsonl"),
            $"{{\"id\":\"{sessionId}\",\"thread_name\":{System.Text.Json.JsonSerializer.Serialize(title)},\"updated_at\":\"2026-08-13T03:00:00Z\"}}\n");
    }

    public void WriteRollout(string sessionId, string cwd, DateTimeOffset updated, bool subagent)
    {
        var day = Path.Combine(Root, "sessions", "2026", "08", "13");
        System.IO.Directory.CreateDirectory(day);
        var path = Path.Combine(day, $"rollout-2026-08-13T00-00-00-{sessionId}.jsonl");
        var source = subagent ? "subagent" : "interactive";
        var cwdJson = System.Text.Json.JsonSerializer.Serialize(cwd);
        File.WriteAllText(
            path,
            "{\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + sessionId +
            "\",\"id\":\"" + sessionId + "\",\"cwd\":" + cwdJson +
            ",\"thread_source\":\"" + source + "\",\"timestamp\":\"" + updated.ToString("O") +
            "\",\"model_provider\":\"openai\"}}\n" +
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"SECRET USER PROMPT\"}}\n");
        File.SetLastWriteTimeUtc(path, updated.UtcDateTime);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

sealed class ClaudeHomeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"cccg-claude-{Guid.NewGuid():N}");

    public ClaudeHomeFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "projects"));
        Directory.CreateDirectory(Path.Combine(Root, "sessions"));
    }

    public void WriteTranscript(string sessionId, string cwd, string title, string model)
    {
        var project = Path.Combine(
            Root,
            "projects",
            Path.GetFullPath(cwd).TrimEnd('\\').Replace(':', '-').Replace('\\', '-'));
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, sessionId + ".jsonl"),
            "{\"type\":\"user\",\"sessionId\":\"" + sessionId
            + "\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(cwd)
            + ",\"timestamp\":\"2026-08-13T03:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"SECRET\"}}\n"
            + "{\"type\":\"custom-title\",\"customTitle\":"
            + System.Text.Json.JsonSerializer.Serialize(title) + ",\"sessionId\":\"" + sessionId + "\"}\n"
            + "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId
            + "\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(cwd)
            + ",\"message\":{\"role\":\"assistant\",\"model\":"
            + System.Text.Json.JsonSerializer.Serialize(model) + ",\"content\":[]}}\n");
    }

    public void WriteActive(string sessionId, int pid, string cwd, string name)
    {
        File.WriteAllText(
            Path.Combine(Root, "sessions", pid + ".json"),
            "{\"pid\":" + pid + ",\"sessionId\":\"" + sessionId
            + "\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(cwd)
            + ",\"startedAt\":1786600000000,\"updatedAt\":1786600100000,\"name\":"
            + System.Text.Json.JsonSerializer.Serialize(name) + "}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

sealed class ClaudeEchoProcessLauncher : IProcessLauncher
{
    public int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        var idIndex = command.Arguments
            .Select((argument, index) => (argument, index))
            .FirstOrDefault(item => item.argument == "--session-id")
            .index;
        var sessionId = idIndex >= 0 ? command.Arguments[idIndex + 1] : "missing";
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        File.WriteAllText(
            stdoutPath,
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sessionId + "\"}\n"
            + "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"" + sessionId
            + "\",\"result\":\"CLAUDE-BOUND-OK\"}");
        File.WriteAllText(stderrPath, string.Empty);
        waiter = new ClaudeImmediateWaiter();
        return 4343;
    }

    private sealed class ClaudeImmediateWaiter : IProcessWaiter
    {
        public int? Pid => 4343;

        public int Wait(TimeSpan timeout) => 0;
    }
}

sealed class CountingProcessLauncher : FakeProcessLauncher
{
    public int Starts { get; private set; }

    public CountingProcessLauncher(string stdout) : base(stdout)
    {
    }

    public override int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        Starts++;
        return base.Start(command, stdoutPath, stderrPath, stdinPath, out waiter);
    }
}

sealed class RecordingProcessLauncher : FakeProcessLauncher
{
    public RecordingProcessLauncher(string stdout) : base(stdout)
    {
    }

    public LaunchCommand? LastCommand { get; private set; }

    public override int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        LastCommand = command;
        return base.Start(command, stdoutPath, stderrPath, stdinPath, out waiter);
    }
}

class FakeProcessLauncher : IProcessLauncher
{
    private readonly string stdout;

    public FakeProcessLauncher(string stdout) => this.stdout = stdout;

    public virtual int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, "");
        waiter = new ImmediateWaiter();
        return 4242;
    }

    private sealed class ImmediateWaiter : IProcessWaiter
    {
        public int? Pid => 4242;

        public int Wait(TimeSpan timeout) => 0;
    }
}

sealed class TrackingProcessLauncher : IProcessLauncher
{
    private int active;
    private int maxConcurrent;
    private int pid = 5000;

    public int MaxConcurrent => maxConcurrent;

    public int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        File.WriteAllText(stdoutPath, "SERIAL-OK");
        File.WriteAllText(stderrPath, "");
        var current = Interlocked.Increment(ref active);
        UpdateMaximum(current);
        waiter = new DelayedWaiter(() => Interlocked.Decrement(ref active));
        return Interlocked.Increment(ref pid);
    }

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maxConcurrent);
            if (observed >= value
                || Interlocked.CompareExchange(ref maxConcurrent, value, observed) == observed)
            {
                return;
            }
        }
    }

    private sealed class DelayedWaiter : IProcessWaiter
    {
        private readonly Action completed;

        public DelayedWaiter(Action completed) => this.completed = completed;

        public int? Pid => null;

        public int Wait(TimeSpan timeout)
        {
            Thread.Sleep(100);
            completed();
            return 0;
        }
    }
}

sealed class EchoTurnTransport : IProviderTurnTransport
{
    private readonly Func<string, string> reply;
    private readonly string? sessionId;

    public EchoTurnTransport(Func<string, string> reply, string? sessionId = null)
    {
        this.reply = reply;
        this.sessionId = sessionId;
    }

    public List<string> Turns { get; } = new();

    public string? LastModel { get; private set; }

    public string? LastReasoningEffort { get; private set; }

    public string? SessionId => sessionId;

    public Task<string> RunTurnAsync(
        string text,
        CancellationToken cancellationToken = default,
        string? model = null,
        string? reasoningEffort = null)
    {
        Turns.Add(text);
        LastModel = model;
        LastReasoningEffort = reasoningEffort;
        return Task.FromResult(reply(text));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class HopRecordingTransport : IProviderTurnTransport
{
    public HopRecordingTransport(int hop) => Hop = hop;

    public int Hop { get; }

    public List<string> Turns { get; } = new();

    public string? SessionId => "thread-1";

    public Task<string> RunTurnAsync(
        string text,
        CancellationToken cancellationToken = default,
        string? model = null,
        string? reasoningEffort = null)
    {
        Turns.Add(text);
        return Task.FromResult("reply");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class BackgroundOwner : IDisposable
{
    private readonly OwnerDaemon daemon;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;

    public BackgroundOwner(OwnerDaemon daemon)
    {
        this.daemon = daemon;
        Registration = daemon.Start();
        thread = new Thread(() =>
        {
            try
            {
                daemon.Run(stop.Token);
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
    }

    public OwnerRegistration Registration { get; }

    public void Dispose()
    {
        stop.Cancel();
        thread.Join(TimeSpan.FromSeconds(2));
        daemon.Dispose();
        stop.Dispose();
    }
}

sealed class CallbackProcessLauncher : FakeProcessLauncher
{
    private readonly Action onStart;

    public CallbackProcessLauncher(string stdout, Action onStart) : base(stdout) =>
        this.onStart = onStart;

    public override int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        var pid = base.Start(command, stdoutPath, stderrPath, stdinPath, out waiter);
        onStart();
        return pid;
    }
}

sealed class ScriptedJsonLineTransport : IJsonLineTransport
{
    private readonly Queue<string> responses;

    public ScriptedJsonLineTransport(IEnumerable<string> responses)
    {
        this.responses = new Queue<string>(responses);
    }

    public List<string> Writes { get; } = new();

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        Writes.Add(line);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(responses.Count == 0 ? null : responses.Dequeue());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
