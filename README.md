# Tandem HDR

A Windows system-tray utility that toggles HDR on and off, and automatically applies the
right ICC display profile for whichever mode is active.

SDR and HDR generally need different calibrated profiles. Tools that only flip the HDR
switch leave you re-selecting a profile by hand every time. Tandem HDR keeps the two in
sync, including when HDR is changed from Windows Settings or by a game.

## Features

- Left-click the tray icon to toggle HDR; right-click for a native menu.
- Applies the configured SDR or HDR ICC profile on every switch.
- Detects HDR changes made outside the app and re-syncs the profile.
- Re-applies the active profile periodically, guarding against Windows resetting it.
- Per-program auto-switching: nominate executables that force HDR on while they run, and
  the previous HDR state is restored when the last one exits.
- Game picker that reads Steam, Epic, Ubisoft, EA and Xbox library records, so you choose
  a game from a list instead of hunting for its `.exe`. A "Recently run" tab covers games
  no launcher knows about — GOG, standalone installs, emulators.
- Optional start with Windows.

## Requirements

- Windows 10/11 with an HDR-capable display.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. The published binary is
  self-contained and needs no runtime installed.

## Build

```
dotnet publish src/TandemHDR/TandemHDR.csproj -c Release -o publish
```

This produces a single self-contained `publish/TandemHDR.exe` (win-x64). Run it from
anywhere; it lives in the tray.

To cut a release — bumps the version, commits, tags, builds, uploads the zip to GitHub
and removes all build output afterwards:

```
.\scriptselease.ps1 0.2.0
```

## Configuration

Settings are edited in the app's settings window (right-click the tray icon). They are
stored in `config.json` beside the executable — see `config.example.json` for the shape.
The two paths that matter are `sdrProfilePath` and `hdrProfilePath`, pointing at your
`.icc` / `.icm` profiles.

`tandemhdr.log` is written next to the executable as well.

## Project layout

```
src/TandemHDR/
  Configuration/   config load/save
  Controls/        shared WPF controls
  Native/          Win32 / display / colour-profile interop
  Services/        HDR, ICC, gamma, game scanning, process watching
  Settings/        settings and game-picker windows
  Theme/           brushes and control styles
```

## Prior art

Tray icon behaviour and the forced dark native context menu follow the approach taken by
[HDRTray](https://github.com/res2k/HDRTray).

## Licence

MIT — see [LICENSE](LICENSE).
