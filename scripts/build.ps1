<#
.SYNOPSIS
    Builds the local publish\TandemHDR.exe and cleans up the intermediates.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
try {
    dotnet publish "$root\TandemHDR\TandemHDR.csproj" -c Release -o "$root\publish"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally {
    Remove-Item "$root\TandemHDR\bin", "$root\TandemHDR\obj" -Recurse -Force -ErrorAction SilentlyContinue
}
