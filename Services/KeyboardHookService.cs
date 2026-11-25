using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HelaType.Engine;
using HelaType.Native;

namespace HelaType.Services;

public readonly record struct InputBufferSnapshot(
    bool IsSinglishEnabled,
    string RomanBuffer,
    string SinhalaText,
    IReadOnlyList<PhoneticCandidate> Candidates);

public sealed class KeyboardHookService : IDisposable
{
    private readonly InputInjector _inputInjector;
    private readonly Win32Api.HookProc _keyboardProc;
    private readonly Win32Api.HookProc _mouseProc;
    private readonly HashSet<uint> _suppressedKeyUps = new();

    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private bool _disposed;

    private bool _isSinglishEnabled = true;

    // Synchronized modifier state tracked across key stream with async-state self-healing
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _leftAltDown;
    private bool _rightAltDown;
    private bool _leftWinDown;
    private bool _rightWinDown;

    public event Action<bool>? ModeChanged;
    public event Action<InputBufferSnapshot>? BufferChanged;

    public Func<IntPtr, bool>? IsHudExcludedWindow { get; set; }

    public KeyboardHookService(InputInjector inputInjector)
    {
        _inputInjector = inputInjector;
        _keyboardProc = LowLevelKeyboardCallback;
        _mouseProc = LowLevelMouseCallback;
    }

    public bool IsSinglishEnabled => _isSinglishEnabled;

    public void InstallHooks()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            return;
        }

        using Process currentProcess = Process.GetCurrentProcess();
        using ProcessModule? currentModule = currentProcess.MainModule;

        IntPtr hModule = Win32Api.GetModuleHandleW(currentModule?.ModuleName);
        if (hModule == IntPtr.Zero)
        {
            hModule = Win32Api.GetModuleHandleW(null);
        }

        _keyboardHookId = Win32Api.SetWindowsHookExW(Win32Api.WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
        if (_keyboardHookId == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global WH_KEYBOARD_LL hook.");
        }

        _mouseHookId = Win32Api.SetWindowsHookExW(Win32Api.WH_MOUSE_LL, _mouseProc, hModule, 0);
        if (_mouseHookId == IntPtr.Zero)
        {
            Win32Api.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global WH_MOUSE_LL hook.");
        }
    }

    public void ToggleMode()
    {
        CommitAndClearBuffer();
        _isSinglishEnabled = !_isSinglishEnabled;
        ModeChanged?.Invoke(_isSinglishEnabled);
    }

    public void SetMode(bool enabled)
    {
        if (_isSinglishEnabled == enabled)
        {
            return;
        }

        CommitAndClearBuffer();
        _isSinglishEnabled = enabled;
        ModeChanged?.Invoke(_isSinglishEnabled);
    }

    public void SelectCandidate(int index)
    {
        if (!_isSinglishEnabled)
        {
            return;
        }

        if (_inputInjector.SelectCandidate(index))
        {
            RaiseBufferChanged();
        }
    }

    public void CommitAndClearBuffer()
    {
        bool hadBuffer = _inputInjector.CurrentRomanBuffer.Length > 0;
        _inputInjector.ClearBuffer();
        if (hadBuffer)
        {
            RaiseBufferChanged();
        }
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        Win32Api.KBDLLHOOKSTRUCT kb = Marshal.PtrToStructure<Win32Api.KBDLLHOOKSTRUCT>(lParam);

        // CRITICAL: Prevent infinite feedback loops on our own or other injected keystrokes
        if ((kb.flags & Win32Api.LLKHF_INJECTED) != 0 ||
            (kb.flags & Win32Api.LLKHF_LOWER_IL_INJECTED) != 0 ||
            kb.dwExtraInfo == Win32Api.HELA_INJECT_SIGNATURE ||
            _inputInjector.IsInjecting)
        {
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        int msg = wParam.ToInt32();
        bool isKeyDown = msg == Win32Api.WM_KEYDOWN || msg == Win32Api.WM_SYSKEYDOWN;
        bool isKeyUp = msg == Win32Api.WM_KEYUP || msg == Win32Api.WM_SYSKEYUP;

        UpdateModifierTracking(kb.vkCode, isKeyDown, isKeyUp);

        if (isKeyUp)
        {
            if (_suppressedKeyUps.Remove(kb.vkCode))
            {
                return (IntPtr)1;
            }
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        if (!isKeyDown)
        {
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        if (IsPureModifierKey(kb.vkCode))
        {
            // Pressing Win opens Start Menu and steals focus, so commit buffer cleanly.
            if (kb.vkCode is Win32Api.VK_LWIN or Win32Api.VK_RWIN)
            {
                CommitAndClearBuffer();
            }
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        ReconcileModifiersWithAsyncState();

        bool isShift = _leftShiftDown || _rightShiftDown || IsAsyncKeyDown(Win32Api.VK_SHIFT);
        bool isCtrl = _leftCtrlDown || _rightCtrlDown || IsAsyncKeyDown(Win32Api.VK_CONTROL);
        bool isAlt = _leftAltDown || _rightAltDown || (kb.flags & Win32Api.LLKHF_ALTDOWN) != 0 || IsAsyncKeyDown(Win32Api.VK_MENU);
        bool isWin = _leftWinDown || _rightWinDown || IsAsyncKeyDown(Win32Api.VK_LWIN) || IsAsyncKeyDown(Win32Api.VK_RWIN);

        // Hotkey Toggle: Ctrl + Space OR Alt + Z
        bool isCtrlSpace = isCtrl && !isAlt && !isWin && !isShift && kb.vkCode == Win32Api.VK_SPACE;
        bool isAltZ = isAlt && !isCtrl && !isWin && !isShift && kb.vkCode == Win32Api.VK_Z;

        if (isCtrlSpace || isAltZ)
        {
            ToggleMode();
            _suppressedKeyUps.Add(kb.vkCode);
            return (IntPtr)1;
        }

        // English Pass-Through Mode
        if (!_isSinglishEnabled)
        {
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        // Any Ctrl / Alt / Win shortcut (Ctrl+C, Ctrl+V, Ctrl+A, Ctrl+Z, Alt+Tab, etc.)
        if (isCtrl || isAlt || isWin)
        {
            CommitAndClearBuffer();
            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        // Candidate selection via F1, F2, F3 when buffer is active
        if (kb.vkCode is Win32Api.VK_F1 or Win32Api.VK_F2 or Win32Api.VK_F3 &&
            _inputInjector.CurrentRomanBuffer.Length > 0)
        {
            int candidateIndex = (int)(kb.vkCode - Win32Api.VK_F1);
            if (candidateIndex < _inputInjector.CurrentCandidates.Count)
            {
                _inputInjector.SelectCandidate(candidateIndex);
                RaiseBufferChanged();
                _suppressedKeyUps.Add(kb.vkCode);
                return (IntPtr)1;
            }
        }

        // Backspace differential deletion
        if (kb.vkCode == Win32Api.VK_BACK)
        {
            if (_inputInjector.HandleBackspace())
            {
                RaiseBufferChanged();
                _suppressedKeyUps.Add(kb.vkCode);
                return (IntPtr)1;
            }

            return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        // Printable ASCII letters A-Z / a-z
        if (kb.vkCode >= Win32Api.VK_A && kb.vkCode <= Win32Api.VK_Z)
        {
            bool isCapsLockOn = (Win32Api.GetKeyState((int)Win32Api.VK_CAPITAL) & 0x0001) != 0;
            bool isUpper = isShift ^ isCapsLockOn;
            char ch = (char)(isUpper ? kb.vkCode : kb.vkCode + 32);

            _inputInjector.AppendCharacter(ch);
            RaiseBufferChanged();
            _suppressedKeyUps.Add(kb.vkCode);
            return (IntPtr)1;
        }

        // Contextual '~' modifier (Shift + OEM_3) for a~, i~, u~, e~, o~, N~
        if (kb.vkCode == Win32Api.VK_OEM_3 && isShift && _inputInjector.CanAcceptTilde())
        {
            _inputInjector.AppendCharacter('~');
            RaiseBufferChanged();
            _suppressedKeyUps.Add(kb.vkCode);
            return (IntPtr)1;
        }

        // All other keys (Space, Enter, Tab, Escape, Arrows, punctuation, digits, etc.) reset buffer & pass through
        CommitAndClearBuffer();
        return Win32Api.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr LowLevelMouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isSinglishEnabled && _inputInjector.CurrentRomanBuffer.Length > 0)
        {
            int msg = wParam.ToInt32();
            if (msg is Win32Api.WM_LBUTTONDOWN or Win32Api.WM_RBUTTONDOWN or Win32Api.WM_MBUTTONDOWN or Win32Api.WM_XBUTTONDOWN)
            {
                Win32Api.MSLLHOOKSTRUCT mouse = Marshal.PtrToStructure<Win32Api.MSLLHOOKSTRUCT>(lParam);
                IntPtr clickedWindow = Win32Api.WindowFromPoint(mouse.pt);
                IntPtr rootWindow = clickedWindow != IntPtr.Zero
                    ? Win32Api.GetAncestor(clickedWindow, Win32Api.GA_ROOT)
                    : IntPtr.Zero;

                bool clickedOnHud = IsHudExcludedWindow is not null &&
                                    (IsHudExcludedWindow(clickedWindow) || IsHudExcludedWindow(rootWindow));

                if (!clickedOnHud)
                {
                    CommitAndClearBuffer();
                }
            }
        }

        return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private void UpdateModifierTracking(uint vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (!isKeyDown && !isKeyUp)
        {
            return;
        }

        bool state = isKeyDown;
        switch (vkCode)
        {
            case Win32Api.VK_LSHIFT: _leftShiftDown = state; break;
            case Win32Api.VK_RSHIFT: _rightShiftDown = state; break;
            case Win32Api.VK_SHIFT: _leftShiftDown = state; break;
            case Win32Api.VK_LCONTROL: _leftCtrlDown = state; break;
            case Win32Api.VK_RCONTROL: _rightCtrlDown = state; break;
            case Win32Api.VK_CONTROL: _leftCtrlDown = state; break;
            case Win32Api.VK_LMENU: _leftAltDown = state; break;
            case Win32Api.VK_RMENU: _rightAltDown = state; break;
            case Win32Api.VK_MENU: _leftAltDown = state; break;
            case Win32Api.VK_LWIN: _leftWinDown = state; break;
            case Win32Api.VK_RWIN: _rightWinDown = state; break;
        }
    }

    private void ReconcileModifiersWithAsyncState()
    {
        if (_leftShiftDown && !IsAsyncKeyDown(Win32Api.VK_LSHIFT) && !IsAsyncKeyDown(Win32Api.VK_SHIFT))
        {
            _leftShiftDown = false;
        }
        if (_rightShiftDown && !IsAsyncKeyDown(Win32Api.VK_RSHIFT) && !IsAsyncKeyDown(Win32Api.VK_SHIFT))
        {
            _rightShiftDown = false;
        }
        if (_leftCtrlDown && !IsAsyncKeyDown(Win32Api.VK_LCONTROL) && !IsAsyncKeyDown(Win32Api.VK_CONTROL))
        {
            _leftCtrlDown = false;
        }
        if (_rightCtrlDown && !IsAsyncKeyDown(Win32Api.VK_RCONTROL) && !IsAsyncKeyDown(Win32Api.VK_CONTROL))
        {
            _rightCtrlDown = false;
        }
        if (_leftAltDown && !IsAsyncKeyDown(Win32Api.VK_LMENU) && !IsAsyncKeyDown(Win32Api.VK_MENU))
        {
            _leftAltDown = false;
        }
        if (_rightAltDown && !IsAsyncKeyDown(Win32Api.VK_RMENU) && !IsAsyncKeyDown(Win32Api.VK_MENU))
        {
            _rightAltDown = false;
        }
        if (_leftWinDown && !IsAsyncKeyDown(Win32Api.VK_LWIN))
        {
            _leftWinDown = false;
        }
        if (_rightWinDown && !IsAsyncKeyDown(Win32Api.VK_RWIN))
        {
            _rightWinDown = false;
        }
    }

    private static bool IsAsyncKeyDown(uint vk) => (Win32Api.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static bool IsPureModifierKey(uint vkCode) => vkCode is
        Win32Api.VK_SHIFT or Win32Api.VK_LSHIFT or Win32Api.VK_RSHIFT or
        Win32Api.VK_CONTROL or Win32Api.VK_LCONTROL or Win32Api.VK_RCONTROL or
        Win32Api.VK_MENU or Win32Api.VK_LMENU or Win32Api.VK_RMENU or
        Win32Api.VK_CAPITAL or Win32Api.VK_LWIN or Win32Api.VK_RWIN;

    private void RaiseBufferChanged()
    {
        BufferChanged?.Invoke(new InputBufferSnapshot(
            _isSinglishEnabled,
            _inputInjector.CurrentRomanBuffer,
            _inputInjector.RenderedSinhalaText,
            _inputInjector.CurrentCandidates));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_keyboardHookId != IntPtr.Zero)
        {
            Win32Api.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        if (_mouseHookId != IntPtr.Zero)
        {
            Win32Api.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
    }
}
