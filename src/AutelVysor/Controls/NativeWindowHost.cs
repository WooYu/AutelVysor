using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AutelVysor.Controls;

/// <summary>
/// Hosts a native top-level window (for example scrcpy's SDL window) inside WPF.
/// The external process remains independent; this class only reparents and resizes its HWND.
/// </summary>
public sealed class NativeWindowHost : HwndHost
{
    private const int GwlStyle = -16;
    private const uint WsChild = 0x40000000U;
    private const uint WsVisible = 0x10000000U;
    private const uint WsPopup = 0x80000000U;
    private const uint WsCaption = 0x00C00000U;
    private const uint WsThickFrame = 0x00040000U;
    private const uint WsClipChildren = 0x02000000U;
    private const uint WsClipSiblings = 0x04000000U;

    private IntPtr _hostHwnd;
    private IntPtr _childHwnd;
    private IntPtr _originalParent;
    private IntPtr _originalStyle;

    public IntPtr ChildHandle => _childHwnd;
    public bool HasChild => _childHwnd != IntPtr.Zero;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var style = WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        _hostHwnd = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            style,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException($"Unable to create native mirror host. Win32 error {Marshal.GetLastWin32Error()}.");

        return new HandleRef(this, _hostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DetachExternalWindow();
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _hostHwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeChild();
    }

    public void AttachExternalWindow(IntPtr childHwnd)
    {
        if (childHwnd == IntPtr.Zero)
            throw new ArgumentException("A valid native window handle is required.", nameof(childHwnd));
        if (_hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException("The WPF native host is not ready yet.");

        if (_childHwnd == childHwnd)
        {
            ResizeChild();
            return;
        }

        DetachExternalWindow();

        _childHwnd = childHwnd;
        _originalParent = GetParent(childHwnd);
        _originalStyle = GetWindowLongPtr(childHwnd, GwlStyle);

        var style = unchecked((uint)_originalStyle.ToInt64());
        style &= ~(WsPopup | WsCaption | WsThickFrame);
        style |= WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        SetWindowLongPtr(childHwnd, GwlStyle, new IntPtr(unchecked((long)style)));

        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(childHwnd, _hostHwnd);
        var parentError = Marshal.GetLastWin32Error();
        if (previousParent == IntPtr.Zero && parentError != 0)
            throw new InvalidOperationException($"Unable to embed scrcpy window. Win32 error {parentError}.");

        ResizeChild();
        ShowWindow(childHwnd, 5); // SW_SHOW
    }

    public void DetachExternalWindow()
    {
        if (_childHwnd == IntPtr.Zero)
            return;

        try
        {
            if (IsWindow(_childHwnd))
            {
                SetParent(_childHwnd, _originalParent);
                if (_originalStyle != IntPtr.Zero)
                    SetWindowLongPtr(_childHwnd, GwlStyle, _originalStyle);
            }
        }
        finally
        {
            _childHwnd = IntPtr.Zero;
            _originalParent = IntPtr.Zero;
            _originalStyle = IntPtr.Zero;
        }
    }

    private void ResizeChild()
    {
        if (_hostHwnd == IntPtr.Zero || _childHwnd == IntPtr.Zero || !IsWindow(_childHwnd))
            return;

        if (!GetClientRect(_hostHwnd, out var rect))
            return;

        MoveWindow(_childHwnd, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
