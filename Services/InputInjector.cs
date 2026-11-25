using System;
using System.Collections.Generic;
using HelaType.Engine;
using HelaType.Native;

namespace HelaType.Services;

public sealed class InputInjector
{
    private readonly object _syncLock = new();

    private string _currentRomanBuffer = string.Empty;
    private string _renderedSinhalaText = string.Empty;
    private IReadOnlyList<PhoneticCandidate> _currentCandidates = Array.Empty<PhoneticCandidate>();
    private volatile bool _isInjecting;

    public bool IsInjecting => _isInjecting;

    public string CurrentRomanBuffer
    {
        get
        {
            lock (_syncLock)
            {
                return _currentRomanBuffer;
            }
        }
    }

    public string RenderedSinhalaText
    {
        get
        {
            lock (_syncLock)
            {
                return _renderedSinhalaText;
            }
        }
    }

    public IReadOnlyList<PhoneticCandidate> CurrentCandidates
    {
        get
        {
            lock (_syncLock)
            {
                return _currentCandidates;
            }
        }
    }

    public bool CanAcceptTilde()
    {
        lock (_syncLock)
        {
            if (_currentRomanBuffer.Length == 0)
            {
                return false;
            }

            char last = _currentRomanBuffer[^1];
            return last is 'a' or 'i' or 'u' or 'e' or 'o' or 'N';
        }
    }

    public void AppendCharacter(char ch)
    {
        lock (_syncLock)
        {
            _currentRomanBuffer += ch;
            _currentCandidates = PhoneticEngine.GetCandidates(_currentRomanBuffer);
            string newSinhala = _currentCandidates.Count > 0
                ? _currentCandidates[0].SinhalaText
                : PhoneticEngine.Transliterate(_currentRomanBuffer);

            ApplyDifferentialUpdateLocked(newSinhala);
        }
    }

    public bool HandleBackspace()
    {
        lock (_syncLock)
        {
            if (_currentRomanBuffer.Length == 0)
            {
                return false;
            }

            _currentRomanBuffer = _currentRomanBuffer[..^1];

            if (_currentRomanBuffer.Length == 0)
            {
                _currentCandidates = Array.Empty<PhoneticCandidate>();
                ApplyDifferentialUpdateLocked(string.Empty);
            }
            else
            {
                _currentCandidates = PhoneticEngine.GetCandidates(_currentRomanBuffer);
                string newSinhala = _currentCandidates.Count > 0
                    ? _currentCandidates[0].SinhalaText
                    : PhoneticEngine.Transliterate(_currentRomanBuffer);
                ApplyDifferentialUpdateLocked(newSinhala);
            }

            return true;
        }
    }

    public bool SelectCandidate(int index)
    {
        lock (_syncLock)
        {
            if (_currentRomanBuffer.Length == 0 || index < 0 || index >= _currentCandidates.Count)
            {
                return false;
            }

            PhoneticCandidate chosen = _currentCandidates[index];
            _currentRomanBuffer = chosen.RomanEquivalent;
            ApplyDifferentialUpdateLocked(chosen.SinhalaText);
            _currentCandidates = PhoneticEngine.GetCandidates(_currentRomanBuffer);
            return true;
        }
    }

    public void ClearBuffer()
    {
        lock (_syncLock)
        {
            _currentRomanBuffer = string.Empty;
            _renderedSinhalaText = string.Empty;
            _currentCandidates = Array.Empty<PhoneticCandidate>();
        }
    }

    private void ApplyDifferentialUpdateLocked(string newSinhala)
    {
        int minLength = Math.Min(_renderedSinhalaText.Length, newSinhala.Length);
        int commonPrefixLength = 0;

        while (commonPrefixLength < minLength &&
               _renderedSinhalaText[commonPrefixLength] == newSinhala[commonPrefixLength])
        {
            commonPrefixLength++;
        }

        int backspacesNeeded = _renderedSinhalaText.Length - commonPrefixLength;
        string suffixToAppend = newSinhala[commonPrefixLength..];

        int totalKeyEvents = (backspacesNeeded + suffixToAppend.Length) * 2;
        if (totalKeyEvents == 0)
        {
            _renderedSinhalaText = newSinhala;
            return;
        }

        var inputs = new Win32Api.INPUT[totalKeyEvents];
        int idx = 0;

        for (int b = 0; b < backspacesNeeded; b++)
        {
            inputs[idx++] = CreateVirtualKeyInput((ushort)Win32Api.VK_BACK, isKeyUp: false);
            inputs[idx++] = CreateVirtualKeyInput((ushort)Win32Api.VK_BACK, isKeyUp: true);
        }

        foreach (char c in suffixToAppend)
        {
            inputs[idx++] = CreateUnicodeInput(c, isKeyUp: false);
            inputs[idx++] = CreateUnicodeInput(c, isKeyUp: true);
        }

        _isInjecting = true;
        try
        {
            Win32Api.SendInput((uint)inputs.Length, inputs, Win32Api.INPUT.Size);
        }
        finally
        {
            _isInjecting = false;
        }

        _renderedSinhalaText = newSinhala;
    }

    private static Win32Api.INPUT CreateVirtualKeyInput(ushort vk, bool isKeyUp) => new()
    {
        type = Win32Api.INPUT_KEYBOARD,
        U = new Win32Api.INPUTUNION
        {
            ki = new Win32Api.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = isKeyUp ? Win32Api.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = Win32Api.HELA_INJECT_SIGNATURE
            }
        }
    };

    private static Win32Api.INPUT CreateUnicodeInput(char ch, bool isKeyUp) => new()
    {
        type = Win32Api.INPUT_KEYBOARD,
        U = new Win32Api.INPUTUNION
        {
            ki = new Win32Api.KEYBDINPUT
            {
                wVk = 0,
                wScan = (ushort)ch,
                dwFlags = Win32Api.KEYEVENTF_UNICODE | (isKeyUp ? Win32Api.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = Win32Api.HELA_INJECT_SIGNATURE
            }
        }
    };
}
