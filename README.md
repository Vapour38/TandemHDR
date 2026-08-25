# Tandem HDR

A Windows system-tray utility that toggles HDR on and off and automatically forces the
right ICC display profile.

Windows 11 greatly improves the HDR experience, but switching between SDR and HDR can often result in the incorrect ICC profile being applied, resulting in washed-out colours. Tandem HDR keeps the two in sync, including when HDR is changed externally.

## Usage

The tray icon will reflect if HDR is on or off, and left-clicking will open the settings menu. 
On toggle, the configured SDR or HDR ICC profile will be applied and re-synced when external changes occur.
Profiles must be configured first through the settings dialogue, opened via right-click.

Per-program auto-switching can also be configured. When opened, configured games and apps will automatically switch to HDR and restore when exited. 
Games will be detected from standard game launcher installs, or can be picked from a recently used list. 

## Configuration

Configurations are stored in `config.json` beside the executable. See `config.example.json` for the shape.

`sdrProfilePath` and `hdrProfilePath` should point at your `.icc` / `.icm` profiles.

Log files are written out to `tandemhdr.log`.

## Licence

Copyright (C) 2026 Lachlan Dennis

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
