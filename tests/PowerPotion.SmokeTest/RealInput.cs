using System.Runtime.InteropServices;

namespace PowerPotion.SmokeTest;

/// <summary>
/// Real keyboard/mouse injection for the input test. Every action is guarded by a foreground check so
/// nothing is sent to another application.
/// </summary>
internal static class RealInput
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public static bool IsForeground(int pid)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == (uint)pid;
    }

    /// <summary>Attaches to the foreground thread and activates the host window.</summary>
    public static bool ForceForeground(IntPtr hwnd, int pid)
    {
        if (hwnd == IntPtr.Zero) return false;
        var fg = GetForegroundWindow();
        var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var attached = fgThread != 0 && fgThread != targetThread && AttachThreadInput(fgThread, targetThread, true);
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, targetThread, false);
        }
        Thread.Sleep(200);
        return IsForeground(pid);
    }

    public static bool CenterCursor(IntPtr hwnd, out int x, out int y)
    {
        x = y = 0;
        if (!GetWindowRect(hwnd, out var r)) return false;
        x = r.Left + (r.Right - r.Left) / 2;
        y = r.Top + (r.Bottom - r.Top) / 2;
        return SetCursorPos(x, y);
    }

    public static void Key(ushort vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    public static void MoveRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    public static void RightButton(bool down)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP } },
        };
        SendInput(1, new[] { input }, InputSize);
    }

    public static void Wheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL },
            },
        };
        SendInput(1, new[] { input }, InputSize);
    }
}
