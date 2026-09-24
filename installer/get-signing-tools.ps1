param(
    [string]$Version = "10.0.28000.2705"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$toolsPath = Join-Path $root "artifacts\.windows-sdk-tools"
$packagePath = Join-Path $toolsPath "package"
$archivePath = Join-Path $toolsPath "buildtools.zip"
$packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$Version/microsoft.windows.sdk.buildtools.$Version.nupkg"

if (Test-Path -LiteralPath $toolsPath) {
    Remove-Item -LiteralPath $toolsPath -Recurse -Force
}
New-Item -ItemType Directory -Path $toolsPath -Force | Out-Null

Write-Host "Downloading Microsoft.Windows.SDK.BuildTools $Version..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $packageUrl -OutFile $archivePath
Expand-Archive -LiteralPath $archivePath -DestinationPath $packagePath -Force

$signTool = Get-ChildItem -LiteralPath $packagePath -Filter "signtool.exe" -File -Recurse |
    Where-Object { $_.FullName -match "\\x64\\" } |
    Select-Object -First 1
if (-not $signTool) {
    throw "The downloaded Windows SDK Build Tools package does not contain x64 signtool.exe."
}

Write-Host "Windows signing tools are ready: $($signTool.FullName)" -ForegroundColor Green
