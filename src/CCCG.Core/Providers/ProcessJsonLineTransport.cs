using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CCCG.Core.Providers;

public sealed partial class ProcessJsonLineTransport : IJsonLineTransport
{
    private readonly Process process;
    private readonly Task stderrPump;
    private readonly IntPtr jobHandle;
    private bool disposed;

    private ProcessJsonLineTransport(Process process, Action<string>? diagnostic, IntPtr jobHandle)
    {
        this.process = process;
        this.jobHandle = jobHandle;
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

        // Explicit UTF-8 on every redirected stream: without this the console
        // codepage (cp950 on zh-TW Windows) garbles Chinese text both ways.
        // Mirrors the 0.4.6 FileProcessLauncher fix in DispatchRunner.cs.
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
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
        // Best-effort kill-on-close job object: if the owner process is
        // hard-killed, the kernel closes the job handle and terminates the
        // codex child instead of leaving an orphan holding the session.
        var jobHandle = KillOnCloseJob.TryAssign(process, diagnostic);
        return Task.FromResult<IJsonLineTransport>(
            new ProcessJsonLineTransport(process, diagnostic, jobHandle));
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
            // Closed last: the process has already exited (or been killed
            // above), so kill-on-job-close is a no-op here; the handle exists
            // for the hard-kill-of-the-owner case.
            KillOnCloseJob.Close(jobHandle);
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

    /// <summary>
    /// Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE around the
    /// provider child: if the owner process is hard-killed, the kernel closes
    /// its handles — including the job — and terminates the child. Strictly
    /// best-effort: any failure logs a diagnostic and the transport continues
    /// without the protection.
    /// </summary>
    private static class KillOnCloseJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        public static IntPtr TryAssign(Process process, Action<string>? diagnostic)
        {
            if (!OperatingSystem.IsWindows())
            {
                return IntPtr.Zero;
            }

            var job = IntPtr.Zero;
            try
            {
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    Report(diagnostic, "CreateJobObject", Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }

                var information = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
                information.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                    if (!SetInformationJobObject(
                            job,
                            JobObjectExtendedLimitInformation,
                            buffer,
                            (uint)length)
                        || !AssignProcessToJobObject(job, process.Handle))
                    {
                        Report(diagnostic, "Set/AssignJobObject", Marshal.GetLastWin32Error());
                        Close(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return job;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
            {
                diagnostic?.Invoke(
                    "[cccg] kill-on-close job object not applied ("
                    + exception.GetType().Name + "); continuing without it.");
                Close(job);
                return IntPtr.Zero;
            }
        }

        public static void Close(IntPtr job)
        {
            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
        }

        private static void Report(Action<string>? diagnostic, string stage, int error) =>
            diagnostic?.Invoke(
                $"[cccg] kill-on-close job object not applied ({stage} error {error}); "
                + "continuing without it.");

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
