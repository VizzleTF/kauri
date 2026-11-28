using System.Runtime.InteropServices;
using System.Text;

namespace Kauri.Core;

internal static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetClipboardOwner();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2;

    /// <summary>Marks keys Kauri sends itself, so the menu's keyboard hook lets them through.</summary>
    public static readonly IntPtr KauriInputTag = new(0x4B415552);

    public static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = KauriInputTag } },
    };

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static string? ProcessPathForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Bring a window to the foreground from a background tray process. Returns whether it is
    /// the foreground window afterwards.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
        if (GetForegroundWindow() == hwnd) return true;
        // Windows grants the foreground to the process that sent the last input. A key-up of an
        // unassigned key is that input and does nothing else, even with Ctrl or Shift still held.
        SendInput(1, [Key(0xE8, up: true)], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    /// <summary>Process and class of a window, for traces.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        var path = ProcessPathForWindow(hwnd);
        return $"{System.IO.Path.GetFileName(path) ?? "?"}/{sb}";
    }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    public const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int size, out int returned);

    /// <summary>
    /// True if the window's process runs above this one (an elevated app). Windows does not pass
    /// input bound for it to this process's low-level hooks.
    /// </summary>
    public static bool IsAboveThisProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Environment.IsPrivilegedProcess) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try
        {
            if (!OpenProcessToken(h, 0x8 /* TOKEN_QUERY */, out var token)) return Marshal.GetLastWin32Error() == 5;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    public const uint EVENT_SYSTEM_FOREGROUND = 3;

    [DllImport("user32.dll")]
    static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    static extern short GetKeyState(int vk);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int size, uint flags, IntPtr hkl);

    /// <summary>
    /// The text a key types in the keyboard layout of the app in front, with Shift, AltGr and Caps Lock;
    /// null for keys that type nothing. Leaves dead-key state alone.
    /// </summary>
    public static string? KeyText(uint vk, uint scan)
    {
        var state = new byte[256];
        if (IsKeyDown(0x10)) state[0x10] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 1;
        if (IsKeyDown(0xA5)) state[0x11] = state[0x12] = 0x80; // AltGr
        var hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var sb = new StringBuilder(8);
        int n = ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 0x4 /* keep keyboard state */, hkl);
        return n > 0 ? sb.ToString(0, n) : null;
    }
    public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
}
