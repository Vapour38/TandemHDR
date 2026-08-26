# Tandem HDR

A Windows system-tray utility that toggles HDR on and off and automatically forces the
right ICC display profile.

Windows 11 greatly improves the HDR experience, but switching between SDR and HDR can often result in the incorrect ICC profile being applied, resulting in washed-out colours. Tandem HDR keeps the two in sync, including when HDR is changed externally.

## Requirements

Windows 11 x64 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Windows offers a download link on first run if it is not installed.

## Usage

The tray icon will reflect if HDR is on or off, and left-clicking toggles HDR. Right-click opens the menu, with Settings and Quit.
On toggle, the configured SDR or HDR ICC profile will be applied and re-synced when external changes occur.
Profiles must be configured first through the settings window, opened from that menu — or by running `TandemHDR.exe` yourself, which opens it directly.

Per-program auto-switching can also be configured. When opened, configured games and apps will automatically switch to HDR and restore when exited. 
Games will be detected from standard game launcher installs, or can be picked from a recently used list. 

## Configuration

Everything is configured from the settings window; point the SDR and HDR profiles at your
`.icc` / `.icm` files there.

Settings are stored in the registry under `HKCU\Software\Tandem HDR`. The executable is
self-contained in the literal sense — it writes no files beside itself, so it can be run
from anywhere and removed by deleting it.

## Licence

MIT. 
Copyright (c) 2026

See [LICENSE](LICENSE) for the full text.
