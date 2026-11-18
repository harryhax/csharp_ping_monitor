# Builds a self-contained single-file Windows executable.
# Usage: pwsh scripts/publish-windows.ps1 [-Rid win-x64|win-arm64]
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/$Rid"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish "$root/NetworkDiagnostics.App/NetworkDiagnostics.App.csproj" `
  -c Release -r $Rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o $out
Write-Host "Created: $out/NetworkDiagnostics.exe"
