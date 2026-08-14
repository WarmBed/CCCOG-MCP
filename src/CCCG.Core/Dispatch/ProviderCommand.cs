namespace CCCG.Core.Dispatch;

public static class ProviderCommand
{
    public const string ClaudeTextOnlySystemPrompt =
        "You are a CCCG text-only peer. Answer only the task in the user message. "
        + "Never use, describe, simulate, or request tools. Never read or write files, memory, settings, network, or external systems. "
        + "Do not delegate. Return only the final textual answer.";

    public static LaunchCommand Build(
        string provider,
        DispatchAction action,
        string? sessionId,
        string? cwd,
        string promptPath,
        string? grokHome = null,
        string? codexCommand = null,
        string? claudeCommand = null)
    {
        var work = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        return provider.ToLowerInvariant() switch
        {
            "codex" => BuildCodex(action, sessionId, work, promptPath, codexCommand),
            "claude" => BuildClaude(action, sessionId, work, promptPath, claudeCommand),
            _ => BuildGrok(action, sessionId, work, promptPath, grokHome)
        };
    }

    public static LaunchCommand BuildGrok(
        DispatchAction action,
        string? sessionId,
        string cwd,
        string promptPath,
        string? grokHome,
        string? newSessionId = null)
    {
        var file = ResolveGrok(grokHome);
        var args = new List<string>
        {
            "--prompt-file", promptPath,
            "--output-format", "json",
            "--cwd", cwd,
            "--permission-mode", "acceptEdits",
            "--no-auto-update"
        };
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

    public static LaunchCommand BuildCodex(
        DispatchAction action,
        string? sessionId,
        string cwd,
        string promptPath,
        string? codexCommand)
    {
        var file = string.IsNullOrWhiteSpace(codexCommand)
            ? ResolveCodex()
            : codexCommand;
        var args = new List<string> { "exec", "--json" };
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
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--safe-mode",
            "--disable-slash-commands",
            "--system-prompt", ClaudeTextOnlySystemPrompt,
            "--tools=",
            "--strict-mcp-config",
            "--setting-sources=",
            "--permission-mode", "dontAsk"
        };
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
