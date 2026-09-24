$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root "src\NetHog\NetHog.csproj"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $dotnetCandidate = Join-Path ${env:ProgramFiles} "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $dotnetCandidate) { $dotnet = $dotnetCandidate }
}
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    throw "The .NET SDK was not found on PATH or under Program Files."
}
$buildPath = Join-Path $root "artifacts\.build"
$publishPath = Join-Path $buildPath "publish"
$outputPath = Join-Path $buildPath "installer"
$releasePath = Join-Path $buildPath "release"
$projectXml = [xml](Get-Content -LiteralPath $projectPath)
$version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0].ToString()
$releaseZip = Join-Path $root "artifacts\release-v$version.zip"

if (Test-Path -LiteralPath $buildPath) {
    Remove-Item -LiteralPath $buildPath -Recurse -Force
}

New-Item -ItemType Directory -Path $buildPath -Force | Out-Null

& $dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishPath

if ($LASTEXITCODE -ne 0) { throw "NetHog publish failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& $dotnet build (Join-Path $PSScriptRoot "NetHog.wixproj") `
    -c Release `
    -p:PublishDir=$publishPath `
    -p:ProductVersion=$version `
    -p:OutputPath=$outputPath

if ($LASTEXITCODE -ne 0) { throw "NetHog MSI build failed with exit code $LASTEXITCODE." }

if (Test-Path -LiteralPath $releasePath) {
    Remove-Item -LiteralPath $releasePath -Recurse -Force
}

New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
$latestReleasePath = Join-Path $releasePath "latest"
$installerReleasePath = Join-Path $releasePath "installer"
New-Item -ItemType Directory -Path $latestReleasePath -Force | Out-Null
New-Item -ItemType Directory -Path $installerReleasePath -Force | Out-Null
$portablePath = Join-Path $publishPath "NetHog.exe"
$msiFiles = @(Get-ChildItem -LiteralPath $outputPath -Filter "*.msi" -File)

if (-not (Test-Path -LiteralPath $portablePath)) { throw "Portable NetHog.exe was not produced." }
if ($msiFiles.Count -eq 0) { throw "NetHog MSI was not produced." }

$signingScript = Join-Path $PSScriptRoot "sign-windows.ps1"
& $signingScript -Paths (@($portablePath) + @($msiFiles | ForEach-Object { $_.FullName }))

Copy-Item -LiteralPath $portablePath -Destination $latestReleasePath -Force
$msiFiles | Copy-Item -Destination $installerReleasePath -Force

if (Test-Path -LiteralPath $releaseZip) {
    Remove-Item -LiteralPath $releaseZip -Force
}

Compress-Archive `
    -Path (Join-Path $releasePath "*") `
    -DestinationPath $releaseZip `
    -CompressionLevel Optimal
Remove-Item -LiteralPath $releasePath -Recurse -Force
Remove-Item -LiteralPath $buildPath -Recurse -Force

Write-Host "Release archive written to $releaseZip" -ForegroundColor Green
