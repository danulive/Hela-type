using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using HelaType.Native;
using HelaType.Services;
using HelaType.UI;
using Microsoft.Win32;

namespace HelaType;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\HelaTypeWindows_SingleInstance_Mutex";
    private const string StartupRegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValueName = "HelaTypeWindows";

    private Mutex? _singleInstanceMutex;
    private uint _activateExistingMessageId;

    private InputInjector? _inputInjector;
    private KeyboardHookService? _keyboardHookService;
    private FloatingHudWindow? _floatingHudWindow;
    private TrayIconService? _trayIconService;
    private MainWindow? _mainWindow;

    private bool _isExiting;
    private bool _hasShownTrayBalloon;

    static App()
    {
        try
        {
            Win32Api.SetProcessDpiAwarenessContext(Win32Api.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Ignore on older Windows builds that do not support PerMonitorV2 API.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activateExistingMessageId = Win32Api.RegisterWindowMessageW("HelaTypeWindows_ActivateExisting");

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Win32Api.PostMessageW(Win32Api.HWND_BROADCAST, _activateExistingMessageId, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        var inputInjector = new InputInjector();
        var hookService = new KeyboardHookService(inputInjector);
        _inputInjector = inputInjector;
        _keyboardHookService = hookService;

        var floatingHud = new FloatingHudWindow();
        floatingHud.CandidateSelected += index => hookService.SelectCandidate(index);
        _floatingHudWindow = floatingHud;

        var trayIcon = new TrayIconService(_activateExistingMessageId);
        trayIcon.ToggleModeRequested += () => hookService.ToggleMode();
        trayIcon.OpenDashboardRequested += ShowMainWindow;
        trayIcon.ToggleStartupRequested += ToggleStartWithWindows;
        trayIcon.ExitRequested += ExitApplication;
        trayIcon.ActivateExistingRequested += ShowMainWindow;
        trayIcon.Initialize(
            isSinglishEnabled: hookService.IsSinglishEnabled,
            isStartWithWindowsEnabled: IsStartWithWindowsEnabled());
        _trayIconService = trayIcon;

        _mainWindow = new MainWindow(this, hookService);

        hookService.ModeChanged += OnInputModeChanged;
        hookService.BufferChanged += OnInputBufferChanged;
        hookService.IsHudExcludedWindow = hwnd => floatingHud.IsOwnHandle(hwnd);

        try
        {
            hookService.InstallHooks();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HelaType could not install global input hooks:\n{ex.Message}",
                "HelaType Windows",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
            return;
        }

        bool startMinimized = e.Args.Any(arg =>
            string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (!startMinimized)
        {
            ShowMainWindow();
        }
        else
        {
            // Ensure HUD handle is initialized cleanly even if dashboard starts minimized.
            floatingHud.EnsureInitializedHidden();
        }
    }

    private void OnInputModeChanged(bool isSinglishEnabled)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _trayIconService?.UpdateState(isSinglishEnabled, IsStartWithWindowsEnabled());
            _floatingHudWindow?.UpdateState(
                isSinglishEnabled,
                _inputInjector?.CurrentRomanBuffer ?? string.Empty,
                _inputInjector?.RenderedSinhalaText ?? string.Empty,
                _inputInjector?.CurrentCandidates ?? Array.Empty<Engine.PhoneticCandidate>(),
                showModeToast: true);
            _mainWindow?.UpdateLiveState(
                isSinglishEnabled,
                _inputInjector?.CurrentRomanBuffer ?? string.Empty,
                _inputInjector?.RenderedSinhalaText ?? string.Empty,
                _inputInjector?.CurrentCandidates ?? Array.Empty<Engine.PhoneticCandidate>());
        });
    }

    private void OnInputBufferChanged(InputBufferSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _floatingHudWindow?.UpdateState(
                snapshot.IsSinglishEnabled,
                snapshot.RomanBuffer,
                snapshot.SinhalaText,
                snapshot.Candidates,
                showModeToast: false);
            _mainWindow?.UpdateLiveState(
                snapshot.IsSinglishEnabled,
                snapshot.RomanBuffer,
                snapshot.SinhalaText,
                snapshot.Candidates);
        });
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void MinimizeMainWindowToTray()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Hide();
        if (!_hasShownTrayBalloon)
        {
            _hasShownTrayBalloon = true;
            _trayIconService?.ShowBalloon(
                "HelaType Windows",
                "Running in system tray. Press Ctrl+Space or Alt+Z anywhere to toggle Sinhala typing.");
        }
    }

    public void SetHudPinned(bool isPinned)
    {
        _floatingHudWindow?.SetPinned(isPinned);
        if (_keyboardHookService is not null && _inputInjector is not null)
        {
            _floatingHudWindow?.UpdateState(
                _keyboardHookService.IsSinglishEnabled,
                _inputInjector.CurrentRomanBuffer,
                _inputInjector.RenderedSinhalaText,
                _inputInjector.CurrentCandidates,
                showModeToast: isPinned);
        }
    }

    public bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryRunKey, writable: false);
            return key?.GetValue(StartupRegistryValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public bool SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryRunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(StartupRegistryRunKey, writable: true);

            if (key is null)
            {
                return false;
            }

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    using var proc = Process.GetCurrentProcess();
                    exePath = proc.MainModule?.FileName;
                }

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                key.SetValue(StartupRegistryValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
            }

            if (_keyboardHookService is not null)
            {
                _trayIconService?.UpdateState(_keyboardHookService.IsSinglishEnabled, IsStartWithWindowsEnabled());
                _mainWindow?.RefreshStartupCheckbox(IsStartWithWindowsEnabled());
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleStartWithWindows()
    {
        SetStartWithWindows(!IsStartWithWindowsEnabled());
    }

    public bool IsExiting => _isExiting;

    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        _keyboardHookService?.Dispose();
        _trayIconService?.Dispose();
        _floatingHudWindow?.Close();
        _mainWindow?.Close();

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if already released.
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isExiting)
        {
            _keyboardHookService?.Dispose();
            _trayIconService?.Dispose();
            _singleInstanceMutex?.Dispose();
        }
        base.OnExit(e);
    }
}
