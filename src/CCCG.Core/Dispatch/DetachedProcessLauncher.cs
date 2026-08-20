using System.Runtime.InteropServices;
using System.Text;

namespace CCCG.Core.Dispatch;

/// <summary>
/// Launches a process with CREATE_BREAKAWAY_FROM_JOB so it escapes whatever
/// Windows Job Object (if any) its parent's ancestry belongs to, instead of
/// silently inheriting membership the way a plain
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// child does.
///
/// Root cause this exists for: a detached "run-job" dispatch worker (see
/// CCCG.Dispatch.Worker's StartDetachedJob) is meant to survive the MCP Host
/// that spawned it. On this machine every live cccg-dispatch.exe Host is
/// itself a member of a Windows Job Object (confirmed via IsProcessInJob),
/// almost certainly inherited from its own launching terminal/IDE/session.
/// A plain child process is automatically enrolled in that same job unless
/// it explicitly breaks away, and a kill-on-close job cascades a
/// TerminateProcess to every member the instant the last handle to it
/// closes -- including a "detached" grandchild several process levels down.
/// That kill produces no exception, no WER crash report, and no trace in
/// the Application/System event logs; the job simply vanishes mid-flight.
/// Reproduced directly: a plain Process.Start grandchild died the instant a
/// job containing only its immediate parent was terminated, even though the
/// grandchild was never itself assigned to that job.
///
/// Strictly best-effort: breakaway only succeeds when the ambient job was
/// created with JOB_OBJECT_LIMIT_BREAKAWAY_OK or
/// JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK. Every other outcome (no ambient
/// job, breakaway disallowed, any Win32 failure, non-Windows) returns null
/// so the caller can fall back to its previous unconditional launch path.
/// </summary>
public static class DetachedProcessLauncher
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;

    public static int? TryStartBreakaway(string executable, params string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var startupInfo = new STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();
            var commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
            // CreateProcessW may write into this buffer in place; keep slack.
            commandLine.EnsureCapacity(commandLine.Length + 16);

            var created = CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: CreateNoWindow | CreateBreakawayFromJob,
                lpEnvironment: IntPtr.Zero, // NULL => inherit the caller's environment block.
                lpCurrentDirectory: null,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var processInformation);
            if (!created)
            {
                return null;
            }

            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            return processInformation.dwProcessId;
        }
        catch
        {
            // Never let a best-effort escape hatch take down dispatch.
            return null;
        }
    }

    private static string BuildCommandLine(string executable, string[] arguments)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        // Minimal Win32 argv quoting: double embedded quotes; backslashes
        // only need escaping when they immediately precede the closing quote.
        var result = new StringBuilder();
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
                result.Append('"');
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
