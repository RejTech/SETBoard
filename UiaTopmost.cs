using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 通过 Win32 SetWindowPos + HWND_TOPMOST 实现窗口置顶，
/// 确保浮窗和笔迹层始终在所有窗口之上（包括希沃全屏覆盖窗口）。
/// </summary>
internal static class UiaTopmost
{
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;
    private const int HwndTopmost = -1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>将窗口设为置顶。</summary>
    public static void MakeTopmost(Window window)
    {
        // 通过 TryGetPlatformHandle() 获取 Win32 HWND，窗口打开后回调确保句柄有效
        window.Opened += (_, _) =>
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero) return;

            IntPtr hwnd = handle.Handle;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
            SetWindowPos(hwnd, new IntPtr(HwndTopmost), 0, 0, 0, 0,
                (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
        };
    }
}
