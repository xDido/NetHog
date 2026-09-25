param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Runtime = "win-x64",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\NetHog\NetHog.csproj"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\NetHog-$Runtime"
}

dotnet publish $project -c Release -r $Runtime --self-contained true -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "NetHog publish failed with exit code $LASTEXITCODE." }
Write-Host "NetHog $Runtime build written to $OutputPath" -ForegroundColor Green
