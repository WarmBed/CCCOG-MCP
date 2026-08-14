namespace CCCG.Core;

public sealed record ClaudeArguments(
    string? Model,
    string? PermissionMode,
    string? ConfigPath,
    string? ResumeSessionId,
    string? SessionId,
    IReadOnlyList<string> ForwardedArguments)
{
    public static ClaudeArguments Parse(IReadOnlyList<string> args)
    {
        string? model = null;
        string? permissionMode = null;
        string? configPath = null;
        string? resumeSessionId = null;
        string? sessionId = null;
        var forwarded = new List<string>(args.Count);

        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];

            if (TryReadValue(args, ref index, value, "--model", out var modelValue))
            {
                model = modelValue;
                forwarded.Add(value);
                if (!value.Contains('='))
                {
                    forwarded.Add(modelValue);
                }

                continue;
            }

            if (TryReadValue(
                    args,
                    ref index,
                    value,
                    "--permission-mode",
                    out var permissionValue))
            {
                permissionMode = permissionValue;
                forwarded.Add(value);
                if (!value.Contains('='))
                {
                    forwarded.Add(permissionValue);
                }

                continue;
            }

            if (TryReadValue(args, ref index, value, "--cccg-config", out var configValue))
            {
                configPath = configValue;
                continue;
            }

            if (TryReadValue(args, ref index, value, "--resume", out var resumeValue))
            {
                resumeSessionId = resumeValue;
                forwarded.Add(value);
                if (!value.Contains('='))
                {
                    forwarded.Add(resumeValue);
                }

                continue;
            }

            if (TryReadValue(args, ref index, value, "--session-id", out var sessionValue))
            {
                sessionId = sessionValue;
                forwarded.Add(value);
                if (!value.Contains('='))
                {
                    forwarded.Add(sessionValue);
                }

                continue;
            }

            forwarded.Add(value);
        }

        configPath ??= Environment.GetEnvironmentVariable("CCCG_CONFIG_PATH");
        return new ClaudeArguments(
            model,
            permissionMode,
            configPath,
            resumeSessionId,
            sessionId,
            forwarded);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string current,
        string option,
        out string value)
    {
        var prefix = option + "=";
        if (current.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = current[prefix.Length..];
            return !string.IsNullOrWhiteSpace(value);
        }

        if (string.Equals(current, option, StringComparison.Ordinal)
            && index + 1 < args.Count)
        {
            value = args[++index];
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }
}
