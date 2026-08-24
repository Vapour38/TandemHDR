<#
.SYNOPSIS
    Builds, packages and publishes a Tandem HDR release.
.EXAMPLE
    .\scripts\release.ps1 0.2.0
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $csproj = 'TandemHDR\TandemHDR.csproj'
    $stage  = "$env:TEMP\TandemHDR-v$Version"
    $zip    = "$env:TEMP\TandemHDR-v$Version.zip"

    if (git status --porcelain) { throw 'Working tree is dirty. Commit or stash first.' }

    (Get-Content $csproj -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" |
        Set-Content $csproj -Encoding utf8 -NoNewline
    git commit -q -am "Release v$Version"
    git push -q origin HEAD

    dotnet publish $csproj -c Release -o publish
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    Remove-Item $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item 'publish\TandemHDR.exe', 'LICENSE' $stage
    Copy-Item 'config.example.json' "$stage\config.json"
    Compress-Archive "$stage\*" $zip -CompressionLevel Optimal

    if (-not $Notes) {
        $Notes = @"
Self-contained Windows x64 build — no .NET runtime required.

Download ``TandemHDR-v$Version.zip``, extract it anywhere, and run ``TandemHDR.exe``. The archive contains:

- ``TandemHDR.exe``
- ``config.json`` — default settings; set your SDR/HDR ICC profile paths here or in the tray icon's settings window
- ``LICENSE``
"@
    }
    gh release create "v$Version" $zip --title "v$Version" --notes $Notes --target (git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
}
finally {
    Remove-Item "$root\publish", "$root\TandemHDR\bin", "$root\TandemHDR\obj", $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue
    Pop-Location
}
