using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

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
    private bool _fitChild = true;
    private int _actualChildWidth;
    private int _actualChildHeight;
    private IntPtr _locationHook;
    private readonly WinEventProc _locationCallback;
    private bool _resizeQueued;

    public NativeWindowHost() => _locationCallback = OnChildLocationChanged;

    public IntPtr ChildHandle => _childHwnd;
    public bool HasChild => _childHwnd != IntPtr.Zero;

    /// <summary>
    /// Cross-process SDL HWNDs do not automatically participate in WPF keyboard
    /// navigation. Transfer Win32 input focus to the real scrcpy child HWND.
    /// </summary>
    public bool FocusEmbeddedWindow()
    {
        if (_childHwnd == IntPtr.Zero || !IsWindow(_childHwnd))
            return false;

        var targetThread = GetWindowThreadProcessId(_childHwnd, out _);
        var uiThread = GetCurrentThreadId();
        var joined = targetThread != 0 && targetThread != uiThread &&
                     AttachThreadInput(uiThread, targetThread, true);
        try
        {
            SetFocus(_childHwnd);
            return GetFocus() == _childHwnd;
        }
        finally
        {
            if (joined)
                AttachThreadInput(uiThread, targetThread, false);
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => { if (HasChild) FocusEmbeddedWindow(); }));
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_SETFOCUS: WPF may focus the host HWND instead of the embedded SDL HWND.
        if (msg == 0x0007 && HasChild)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => { if (HasChild) FocusEmbeddedWindow(); }));
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var style = WsChild | WsVisible | WsClipChildren | WsClipSiblings | 0x00000004U; // SS_BLACKRECT
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

        // SDL resizes its own HWND when the Android display orientation changes.
        // WPF's layout may remain unchanged, so OnRenderSizeChanged alone cannot keep it fitted.
        GetWindowThreadProcessId(childHwnd, out var processId);
        _locationHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _locationCallback, processId, 0, 0);
        if (_locationHook == IntPtr.Zero)
            throw new InvalidOperationException($"Unable to monitor mirror window size. Win32 error {Marshal.GetLastWin32Error()}.");
        ResizeChild();
        ShowWindow(childHwnd, 5); // SW_SHOW
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => { if (HasChild) FocusEmbeddedWindow(); }));
    }

    public void SetFitMode()
    {
        _fitChild = true;
        ResizeChild();
    }

    public void SetActualSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A positive Android display size is required.");
        _fitChild = false;
        _actualChildWidth = width;
        _actualChildHeight = height;
        ResizeChild();
    }

    public void DetachExternalWindow()
    {
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
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

        var hostWidth = Math.Max(1, rect.Right);
        var hostHeight = Math.Max(1, rect.Bottom);
        var actualSize = !_fitChild && _actualChildWidth > 0 && _actualChildHeight > 0;
        var width = actualSize ? _actualChildWidth : hostWidth;
        var height = actualSize ? _actualChildHeight : hostHeight;
        var x = actualSize ? (hostWidth - width) / 2 : 0;
        var y = actualSize ? (hostHeight - height) / 2 : 0;

        if (GetWindowRect(_childHwnd, out var current))
        {
            MapWindowPoints(IntPtr.Zero, _hostHwnd, ref current, 2);
            if (current.Left == x && current.Top == y && current.Right - current.Left == width && current.Bottom - current.Top == height)
                return; // Avoid a resize event feedback loop.
        }
        // Never synchronously wait for the external renderer's window procedure on the UI thread.
        SetWindowPos(_childHwnd, IntPtr.Zero, x, y, width, height, 0x4000 | 0x0010 | 0x0004);
    }

    private void OnChildLocationChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (hwnd != _childHwnd || objectId != 0 || childId != 0 || _resizeQueued || Dispatcher.HasShutdownStarted)
            return;
        _resizeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _resizeQueued = false;
            ResizeChild();
        }));
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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Rect rect, uint points);

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}
