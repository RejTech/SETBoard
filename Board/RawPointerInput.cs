using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace SETBoard.Board;

internal enum RawPointerKind
{
    Down,
    Move,
    Up,
}

internal readonly record struct RawPointerSample(
    RawPointerKind Kind,
    int ContactId,
    Point Position,
    double Pressure,
    bool IsPen);

/// <summary>
/// 直接读取触屏/触控笔设备消息（WM_POINTER / WM_TOUCH），
/// 不经 Avalonia 的指针事件管线，采样更早、更密。
/// 只做旁路读取，不吞消息：Avalonia 仍然收到同一份输入，
/// 因此多点缩放、选择、橡皮等交互保持原样。
/// </summary>
internal sealed class RawPointerInput : IDisposable
{
    private const int GWLP_WNDPROC = -4;

    private const uint WM_TOUCH = 0x0240;
    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;

    private const uint TOUCHEVENTF_DOWN = 0x0001;
    private const uint TOUCHEVENTF_MOVE = 0x0002;
    private const uint TOUCHEVENTF_UP = 0x0004;

    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    private const int PT_TOUCH = 2;
    private const int PT_PEN = 3;

    private readonly Window _window;
    private readonly Action<RawPointerSample> _sink;
    private readonly WndProcDelegate _proc;
    private IntPtr _hwnd;
    private IntPtr _oldProc;
    private bool _disposed;

    private RawPointerInput(Window window, Action<RawPointerSample> sink)
    {
        _window = window;
        _sink = sink;
        _proc = WndProc;
    }

    /// <summary>给窗口挂上直读输入；不支持的系统或取不到句柄时返回 null，调用方回退到 Avalonia 事件。</summary>
    public static RawPointerInput? Attach(Window window, Action<RawPointerSample> sink)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return null;

        var input = new RawPointerInput(window, sink);
        input._hwnd = handle.Handle;
        input._oldProc = SetWindowLongPtrW(input._hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(input._proc));
        return input._oldProc == IntPtr.Zero ? null : input;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero && _oldProc != IntPtr.Zero)
        {
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldProc);
            _oldProc = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TOUCH:
                DispatchTouch(hwnd, wParam, lParam);
                break;
            case WM_POINTERDOWN:
            case WM_POINTERUPDATE:
            case WM_POINTERUP:
            case WM_POINTERCAPTURECHANGED:
                DispatchPointer(hwnd, msg, wParam);
                break;
        }

        // 先自己取样本，再交给 Avalonia 处理同一条消息（缩放、选择等不受影响）。
        var result = CallWindowProcW(_oldProc, hwnd, msg, wParam, lParam);

        // WM_TOUCH 的输入句柄正常由处理方关闭；这里兜底关闭，重复关闭会安全返回失败。
        if (msg == WM_TOUCH && lParam != IntPtr.Zero) CloseTouchInputHandle(lParam);

        return result;
    }

    private void DispatchTouch(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        int count = (int)(wParam.ToInt64() & 0xFFFF);
        if (count <= 0 || lParam == IntPtr.Zero) return;

        var inputs = new TOUCHINPUT[count];
        int size = Marshal.SizeOf<TOUCHINPUT>();
        if (!GetTouchInputInfo(lParam, (uint)count, inputs, size)) return;

        foreach (var input in inputs)
        {
            RawPointerKind kind;
            if ((input.dwFlags & TOUCHEVENTF_DOWN) != 0) kind = RawPointerKind.Down;
            else if ((input.dwFlags & TOUCHEVENTF_UP) != 0) kind = RawPointerKind.Up;
            else if ((input.dwFlags & TOUCHEVENTF_MOVE) != 0) kind = RawPointerKind.Move;
            else continue;

            // TOUCHINPUT 坐标单位是 0.01 屏幕像素，保留亚像素精度
            double physicalX = input.x / 100.0;
            double physicalY = input.y / 100.0;
            var screen = new POINT { X = (int)Math.Floor(physicalX), Y = (int)Math.Floor(physicalY) };
            if (!ScreenToClient(hwnd, ref screen)) continue;

            Emit(kind, input.dwID,
                screen.X + (physicalX - Math.Floor(physicalX)),
                screen.Y + (physicalY - Math.Floor(physicalY)),
                1.0, isPen: false);
        }
    }

    private void DispatchPointer(IntPtr hwnd, uint msg, IntPtr wParam)
    {
        int pointerId = (int)(wParam.ToInt64() & 0xFFFF);
        if (pointerId == 0) return;

        if (!GetPointerType(pointerId, out int pointerType)) return;

        bool isPen = pointerType == PT_PEN;
        if (!isPen && pointerType != PT_TOUCH) return;

        POINTER_INFO info;
        double pressure;
        if (isPen && GetPointerPenInfo(pointerId, out POINTER_PEN_INFO penInfo))
        {
            info = penInfo.PointerInfo;
            pressure = Math.Clamp(penInfo.Pressure / 1024.0, 0.02, 1.0);
        }
        else if (GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO touchInfo))
        {
            info = touchInfo.PointerInfo;
            pressure = touchInfo.Pressure > 0
                ? Math.Clamp(touchInfo.Pressure / 1024.0, 0.02, 1.0)
                : 1.0;
        }
        else
        {
            return;
        }

        var kind = msg switch
        {
            WM_POINTERDOWN => RawPointerKind.Down,
            WM_POINTERUP => RawPointerKind.Up,
            WM_POINTERCAPTURECHANGED => RawPointerKind.Up,
            _ => RawPointerKind.Move,
        };

        // 悬停（未接触）不产生墨迹
        if (kind == RawPointerKind.Move
            && ((info.PointerFlags & POINTER_FLAG_INCONTACT) == 0 || (info.PointerFlags & POINTER_FLAG_DOWN) != 0))
        {
            return;
        }

        var screen = new POINT { X = info.PtPixelLocation.X, Y = info.PtPixelLocation.Y };
        if (!ScreenToClient(hwnd, ref screen)) return;

        Emit(kind, pointerId, screen.X, screen.Y, pressure, isPen);
    }

    private void Emit(RawPointerKind kind, int contactId, double clientX, double clientY,
        double pressure, bool isPen)
    {
        double scaling = _window.RenderScaling;
        if (scaling <= 0) scaling = 1;

        _sink(new RawPointerSample(
            kind,
            contactId,
            new Point(clientX / scaling, clientY / scaling),
            pressure,
            isPen));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public POINT PtPixelLocation;
        public POINT PtHimetricLocation;
        public POINT PtPixelLocationRaw;
        public POINT PtHimetricLocationRaw;
        public uint DwTime;
        public uint HistoryCount;
        public int InputState;
        public uint DwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO PointerInfo;
        public uint PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public RECT Contact;
        public RECT ContactRaw;
        public uint Orientation;
        public uint Pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOUCHINPUT
    {
        public int x;
        public int y;
        public IntPtr hSource;
        public int dwID;
        public uint dwFlags;
        public uint dwMask;
        public uint dwTime;
        public IntPtr dwExtraInfo;
        public uint cxContact;
        public uint cyContact;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetTouchInputInfo(IntPtr hTouchInput, uint cInputs,
        [Out] TOUCHINPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseTouchInputHandle(IntPtr hTouchInput);

    [DllImport("user32.dll")]
    private static extern bool GetPointerType(int pointerId, out int pointerType);

    [DllImport("user32.dll")]
    private static extern bool GetPointerPenInfo(int pointerId, out POINTER_PEN_INFO penInfo);

    [DllImport("user32.dll")]
    private static extern bool GetPointerTouchInfo(int pointerId, out POINTER_TOUCH_INFO touchInfo);
}
