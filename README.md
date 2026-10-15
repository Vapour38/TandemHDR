<div align="center">

<img src="docs/logo.png" alt="Tandem HDR logo" width="120">

# Tandem HDR

**One-click HDR toggle for the Windows tray, with the right ICC profile applied every time.**

[![Latest release](https://img.shields.io/github/v/release/Vapour38/TandemHDR?style=flat-square)](https://github.com/Vapour38/TandemHDR/releases/latest)
![Platform: Windows 11 x64](https://img.shields.io/badge/platform-Windows%2011%20x64-0078d4?style=flat-square)

[Download latest](https://github.com/Vapour38/TandemHDR/releases/latest) · [Features](#features) · [Getting started](#getting-started) · [How it works](#how-it-works)

</div>

---

## Why Tandem HDR?

Windows 11 has greatly improved the HDR experience. But when you switch between SDR and HDR, Windows often leaves the wrong ICC profile applied, and colours look washed out - especially on displays with incorrectly mapped SDR gamma.

Tandem HDR keeps the display mode and the colour profile in sync. This includes changes you make outside the app, including the Win+Alt+B shortcut.

## Features

### HDR and colour profiles

- **One-click toggle.** Left-click the tray icon to switch HDR on or off.
- **Automatic ICC profile switching.** Set one profile for SDR and one for HDR. Tandem HDR applies the correct one for the active mode.
- **External change detection.** If HDR changes outside the app, the matching profile is applied again.
- **Profile guard.** Windows can reset the profile at any time, so Tandem HDR re-applies it on a timer, and again after sleep or a display change.
- **Gamma table support.** The `vcgt` calibration table of the profile is loaded into the GPU, so SDR gamma-correction profiles take effect.

### Per-program auto-switching

- **Games and apps switch HDR for you.** HDR turns on when a configured program starts, and reverts on exit.
- **Per-program profile.** Each program can use its own HDR profile.
- **Launcher scan.** Installed games are found from Steam, Epic, Ubisoft, EA and Xbox/Game Pass.
- **Correct executable.** The scanner finds the file that runs during play, avoiding launcher stubs and anti-cheat starters.
- **Recently run list.** Pick standalone installs, GOG games and emulators from a list of programs you ran recently.
- **Manual browse.** Choose any `.exe` yourself.
- **Safe manual override.** If you change HDR by hand during a game, Tandem HDR does not change it back.

### Tray and interface

- **Live tray icon.** The icon shows whether HDR is on or off.
- **Modern settings window.** Acrylic backdrop and light or dark theme from Windows. 
- **Toast notifications.** A notification tells you which profile was applied when HDR switches. You can turn these off. If a profile file is missing, a warning tells you once.
- **Start with Windows.** Tandem HDR starts quietly in the tray when you sign in.

### Updates

- **Self-update from GitHub releases.**
- **No installer, no server.**

## Getting started

### Requirements

- Windows 11 x64
- An HDR-capable display
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Windows will offer a download link on first run if it is not installed.

### Install

1. Download `TandemHDR-vX.Y.Z.zip` from the [latest release](https://github.com/Vapour38/TandemHDR/releases/latest).
2. Extract `TandemHDR.exe` to a folder you want to keep.
3. Run `TandemHDR.exe`. On first launch, the settings window will open.
4. Set your **SDR profile** and **HDR profile** to your `.icc` or `.icm` files.
5. Turn on **Start with Windows** if you want the app to run at sign-in.

> [!NOTE]
> The release is signed with a self-signed certificate. Windows SmartScreen can show a warning the first time. Choose **More info**, then **Run anyway**.

## Usage

| Action | Result |
| --- | --- |
| Left-click the tray icon | Toggle HDR on or off |
| Right-click the tray icon | Open the menu: Enable HDR, Auto-switch for programs, Settings, Quit |
| Start `TandemHDR.exe` by hand | Open the settings window |

### Auto-switching for games

1. Open **Settings**, then the **Programs** tab.
2. Add a game from the launcher list or the recently run list. You can also browse for a program.
3. Optionally choose an HDR profile for that program.
4. Start the game. HDR turns on. When you close the game, HDR will return to its previous state.

## Configuration

All options are in the settings window.

| Setting | Purpose |
| --- | --- |
| SDR profile / HDR profile | The `.icc` or `.icm` file to apply for each mode |
| Sync with external changes | Re-apply the profile when HDR changes outside the app |
| Start with Windows | Run at sign-in |
| Show notifications | Show a toast when a profile is applied |

Settings are stored as JSON in the registry under `HKCU\Software\Tandem HDR`.

## How it works

Tandem HDR is a small .NET 10 application. A WinForms host owns the tray icon. A WPF window provides the settings.

1. It reads the real HDR state of the display on a timer.
2. When the state changes, it applies the SDR or HDR ICC profile for that state.
3. It watches the process list for programs on your list. A program that starts forces HDR on. The last program that exits restores the earlier state.

## Building from source

```powershell
git clone https://github.com/Vapour38/TandemHDR.git
cd TandemHDR
dotnet publish TandemHDR/TandemHDR.csproj -c Release -r win-x64
```

## Acknowledgements

The tray icon was inspired by [HDRTray](https://github.com/res2k/HDRTray).

## Licence

MIT. Copyright (c) 2026. See [LICENSE](LICENSE) for the full text.
