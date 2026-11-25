using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelaType.Native;

namespace HelaType.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint CmdToggleSinglish = 1001;
    private const uint CmdOpenDashboard = 1002;
    private const uint CmdStartWithWindows = 1003;
    private const uint CmdExit = 1004;

    private readonly uint _activateExistingMessageId;
    private uint _taskbarCreatedMessageId;

    private HwndSource? _hwndSource;
    private IntPtr _singlishIconHandle = IntPtr.Zero;
    private IntPtr _englishIconHandle = IntPtr.Zero;

    private bool _isSinglishEnabled = true;
    private bool _isStartWithWindowsEnabled;
    private bool _iconAdded;
    private bool _disposed;

    public event Action? ToggleModeRequested;
    public event Action? OpenDashboardRequested;
    public event Action? ToggleStartupRequested;
    public event Action? ExitRequested;
    public event Action? ActivateExistingRequested;

    public TrayIconService(uint activateExistingMessageId)
    {
        _activateExistingMessageId = activateExistingMessageId;
    }

    public void Initialize(bool isSinglishEnabled, bool isStartWithWindowsEnabled)
    {
        _isSinglishEnabled = isSinglishEnabled;
        _isStartWithWindowsEnabled = isStartWithWindowsEnabled;
        _taskbarCreatedMessageId = Win32Api.RegisterWindowMessageW("TaskbarCreated");

        var parameters = new HwndSourceParameters("HelaTypeTrayMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _singlishIconHandle = CreateBadgeIconHandle("සිං", Color.FromRgb(16, 185, 129));
        _englishIconHandle = CreateBadgeIconHandle("EN", Color.FromRgb(71, 85, 105));

        AddTrayIcon();
    }

    public void UpdateState(bool isSinglishEnabled, bool isStartWithWindowsEnabled)
    {
        _isSinglishEnabled = isSinglishEnabled;
        _isStartWithWindowsEnabled = isStartWithWindowsEnabled;

        if (_hwndSource is null || !_iconAdded)
        {
            return;
        }

        Win32Api.NOTIFYICONDATAW data = CreateBaseNotifyIconData();
        data.uFlags = Win32Api.NIF_ICON | Win32Api.NIF_TIP;
        data.hIcon = _isSinglishEnabled ? _singlishIconHandle : _englishIconHandle;
        data.szTip = _isSinglishEnabled
            ? "HelaType Windows — Singlish ON (Ctrl+Space)"
            : "HelaType Windows — English Mode (Ctrl+Space)";

        Win32Api.Shell_NotifyIconW(Win32Api.NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string message)
    {
        if (_hwndSource is null || !_iconAdded)
        {
            return;
        }

        Win32Api.NOTIFYICONDATAW data = CreateBaseNotifyIconData();
        data.uFlags = Win32Api.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = Win32Api.NIIF_INFO;
        data.uTimeoutOrVersion = 3000;

        Win32Api.Shell_NotifyIconW(Win32Api.NIM_MODIFY, ref data);
    }

    public static ImageSource CreateAppWindowIcon(bool singlishActive = true)
    {
        RenderTargetBitmap rtb = RenderBadgeBitmap(
            singlishActive ? "සිං" : "EN",
            singlishActive ? Color.FromRgb(16, 185, 129) : Color.FromRgb(71, 85, 105),
            size: 64);
        rtb.Freeze();
        return rtb;
    }

    private void AddTrayIcon()
    {
        if (_hwndSource is null)
        {
            return;
        }

        Win32Api.NOTIFYICONDATAW data = CreateBaseNotifyIconData();
        data.uFlags = Win32Api.NIF_MESSAGE | Win32Api.NIF_ICON | Win32Api.NIF_TIP;
        data.uCallbackMessage = Win32Api.WM_TRAYICON;
        data.hIcon = _isSinglishEnabled ? _singlishIconHandle : _englishIconHandle;
        data.szTip = _isSinglishEnabled
            ? "HelaType Windows — Singlish ON (Ctrl+Space)"
            : "HelaType Windows — English Mode (Ctrl+Space)";

        _iconAdded = Win32Api.Shell_NotifyIconW(Win32Api.NIM_ADD, ref data);
    }

    private Win32Api.NOTIFYICONDATAW CreateBaseNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32Api.NOTIFYICONDATAW>(),
        hWnd = _hwndSource?.Handle ?? IntPtr.Zero,
        uID = TrayIconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _taskbarCreatedMessageId && _taskbarCreatedMessageId != 0)
        {
            AddTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)msg == _activateExistingMessageId && _activateExistingMessageId != 0)
        {
            ActivateExistingRequested?.Invoke();
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)msg == Win32Api.WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32();
            if (mouseMsg == Win32Api.WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
            else if (mouseMsg == Win32Api.WM_LBUTTONUP || mouseMsg == Win32Api.WM_LBUTTONDBLCLK)
            {
                OpenDashboardRequested?.Invoke();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_hwndSource is null)
        {
            return;
        }

        IntPtr hMenu = Win32Api.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            uint singlishFlags = Win32Api.MF_STRING | (_isSinglishEnabled ? Win32Api.MF_CHECKED : 0);
            uint startupFlags = Win32Api.MF_STRING | (_isStartWithWindowsEnabled ? Win32Api.MF_CHECKED : 0);

            Win32Api.AppendMenuW(hMenu, singlishFlags, (UIntPtr)CmdToggleSinglish, "Toggle Singlish (Ctrl+Space)");
            Win32Api.AppendMenuW(hMenu, Win32Api.MF_STRING, (UIntPtr)CmdOpenDashboard, "Open Cheat Sheet && Test Pad");
            Win32Api.AppendMenuW(hMenu, Win32Api.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            Win32Api.AppendMenuW(hMenu, startupFlags, (UIntPtr)CmdStartWithWindows, "Start with Windows");
            Win32Api.AppendMenuW(hMenu, Win32Api.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            Win32Api.AppendMenuW(hMenu, Win32Api.MF_STRING, (UIntPtr)CmdExit, "Exit");

            Win32Api.GetCursorPos(out Win32Api.POINT pt);
            Win32Api.SetForegroundWindow(_hwndSource.Handle);

            uint cmd = Win32Api.TrackPopupMenuEx(
                hMenu,
                Win32Api.TPM_LEFTALIGN | Win32Api.TPM_BOTTOMALIGN | Win32Api.TPM_RETURNCMD | Win32Api.TPM_RIGHTBUTTON,
                pt.X,
                pt.Y,
                _hwndSource.Handle,
                IntPtr.Zero);

            Win32Api.PostMessageW(_hwndSource.Handle, Win32Api.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdToggleSinglish:
                    ToggleModeRequested?.Invoke();
                    break;
                case CmdOpenDashboard:
                    OpenDashboardRequested?.Invoke();
                    break;
                case CmdStartWithWindows:
                    ToggleStartupRequested?.Invoke();
                    break;
                case CmdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            Win32Api.DestroyMenu(hMenu);
        }
    }

    private static RenderTargetBitmap RenderBadgeBitmap(string glyph, Color fillColor, int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double corner = size * 0.24;
            var rect = new Rect(1, 1, size - 2, size - 2);
            var brush = new SolidColorBrush(fillColor);
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), Math.Max(1.0, size * 0.04));

            dc.DrawRoundedRectangle(brush, borderPen, rect, corner, corner);

            double fontSize = glyph.Length > 2 ? size * 0.44 : size * 0.48;
            var formattedText = new FormattedText(
                glyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Nirmala UI, Iskoola Pota, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize,
                Brushes.White,
                pixelsPerDip: 1.0);

            double textX = (size - formattedText.Width) / 2.0;
            double textY = (size - formattedText.Height) / 2.0;
            dc.DrawText(formattedText, new Point(textX, textY));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static IntPtr CreateBadgeIconHandle(string glyph, Color fillColor)
    {
        const int size = 32;
        RenderTargetBitmap rtb = RenderBadgeBitmap(glyph, fillColor, size);

        byte[] pixels = new byte[size * size * 4];
        rtb.CopyPixels(pixels, size * 4, 0);

        var header = new Win32Api.BITMAPV5HEADER
        {
            bV5Size = (uint)Marshal.SizeOf<Win32Api.BITMAPV5HEADER>(),
            bV5Width = size,
            bV5Height = -size, // Top-down DIB matching WPF pixel layout
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = Win32Api.BI_BITFIELDS,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000
        };

        IntPtr hColorBitmap = Win32Api.CreateDIBSection(
            IntPtr.Zero,
            ref header,
            Win32Api.DIB_RGB_COLORS,
            out IntPtr ppvBits,
            IntPtr.Zero,
            0);

        if (hColorBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 1-bpp all-zero mask bitmap (WORD-aligned rows: 32 pixels = 4 bytes/row)
        byte[] maskBits = new byte[size * 4];
        IntPtr hMaskBitmap = Win32Api.CreateBitmap(size, size, 1, 1, maskBits);

        try
        {
            Marshal.Copy(pixels, 0, ppvBits, pixels.Length);

            var iconInfo = new Win32Api.ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMaskBitmap,
                hbmColor = hColorBitmap
            };

            return Win32Api.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            Win32Api.DeleteObject(hColorBitmap);
            if (hMaskBitmap != IntPtr.Zero)
            {
                Win32Api.DeleteObject(hMaskBitmap);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_iconAdded && _hwndSource is not null)
        {
            Win32Api.NOTIFYICONDATAW data = CreateBaseNotifyIconData();
            Win32Api.Shell_NotifyIconW(Win32Api.NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_singlishIconHandle != IntPtr.Zero)
        {
            Win32Api.DestroyIcon(_singlishIconHandle);
            _singlishIconHandle = IntPtr.Zero;
        }

        if (_englishIconHandle != IntPtr.Zero)
        {
            Win32Api.DestroyIcon(_englishIconHandle);
            _englishIconHandle = IntPtr.Zero;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
