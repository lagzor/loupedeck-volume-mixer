# Builds the plugin and links it into Logi Plugin Service.
#   Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1
#   With  -Package  it also creates DeviceVolumeMixer.lplug4 (double-click to install, e.g. on another PC).
param(
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ".NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.10" -ForegroundColor Red
    exit 1
}

if (-not (dotnet --list-sdks | Where-Object { $_ -match '^(1[0-9])\.' })) {
    Write-Host ".NET 10 SDK not found (Logi Plugin Service 6.4+ runs on .NET 10)." -ForegroundColor Red
    Write-Host "Install it with:  winget install Microsoft.DotNet.SDK.10" -ForegroundColor Red
    exit 1
}

$apiDll = 'C:\Program Files\Logi\LogiPluginService\PluginApi.dll'
if (-not (Test-Path $apiDll)) {
    Write-Host "Cannot find $apiDll" -ForegroundColor Red
    Write-Host "The plugin requires Logi Plugin Service 6.4 or later (Loupedeck software or Logi Options+)." -ForegroundColor Red
    exit 1
}

dotnet build .\src\DeviceVolumeMixerPlugin.csproj -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. The plugin is loaded from bin\Release (via a .link file)." -ForegroundColor Green
Write-Host "In Loupedeck: open the plugin list and enable 'Device Volume Mixer'."
Write-Host "If it doesn't show up: restart Logi Plugin Service in the settings."

if ($Package) {
    $out = Join-Path $PSScriptRoot 'DeviceVolumeMixer.lplug4'
    $zip = "$out.zip"
    Remove-Item $out, $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $PSScriptRoot 'bin\Release\*') -DestinationPath $zip
    Move-Item $zip $out
    Write-Host "Package created: $out" -ForegroundColor Green
}
