# HelaType Windows

A lightweight Windows desktop input method that converts **Singlish (Romanized Sinhala)** into Sinhala Unicode as you type. HelaType runs in the background and works in most Windows text fields, including Antigravity, Notepad, browsers, and Microsoft Word.

> **Platform:** Windows 10/11, 64-bit. This is a WPF application targeting .NET 8 for Windows.

## Features

- Real-time Singlish-to-Sinhala phonetic transliteration.
- Global keyboard hook and Unicode text injection; no clipboard-based replacement.
- Floating preview HUD with Sinhala/English mode badge and up to three candidate suggestions.
- Candidate selection with **F1**, **F2**, and **F3**.
- Toggle input mode with **Ctrl+Space** or **Alt+Z**.
- Backspace-aware editing while composing a word.
- System tray controls, optional start-with-Windows setting, interactive cheat sheet, and sandbox pad.
- No third-party NuGet dependencies for the core transliteration engine.

## Requirements

- Windows 10 or later (64-bit).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build and run from source.

## Build and run

Open PowerShell in the project directory (the directory containing `HelaType.csproj`) and run:

```powershell
dotnet build
dotnet run
```

The app starts with a dashboard. Closing the dashboard hides it to the system tray; choose **Exit** from the tray menu to stop HelaType.

## Publish a self-contained single-file executable

For a typical x64 Windows PC:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable is created at:

```text
bin\Release\net8.0-windows\win-x64\publish\HelaType.exe
```

You can launch the published app directly; the .NET runtime does not need to be installed separately. To publish for Windows ARM64, replace `win-x64` with `win-arm64`.

## Using HelaType

1. Start HelaType and leave it running in the background.
2. Toggle to Sinhala mode with **Ctrl+Space** or **Alt+Z**. The HUD/tray badge indicates the active mode (`සිං` or `EN`).
3. Click into a text field and type a word phonetically. For example:
   - `kohomadha` → `කොහොමද`
   - `aayuboowan` → `ආයුබෝවන්`
   - `shrii langkaa` → `ශ්‍රී ලංකා`
4. If alternatives appear in the HUD, choose one with **F1–F3** or click its candidate button.
5. Space, Enter, Tab, punctuation, navigation keys, and mouse clicks finish the current composition. Use Backspace to edit it before committing.
6. Right-click the system tray icon for mode, startup, and exit controls. The dashboard includes a live sandbox and searchable phonetic cheat sheet.

### Antigravity / VS Code shortcut note

**Ctrl+Space** is also commonly used for code completion in VS Code-based editors, including Antigravity. HelaType uses that shortcut to switch input mode, so use **Alt+Z** to toggle modes while working in the editor. If you want Ctrl+Space to remain available for completion, remove the Ctrl+Space toggle condition in `Services/KeyboardHookService.cs`, then rebuild.

## Troubleshooting

- **The toggle shortcut conflicts with the editor:** Use **Alt+Z**, or adjust the Ctrl+Space hotkey in `Services/KeyboardHookService.cs` and rebuild.
- **Text is not injected into an application launched as Administrator:** Windows may block input from a non-elevated process. Prefer running both applications normally. If you must type into an elevated application, HelaType may also need to run elevated.
- **The HUD is not next to the caret:** Some applications use custom-rendered editors that do not expose standard caret coordinates. In that case, HelaType uses its fallback HUD position; drag the HUD to move it.
- **Sinhala glyphs look different:** The target application must use a Sinhala-capable font, such as Nirmala UI or Iskoola Pota.

## Project layout

```text
HelaType.csproj
App.xaml
App.xaml.cs
MainWindow.xaml
MainWindow.xaml.cs
Native/Win32Api.cs
Engine/PhoneticEngine.cs
Services/InputInjector.cs
Services/KeyboardHookService.cs
Services/TrayIconService.cs
UI/FloatingHudWindow.xaml
UI/FloatingHudWindow.xaml.cs
```

## Notes

- HelaType uses low-level keyboard and mouse hooks while running so it can process typing globally. Quit from the tray menu when you do not want it active.
- This project has not been live-built or runtime-tested on Windows in the current development environment. Please run `dotnet build` and test the published executable on a Windows machine before distributing it.
- The published executable is not an installer and is not code-signed by this project.
