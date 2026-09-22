$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src\WifiBox\NetHog.csproj"
$outputPath = Join-Path $PSScriptRoot "artifacts\latest"

foreach ($processName in @("NetHog", "WifiBox")) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --source "https://api.nuget.org/v3/index.json" `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "NetHog publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published: $outputPath\NetHog.exe" -ForegroundColor Green
