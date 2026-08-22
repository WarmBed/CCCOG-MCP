using System.Globalization;

namespace CCCG.Core.Dispatch;

public static class ProviderCommand
{
    /// <summary>
    /// Operator escape hatch for the grok CLI's --max-turns cap (see
    /// <see cref="ResolveGrokMaxTurns"/>), mirroring
    /// DispatchRunner.JobTimeoutEnvVariable's defensive-parse pattern.
    /// </summary>
    public const string GrokMaxTurnsEnvVariable = "CCCG_GROK_MAX_TURNS";

    /// <summary>
    /// Default --max-turns for non-interactive grok runs. Without
    /// --always-approve + a turn budget, a headless grok agent that needs
    /// to run any tool (curl, fetch, file read) has nothing to auto-approve
    /// its request and the turn ends after its first message ("wakes then
    /// immediately sleeps": stopReason "cancelled", num_turns 1). 30 turns
    /// is generous for a bounded read-only task while still eventually
    /// stopping a runaway tool-call loop instead of spinning forever.
    /// </summary>
    public const int DefaultGrokMaxTurns = 30;

    public const string ClaudeTextOnlySystemPrompt =
        "You are a CCCG text-only peer. Answer only the task in the user message. "
        + "Never use, describe, simulate, or request tools. Never read or write files, memory, settings, network, or external systems. "
        + "Do not delegate. Return only the final textual answer.";

    public const string ClaudeToolsSystemPrompt =
        "You are a CCCG tool-enabled peer. Answer only the task in the user message. "
        + "Use only the explicitly enabled built-in WebSearch and WebFetch tools when they materially help. "
        + "Never use MCP, hooks, project commands, slash commands, or delegation. Return only the final textual answer.";

    public static LaunchCommand Build(
        string provider,
        DispatchAction action,
        string? sessionId,
        string? cwd,
        string promptPath,
        string? grokHome = null,
        string? codexCommand = null,
        string? claudeCommand = null,
        string? model = null,
        string? reasoningEffort = null)
    {
        var work = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        return provider.ToLowerInvariant() switch
        {
            "codex" => BuildCodex(
                action, sessionId, work, promptPath, codexCommand, model, reasoningEffort),
            "claude" => BuildClaude(action, sessionId, work, promptPath, claudeCommand),
            _ => BuildGrok(
                action,
                sessionId,
                work,
                promptPath,
                grokHome,
                model: model,
                reasoningEffort: reasoningEffort)
        };
    }

    public static LaunchCommand BuildGrok(
        DispatchAction action,
        string? sessionId,
        string cwd,
        string promptPath,
        string? grokHome,
        string? newSessionId = null,
        string? model = null,
        string? reasoningEffort = null)
    {
        var file = ResolveGrok(grokHome);
        var args = new List<string>
        {
            "--prompt-file", promptPath,
            "--output-format", "json",
            "--cwd", cwd,
            "--permission-mode", "acceptEdits",
            "--no-auto-update",
            // --permission-mode acceptEdits only covers file edits; a
            // headless run has no TTY to answer an approval prompt for any
            // other tool (shell, curl/fetch, ...), so without an explicit
            // auto-approve the agent stops after its first message the
            // moment it needs to run one. --max-turns is the matching
            // safety bound so an auto-approved agent can't loop forever.
            "--always-approve",
            "--max-turns", ResolveGrokMaxTurns().ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            args.Add("--reasoning-effort");
            args.Add(reasoningEffort);
        }

        if (action != DispatchAction.Create)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException("Grok resume/attach requires a sessionId.");
            }

            args.Add("-r");
            args.Add(sessionId);
        }
        else if (!string.IsNullOrWhiteSpace(newSessionId))
        {
            args.Add("--session-id");
            args.Add(newSessionId);
        }

        return new LaunchCommand(file, args, cwd);
    }

    /// <summary>
    /// Reads <see cref="GrokMaxTurnsEnvVariable"/> and falls back to
    /// <see cref="DefaultGrokMaxTurns"/> for anything unset, unparsable, or
    /// non-positive -- a malformed operator override must never crash
    /// dispatch or hand the CLI a zero/negative turn budget.
    /// </summary>
    public static int ResolveGrokMaxTurns(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var raw = getEnvironmentVariable(GrokMaxTurnsEnvVariable);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var turns)
            && turns > 0)
        {
            return turns;
        }

        return DefaultGrokMaxTurns;
    }

    public static LaunchCommand BuildCodex(
        DispatchAction action,
        string? sessionId,
        string cwd,
        string promptPath,
        string? codexCommand,
        string? model = null,
        string? reasoningEffort = null)
    {
        var file = string.IsNullOrWhiteSpace(codexCommand)
            ? ResolveCodex()
            : codexCommand;
        var args = new List<string> { "exec", "--json" };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            args.Add("-c");
            args.Add("model_reasoning_effort=" + reasoningEffort);
        }

        if (action == DispatchAction.Create)
        {
            args.AddRange(new[] { "--skip-git-repo-check", "-" });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException("Codex resume/attach requires a sessionId.");
            }

            args.AddRange(new[]
            {
                "resume", "--skip-git-repo-check", sessionId, "-"
            });
        }

        return new LaunchCommand(file, args, cwd);
    }

    public static LaunchCommand BuildClaude(
        DispatchAction action,
        string? sessionId,
        string cwd,
        string promptPath,
        string? claudeCommand,
        string? newSessionId = null)
    {
        var file = string.IsNullOrWhiteSpace(claudeCommand)
            ? ResolveClaude()
            : claudeCommand;
        var childMode = Environment.GetEnvironmentVariable("CCCG_CLAUDE_CHILD_MODE")
            ?.Trim()
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(childMode))
        {
            childMode = "text-only";
        }

        if (childMode is not ("text-only" or "tools"))
        {
            throw new InvalidOperationException(
                "CCCG_CLAUDE_CHILD_MODE must be text-only or tools.");
        }

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--safe-mode",
            "--disable-slash-commands",
            "--system-prompt",
            childMode == "tools" ? ClaudeToolsSystemPrompt : ClaudeTextOnlySystemPrompt,
            "--strict-mcp-config",
            "--setting-sources=",
            "--permission-mode", "dontAsk"
        };
        if (childMode == "tools")
        {
            args.Insert(8, "WebSearch,WebFetch");
            args.Insert(8, "--tools");
            args.Insert(10, "WebSearch,WebFetch");
            args.Insert(10, "--allowed-tools");
        }
        else
        {
            args.Insert(8, "--tools=");
        }
        if (action == DispatchAction.Create)
        {
            if (string.IsNullOrWhiteSpace(newSessionId))
            {
                throw new InvalidOperationException("Claude create requires a preassigned session id.");
            }

            args.Add("--session-id");
            args.Add(newSessionId);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException("Claude resume/attach requires a sessionId.");
            }

            args.Add("--resume");
            args.Add(sessionId);
        }

        return new LaunchCommand(file, args, cwd);
    }

    public static string ResolveGrok(string? grokHome)
    {
        var home = string.IsNullOrWhiteSpace(grokHome)
            ? GrokHome.Resolve().Path
            : grokHome;
        var candidate = Path.Combine(home, "bin", "grok.exe");
        return File.Exists(candidate) ? candidate : "grok";
    }

    public static string ResolveCodex()
    {
        if (OperatingSystem.IsWindows())
        {
            var packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "node_modules",
                "@openai",
                "codex",
                "node_modules");
            if (Directory.Exists(packageRoot))
            {
                var native = Directory.EnumerateFiles(
                        packageRoot,
                        "codex.exe",
                        SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Contains(
                        $"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(native))
                {
                    return native;
                }
            }
        }

        var cmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");
        return File.Exists(cmd) ? cmd : "codex";
    }

    public static string ResolveClaude()
    {
        var configured = Environment.GetEnvironmentVariable("CCCG_CLAUDE_COMMAND");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            var desktopRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "Claude_pzs8sxrjxfjjc",
                "LocalCache",
                "Roaming",
                "Claude",
                "claude-code");
            if (Directory.Exists(desktopRoot))
            {
                var official = Directory.EnumerateFiles(
                        desktopRoot,
                        "claude.anthropic-*.exe",
                        SearchOption.AllDirectories)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(official))
                {
                    return official;
                }
            }

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "bin",
                "claude.exe");
            if (File.Exists(local))
            {
                return local;
            }

            var npm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "claude.cmd");
            if (File.Exists(npm))
            {
                return npm;
            }
        }

        return "claude";
    }
}
