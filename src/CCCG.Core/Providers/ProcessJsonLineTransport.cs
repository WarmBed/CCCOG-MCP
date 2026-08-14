using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CCCG.Core.Providers;

public sealed partial class ProcessJsonLineTransport : IJsonLineTransport
{
    private readonly Process process;
    private readonly Task stderrPump;
    private bool disposed;

    private ProcessJsonLineTransport(Process process, Action<string>? diagnostic)
    {
        this.process = process;
        stderrPump = PumpStderrAsync(process.StandardError, diagnostic);
    }

    public static Task<IJsonLineTransport> StartCodexAsync(
        Action<string>? diagnostic = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executable = Environment.GetEnvironmentVariable("CCCG_CODEX_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = OperatingSystem.IsWindows() ? "codex.cmd" : "codex";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the Codex app-server process.");
        }

        process.StandardInput.AutoFlush = true;
        return Task.FromResult<IJsonLineTransport>(
            new ProcessJsonLineTransport(process, diagnostic));
    }

    public async ValueTask WriteLineAsync(
        string line,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string?> ReadLineAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return await process.StandardOutput.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree();
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the stream close and wait.
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKillProcessTree();
            }

            try
            {
                await stderrPump.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The stream can close while the process is being terminated.
            }

            process.Dispose();
        }
    }

    private static async Task PumpStderrAsync(
        StreamReader reader,
        Action<string>? diagnostic)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            diagnostic?.Invoke(RedactDiagnostic(line));
        }
    }

    private static string RedactDiagnostic(string line) =>
        SensitiveValueRegex().Replace(line, "$1=<redacted>");

    private void TryKillProcessTree()
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    [GeneratedRegex(
        "(?i)(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|bearer)\\s*[:=]\\s*\\\"?[^\\s\\\",}]+")]
    private static partial Regex SensitiveValueRegex();
}
