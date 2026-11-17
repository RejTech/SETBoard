using System.Runtime.InteropServices;
using Avalonia;

namespace SETBoard;

/// <summary>
/// 通过 Win32 SetWindowPos + HWND_TOPMOST 实现 UI Access 级窗口置顶，
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

    public static void MakeTopmost(Window window)
    {
        var handle = window.PlatformImpl?.Handle;
        if (handle is null) return;

        IntPtr hwnd = handle.DangerousHandle;

        window.Opened += (_, _) => SetTopmost(hwnd);
        SetTopmost(hwnd);

        static void SetTopmost(IntPtr h)
        {
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
            SetWindowPos(h, new IntPtr(HwndTopmost), 0, 0, 0, 0,
                (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
        }
    }
}
