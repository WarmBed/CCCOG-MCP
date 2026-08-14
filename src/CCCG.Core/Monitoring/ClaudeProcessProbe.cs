using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CCCG.Core.Monitoring;

public sealed record ClaudeProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? Path,
    string Role,
    DateTimeOffset? StartedAt);

public static class ClaudeProcessProbe
{
    public static IReadOnlyList<ClaudeProcessSnapshot> Snapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<ClaudeProcessSnapshot>();
        }

        var parents = ReadParentMap();
        var result = new List<ClaudeProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!process.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path = null;
                DateTimeOffset? started = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                try
                {
                    started = process.StartTime;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                result.Add(new ClaudeProcessSnapshot(
                    process.Id,
                    parents.GetValueOrDefault(process.Id),
                    process.ProcessName,
                    path,
                    Classify(path, process.ProcessName),
                    started));
            }
        }

        return result;
    }

    private static string Classify(string? path, string name)
    {
        if (name.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "engine-backend";
        }

        if (path?.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "desktop";
        }

        if (path?.Contains("claude-code", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "engine-entry";
        }

        return "claude-process";
    }

    private static Dictionary<int, int> ReadParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
