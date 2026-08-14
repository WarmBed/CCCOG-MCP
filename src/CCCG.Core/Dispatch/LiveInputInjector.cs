using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CCCG.Core.Dispatch;

public interface ILiveInputInjector
{
    void Inject(int processId, string text);
}

public sealed class RecordingLiveInputInjector : ILiveInputInjector
{
    public readonly List<(int ProcessId, string Text)> Calls = [];

    public void Inject(int processId, string text)
    {
        Calls.Add((processId, text));
    }
}

public sealed class WindowsLiveInputInjector : ILiveInputInjector
{
    public const ushort KeyEvent = 1;
    public const ushort VkReturn = 0x0D;
    public const ushort VkControl = 0x11;
    public const ushort VkI = 0x49;
    public const string WriteConsoleInputEntryPoint = "WriteConsoleInputW";

    public void Inject(int processId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (processId <= 0)
        {
            throw new InvalidOperationException("Live inject requires a process id.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Live window inject is implemented for Windows.");
        }

        var records = BuildKeyEvents(text);
        var seen = new HashSet<int>();
        var notes = new List<string>();
        foreach (var candidate in EnumerateProcessChain(processId))
        {
            seen.Add(candidate);
        }

        foreach (var candidate in seen)
        {
            if (TryTypeIntoWindow(candidate, text, notes))
            {
                return;
            }
        }

        foreach (var candidate in seen)
        {
            if (TryWriteConsole(candidate, records, notes))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not type into live process {processId}. "
            + string.Join(" ", notes));
    }

    public static string FlattenForTyping(string text)
    {
        var collapsed = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ');
        while (collapsed.Contains("  ", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return collapsed.Trim();
    }

    public static INPUT_RECORD[] BuildKeyEvents(string text)
    {
        var normalized = FlattenForTyping(text);
        var records = new List<INPUT_RECORD>(normalized.Length * 2 + 4);
        foreach (var ch in normalized)
        {
            records.Add(Key(ch, down: true));
            records.Add(Key(ch, down: false));
        }

        records.Add(Key('\r', down: true, VkReturn));
        records.Add(Key('\r', down: false, VkReturn));
        return records.ToArray();
    }

    private static INPUT_RECORD Key(char ch, bool down, ushort? virtualKey = null) => new()
    {
        EventType = KeyEvent,
        KeyEvent = new KEY_EVENT_RECORD
        {
            bKeyDown = down ? 1 : 0,
            wRepeatCount = 1,
            wVirtualKeyCode = virtualKey ?? 0,
            wVirtualScanCode = 0,
            UnicodeChar = ch,
            dwControlKeyState = 0
        }
    };

    private static bool TryWriteConsole(int processId, INPUT_RECORD[] records, List<string> notes)
    {
        var attached = Native.AttachConsole((uint)processId);
        var attachErr = Marshal.GetLastWin32Error();
        if (!attached)
        {
            Native.FreeConsole();
            attached = Native.AttachConsole((uint)processId);
            attachErr = Marshal.GetLastWin32Error();
        }

        var alreadyOnConsole = attachErr == Native.ErrorAccessDenied || ConsoleHasProcess(processId);
        if (!attached && !alreadyOnConsole)
        {
            notes.Add($"AttachConsole({processId}) failed win32={attachErr}.");
            return false;
        }

        var input = Native.CreateFile(
            "CONIN$",
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero,
            Native.OpenExisting,
            0,
            IntPtr.Zero);
        try
        {
            if (input == Native.InvalidHandle || input == IntPtr.Zero)
            {
                notes.Add($"CONIN$({processId}) failed win32={Marshal.GetLastWin32Error()}.");
                return false;
            }

            var previousCp = Native.GetConsoleCP();
            Native.SetConsoleCP(65001);
            Native.SetConsoleOutputCP(65001);
            try
            {
                var wrote = Native.WriteConsoleInput(
                    input,
                    records,
                    (uint)records.Length,
                    out var written);
                var writeErr = Marshal.GetLastWin32Error();
                if (wrote && written == (uint)records.Length)
                {
                    return true;
                }

                notes.Add(
                    $"WriteConsoleInputW({processId}) wrote={wrote} n={written}/{records.Length} win32={writeErr}.");
                return false;
            }
            finally
            {
                if (previousCp != 0)
                {
                    Native.SetConsoleCP(previousCp);
                }
            }
        }
        finally
        {
            if (input != IntPtr.Zero && input != Native.InvalidHandle)
            {
                Native.CloseHandle(input);
            }

            if (attached)
            {
                Native.FreeConsole();
            }
        }
    }

    private static bool TryTypeIntoWindow(int processId, string text, List<string> notes)
    {
        var hwnd = FindVisibleWindow(processId);
        if (hwnd == IntPtr.Zero)
        {
            notes.Add($"No window for pid {processId}.");
            return false;
        }

        Native.ShowWindow(hwnd, Native.SwRestore);
        TryForceForeground(hwnd);
        Thread.Sleep(40);
        var typed = Send(BuildSendInput(text));
        if (!typed.ok)
        {
            notes.Add($"SendInput({processId}) type sent={typed.sent}/{typed.total} win32={typed.error}.");
            return false;
        }

        Thread.Sleep(200);
        TryForceForeground(hwnd);
        Thread.Sleep(40);
        var submitted = Send(BuildSubmitInput());
        if (!submitted.ok)
        {
            notes.Add($"SendInput({processId}) submit sent={submitted.sent}/{submitted.total} win32={submitted.error}.");
            return false;
        }

        return true;
    }

    private static (bool ok, uint sent, int total, int error) Send(INPUT[] inputs)
    {
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return (sent == (uint)inputs.Length, sent, inputs.Length, Marshal.GetLastWin32Error());
    }

    private static bool ConsoleHasProcess(int processId)
    {
        var buffer = new uint[64];
        var count = Native.GetConsoleProcessList(buffer, (uint)buffer.Length);
        if (count == 0)
        {
            return false;
        }

        for (var i = 0; i < count && i < buffer.Length; i++)
        {
            if (buffer[i] == (uint)processId)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryForceForeground(IntPtr hwnd)
    {
        var foreground = Native.GetForegroundWindow();
        var currentThread = Native.GetCurrentThreadId();
        Native.GetWindowThreadProcessId(foreground, out _);
        var targetThread = Native.GetWindowThreadProcessId(hwnd, out _);
        if (targetThread != 0)
        {
            Native.AttachThreadInput(currentThread, targetThread, true);
        }

        Native.SetForegroundWindow(hwnd);
        if (targetThread != 0)
        {
            Native.AttachThreadInput(currentThread, targetThread, false);
        }
    }



    public static INPUT[] BuildSendInput(string text)
    {
        var normalized = FlattenForTyping(text);
        var list = new List<INPUT>(normalized.Length * 2);
        foreach (var ch in normalized)
        {
            list.Add(Unicode(ch, up: false));
            list.Add(Unicode(ch, up: true));
        }

        return list.ToArray();
    }

    public static INPUT[] BuildSubmitInput()
    {
        return
        [
            Unicode('\r', up: false),
            Unicode('\r', up: true),
            VirtualKey(VkReturn, up: false),
            VirtualKey(VkReturn, up: true),
            VirtualKey(VkControl, up: false),
            VirtualKey(VkI, up: false),
            VirtualKey(VkI, up: true),
            VirtualKey(VkControl, up: true)
        ];
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = Native.InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = Native.KeyeventfUnicode | (up ? Native.KeyeventfKeyup : 0)
            }
        }
    };

    private static INPUT VirtualKey(ushort vk, bool up) => new()
    {
        type = Native.InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)Native.MapVirtualKey(vk, 0),
                dwFlags = up ? Native.KeyeventfKeyup : 0
            }
        }
    };

    private static IntPtr FindVisibleWindow(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    public static IEnumerable<int> EnumerateProcessChain(int processId)
    {
        var current = processId;
        var guard = 0;
        while (current > 0 && guard++ < 8)
        {
            yield return current;
            current = Native.ParentProcessId(current) ?? 0;
        }
    }
}

public static class ProcessLockHolder
{
    public static int? TryGetPid(string lockPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(lockPath) || !File.Exists(lockPath))
        {
            return null;
        }

        var session = 0u;
        var key = Guid.NewGuid().ToString();
        if (Native.RmStartSession(out session, 0, key) != 0)
        {
            return null;
        }

        try
        {
            string[] files = [lockPath];
            if (Native.RmRegisterResources(session, 1, files, 0, IntPtr.Zero, 0, null) != 0)
            {
                return null;
            }

            uint needed = 0;
            uint count = 0;
            uint reboot = 0;
            _ = Native.RmGetList(session, out needed, ref count, null, ref reboot);
            if (needed == 0)
            {
                return null;
            }

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            var status = Native.RmGetList(session, out needed, ref count, infos, ref reboot);
            if (status != 0 || count == 0)
            {
                return null;
            }

            return infos[0].Process.dwProcessId;
        }
        finally
        {
            Native.RmEndSession(session);
        }
    }
}

internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint InputKeyboard = 1;
    public const uint KeyeventfKeyup = 0x0002;
    public const uint KeyeventfUnicode = 0x0004;
    public const int SwRestore = 9;
    public const uint Th32csSnapProcess = 2;
    public const int ErrorAccessDenied = 5;
    public static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    public static extern bool WriteConsoleInput(
        IntPtr hConsoleInput,
        INPUT_RECORD[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleProcessList(uint[] processList, uint count);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In] [Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    public static int? ParentProcessId(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return null;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return null;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct KEY_EVENT_RECORD
{
    public int bKeyDown;
    public ushort wRepeatCount;
    public ushort wVirtualKeyCode;
    public ushort wVirtualScanCode;
    public char UnicodeChar;
    public uint dwControlKeyState;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT_RECORD
{
    [FieldOffset(0)]
    public ushort EventType;

    [FieldOffset(4)]
    public KEY_EVENT_RECORD KeyEvent;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESSENTRY32
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public nint th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExeFile;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public InputUnion U;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct InputUnion
{
    [FieldOffset(0)]
    public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct RM_UNIQUE_PROCESS
{
    public int dwProcessId;
    public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct RM_PROCESS_INFO
{
    public RM_UNIQUE_PROCESS Process;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strAppName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string strServiceShortName;

    public int ApplicationType;
    public uint AppStatus;
    public uint TSSessionId;

    [MarshalAs(UnmanagedType.Bool)]
    public bool bRestartable;
}
