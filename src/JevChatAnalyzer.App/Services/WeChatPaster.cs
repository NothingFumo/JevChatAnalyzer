using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace JevChatAnalyzer.App.Services;

/// <summary>
///     把候选回复粘贴进微信输入框。
///     微信 4.x 是 Qt 自绘界面，UIA 树为空，无法按控件定位；唯一可用的判据是
///     「前台窗口属于 Weixin.exe」。满足才发送一次 Ctrl+V；永不发送 Enter，也从不切换窗口。
/// </summary>
public static class WeChatPaster
{
    private const string WeChatWindowClass = "Qt51514QWindowIcon";
    private const string WeChatProcess = "Weixin";

    // ── 对外入口 ─────────────────────────────────────────────────────

    /// <summary>把文本放到剪贴板（调用方负责在 UI 线程调用）。</summary>
    public static bool CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>前台窗口是否为微信主窗口；是则返回窗口句柄。</summary>
    public static bool IsWeChatForeground(out nint hwnd)
    {
        hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return false;
        var cls = new StringBuilder(256);
        if (GetClassName(hwnd, cls, cls.Capacity) == 0) return false;
        if (!cls.ToString().StartsWith(WeChatWindowClass, StringComparison.Ordinal)) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            return string.Equals(Process.GetProcessById(pid).ProcessName, WeChatProcess,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     发送一次 Ctrl+V（绝不发送 Enter）。发送前重新确认前台仍是刚校验过的窗口：
    ///     倒计时期间用户切走，会把回复贴进别的会话甚至别的应用。
    /// </summary>
    public static bool SendPasteShortcut(nint expectedHwnd)
    {
        if (expectedHwnd == nint.Zero || GetForegroundWindow() != expectedHwnd) return false;

        const ushort vkControl = 0x11;
        const ushort vkV = 0x56;
        const uint keyUp = 0x0002;
        var inputs = new[]
        {
            Key(vkControl, 0),
            Key(vkV, 0),
            Key(vkV, keyUp),
            Key(vkControl, keyUp)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

        static INPUT Key(ushort vk, uint flags)
        {
            return new INPUT
            {
                type = 1, // INPUT_KEYBOARD
                U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
            };
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
