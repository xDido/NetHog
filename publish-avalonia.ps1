param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Runtime = "win-x64",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\NetHog.Avalonia\NetHog.Avalonia.csproj"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\avalonia-$Runtime"
}

dotnet publish $project -c Release -r $Runtime --self-contained true -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Avalonia publish failed with exit code $LASTEXITCODE." }
Write-Host "Avalonia $Runtime build written to $OutputPath" -ForegroundColor Green
