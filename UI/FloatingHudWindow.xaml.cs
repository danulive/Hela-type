using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HelaType.Engine;
using HelaType.Native;

namespace HelaType.UI;

public partial class FloatingHudWindow : Window
{
    private static readonly SolidColorBrush SinglishBadgeBrush = new(Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush EnglishBadgeBrush = new(Color.FromRgb(71, 85, 105));

    private readonly DispatcherTimer _toastHideTimer;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isPinned;
    private bool _hasCustomPosition;
    private double _customLeft;
    private double _customTop;

    private bool _isDragging;
    private Win32Api.POINT _dragStartScreenPx;
    private double _dragStartLeft;
    private double _dragStartTop;

    public event Action<int>? CandidateSelected;

    public FloatingHudWindow()
    {
        InitializeComponent();

        _toastHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _toastHideTimer.Tick += (_, _) =>
        {
            _toastHideTimer.Stop();
            ModeToastText.Visibility = Visibility.Collapsed;
            if (!_isPinned && string.IsNullOrEmpty(RomanBufferText.Text))
            {
                Hide();
            }
            else if (_isPinned)
            {
                PositionHud();
            }
        };
    }

    public void EnsureInitializedHidden()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }
    }

    public bool IsOwnHandle(IntPtr hwnd) => hwnd != IntPtr.Zero && hwnd == _hwnd;

    public void SetPinned(bool isPinned)
    {
        _isPinned = isPinned;
        if (_isPinned)
        {
            _toastHideTimer.Stop();
            ModeToastText.Visibility = Visibility.Collapsed;
            if (!IsVisible)
            {
                EnsureInitializedHidden();
                PositionHud();
                Show();
            }
        }
        else if (string.IsNullOrEmpty(RomanBufferText.Text))
        {
            Hide();
        }
    }

    public void UpdateState(
        bool isSinglishEnabled,
        string romanBuffer,
        string sinhalaText,
        IReadOnlyList<PhoneticCandidate> candidates,
        bool showModeToast)
    {
        EnsureInitializedHidden();

        ModeBadgeText.Text = isSinglishEnabled ? "සිං" : "EN";
        ModeBadgeBorder.Background = isSinglishEnabled ? SinglishBadgeBrush : EnglishBadgeBrush;

        bool hasBuffer = isSinglishEnabled && !string.IsNullOrEmpty(romanBuffer);

        if (hasBuffer)
        {
            _toastHideTimer.Stop();
            ModeToastText.Visibility = Visibility.Collapsed;

            RomanBufferText.Text = romanBuffer;
            SinhalaPreviewText.Text = sinhalaText;
            BufferPreviewPanel.Visibility = Visibility.Visible;

            UpdateCandidatePills(candidates);

            if (!IsVisible)
            {
                Show();
            }

            PositionHud();
            return;
        }

        // No active Roman buffer
        RomanBufferText.Text = string.Empty;
        SinhalaPreviewText.Text = string.Empty;
        BufferPreviewPanel.Visibility = Visibility.Collapsed;
        CandidatesPanel.Visibility = Visibility.Collapsed;

        if (showModeToast)
        {
            ModeToastText.Text = isSinglishEnabled ? "Singlish ON" : "English";
            ModeToastText.Visibility = Visibility.Visible;

            if (!IsVisible)
            {
                Show();
            }

            PositionHud();
            _toastHideTimer.Stop();
            _toastHideTimer.Start();
            return;
        }

        ModeToastText.Visibility = Visibility.Collapsed;
        if (_isPinned)
        {
            if (!IsVisible)
            {
                Show();
            }
            PositionHud();
        }
        else if (!_toastHideTimer.IsEnabled && IsVisible)
        {
            Hide();
        }
    }

    private void UpdateCandidatePills(IReadOnlyList<PhoneticCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            CandidatesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        CandidatesPanel.Visibility = Visibility.Visible;

        Candidate0Text.Text = candidates[0].SinhalaText;
        Candidate0Border.Visibility = Visibility.Visible;

        if (candidates.Count > 1)
        {
            Candidate1Text.Text = candidates[1].SinhalaText;
            Candidate1Border.Visibility = Visibility.Visible;
        }
        else
        {
            Candidate1Border.Visibility = Visibility.Collapsed;
        }

        if (candidates.Count > 2)
        {
            Candidate2Text.Text = candidates[2].SinhalaText;
            Candidate2Border.Visibility = Visibility.Visible;
        }
        else
        {
            Candidate2Border.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // CRITICAL: Never steal focus from the active typing window
        long exStyle = Win32Api.GetWindowLongPtr(_hwnd, Win32Api.GWL_EXSTYLE).ToInt64();
        exStyle |= Win32Api.WS_EX_NOACTIVATE | Win32Api.WS_EX_TOOLWINDOW;
        Win32Api.SetWindowLongPtr(_hwnd, Win32Api.GWL_EXSTYLE, new IntPtr(exStyle));

        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(HudWndProc);
    }

    private static IntPtr HudWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Api.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Win32Api.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void PositionHud()
    {
        UpdateLayout();
        double hudWidth = ActualWidth > 1 ? ActualWidth : (DesiredSize.Width > 1 ? DesiredSize.Width : 220);
        double hudHeight = ActualHeight > 1 ? ActualHeight : (DesiredSize.Height > 1 ? DesiredSize.Height : 64);

        if (_hasCustomPosition)
        {
            Left = _customLeft;
            Top = _customTop;
            return;
        }

        if (TryGetCaretScreenPosition(out int caretX, out int caretBottomY, out int caretTopY))
        {
            IntPtr hMonitor = Win32Api.MonitorFromPoint(new Win32Api.POINT { X = caretX, Y = caretBottomY }, Win32Api.MONITOR_DEFAULTTONEAREST);
            GetMonitorWorkAreaDips(hMonitor, out double workLeft, out double workTop, out double workRight, out double workBottom, out double scale);

            double targetLeft = (caretX / scale) - 12;
            double targetTop = (caretBottomY / scale) + 4;

            if (targetTop + hudHeight > workBottom)
            {
                targetTop = (caretTopY / scale) - hudHeight - 4;
            }

            Left = Math.Clamp(targetLeft, workLeft, Math.Max(workLeft, workRight - hudWidth));
            Top = Math.Clamp(targetTop, workTop, Math.Max(workTop, workBottom - hudHeight));
            return;
        }

        // Fallback when target app (e.g. Chrome/WhatsApp/Electron) hides Win32 caret coordinates
        IntPtr fg = Win32Api.GetForegroundWindow();
        IntPtr fallbackMonitor = fg != IntPtr.Zero
            ? Win32Api.MonitorFromWindow(fg, Win32Api.MONITOR_DEFAULTTONEAREST)
            : Win32Api.MonitorFromPoint(new Win32Api.POINT { X = 0, Y = 0 }, Win32Api.MONITOR_DEFAULTTONEAREST);

        GetMonitorWorkAreaDips(fallbackMonitor, out _, out _, out double fbRight, out double fbBottom, out _);

        Left = fbRight - hudWidth - 14;
        Top = fbBottom - hudHeight - 14;
    }

    private static bool TryGetCaretScreenPosition(out int screenX, out int screenBottomY, out int screenTopY)
    {
        screenX = 0;
        screenBottomY = 0;
        screenTopY = 0;

        var gti = new Win32Api.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32Api.GUITHREADINFO>()
        };

        if (!Win32Api.GetGUIThreadInfo(0, ref gti) || gti.hwndCaret == IntPtr.Zero)
        {
            return false;
        }

        bool hasCaretRect = gti.rcCaret.left != 0 || gti.rcCaret.top != 0 ||
                            gti.rcCaret.right != 0 || gti.rcCaret.bottom != 0;
        if (!hasCaretRect)
        {
            return false;
        }

        var bottomLeft = new Win32Api.POINT { X = gti.rcCaret.left, Y = gti.rcCaret.bottom };
        var topLeft = new Win32Api.POINT { X = gti.rcCaret.left, Y = gti.rcCaret.top };

        if (!Win32Api.ClientToScreen(gti.hwndCaret, ref bottomLeft) ||
            !Win32Api.ClientToScreen(gti.hwndCaret, ref topLeft))
        {
            return false;
        }

        if (bottomLeft.X <= 0 && bottomLeft.Y <= 0)
        {
            return false;
        }

        screenX = bottomLeft.X;
        screenBottomY = bottomLeft.Y;
        screenTopY = topLeft.Y;
        return true;
    }

    private static void GetMonitorWorkAreaDips(
        IntPtr hMonitor,
        out double workLeft,
        out double workTop,
        out double workRight,
        out double workBottom,
        out double scale)
    {
        scale = GetMonitorDpiScale(hMonitor);

        var mi = new Win32Api.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32Api.MONITORINFO>()
        };

        if (hMonitor != IntPtr.Zero && Win32Api.GetMonitorInfoW(hMonitor, ref mi))
        {
            workLeft = mi.rcWork.left / scale;
            workTop = mi.rcWork.top / scale;
            workRight = mi.rcWork.right / scale;
            workBottom = mi.rcWork.bottom / scale;
            return;
        }

        Rect wa = SystemParameters.WorkArea;
        workLeft = wa.Left;
        workTop = wa.Top;
        workRight = wa.Right;
        workBottom = wa.Bottom;
    }

    private static double GetMonitorDpiScale(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return 1.0;
        }

        try
        {
            if (Win32Api.GetDpiForMonitor(hMonitor, Win32Api.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // Fallback to 96 DPI
        }

        return 1.0;
    }

    private void CandidatePill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string tagStr } && int.TryParse(tagStr, out int index))
        {
            CandidateSelected?.Invoke(index);
        }
    }

    private void ModeBadgeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _hasCustomPosition = false;
            PositionHud();
            e.Handled = true;
        }
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (Win32Api.GetCursorPos(out Win32Api.POINT pt))
        {
            _isDragging = true;
            _dragStartScreenPx = pt;
            _dragStartLeft = Left;
            _dragStartTop = Top;
            RootBorder.CaptureMouse();
            e.Handled = true;
        }
    }

    private void RootBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        if (Win32Api.GetCursorPos(out Win32Api.POINT pt))
        {
            IntPtr hMonitor = Win32Api.MonitorFromPoint(pt, Win32Api.MONITOR_DEFAULTTONEAREST);
            double scale = GetMonitorDpiScale(hMonitor);

            double dx = (pt.X - _dragStartScreenPx.X) / scale;
            double dy = (pt.Y - _dragStartScreenPx.Y) / scale;

            _customLeft = _dragStartLeft + dx;
            _customTop = _dragStartTop + dy;
            _hasCustomPosition = true;

            Left = _customLeft;
            Top = _customTop;
        }
    }

    private void RootBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            RootBorder.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
