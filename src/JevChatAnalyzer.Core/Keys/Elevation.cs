using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace JevChatAnalyzer.Core.Keys;

/// <summary>当前进程权限，以及能否读取微信进程内存。</summary>
public static class Elevation
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    /// <summary>false = 当前是 UAC 筛选后的普通权限进程。</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    ///     微信以更高完整级别运行（管理员启动）时无法 OpenProcess 其内存，rwk.dll 只会笼统地报
    ///     「未找到任何有效用户信息」。这里先探测一次，好给出可执行的提示。
    /// </summary>
    public static bool CanReadWeChatProcesses(out int denied)
    {
        denied = 0;
        var opened = 0;
        foreach (var process in Process.GetProcessesByName("Weixin"))
            using (process)
            {
                var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
                if (handle == 0)
                {
                    denied++;
                    continue;
                }

                opened++;
                CloseHandle(handle);
            }

        return opened > 0 || denied == 0;
    }

    /// <summary>
    ///     以管理员身份重新启动自身。先起一个等待进程：等当前实例完全退出（释放单实例互斥量）后
    ///     再以 runas 启动，否则新实例会被单实例检查挡掉。用户取消 UAC 时新进程静默消失。
    /// </summary>
    public static bool TryRestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var script =
            $"Wait-Process -Id {Environment.ProcessId} -Timeout 25 -ErrorAction SilentlyContinue; " +
            $"Start-Process -FilePath '{exe.Replace("'", "''")}' -Verb RunAs";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
