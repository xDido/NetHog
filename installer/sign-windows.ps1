param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

$pfxBase64 = $env:NETHOG_CODESIGN_PFX_BASE64
$pfxPassword = $env:NETHOG_CODESIGN_PFX_PASSWORD
$certificateThumbprint = $env:NETHOG_CODESIGN_CERT_THUMBPRINT
$usePfx = -not [string]::IsNullOrWhiteSpace($pfxBase64) -and -not [string]::IsNullOrWhiteSpace($pfxPassword)

if (-not $usePfx -and [string]::IsNullOrWhiteSpace($certificateThumbprint)) {
    $localCertificate = Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq "CN=NetHog Development" -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($localCertificate) {
        $certificateThumbprint = $localCertificate.Thumbprint
    }
}

if (-not $usePfx -and [string]::IsNullOrWhiteSpace($certificateThumbprint)) {
    if ($RequireSignature) {
        throw "Signing is required, but no PFX or local NetHog certificate was configured."
    }
    Write-Host "Authenticode signing skipped: configure a PFX certificate or run setup-local-signing.ps1." -ForegroundColor Yellow
    return
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @()
    $sdkRoot = $env:WindowsSdkDir
    if (-not [string]::IsNullOrWhiteSpace($sdkRoot)) {
        $roots += Join-Path $sdkRoot "bin"
    }
    $roots += Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\.windows-sdk-tools"
    foreach ($root in $roots | Where-Object { Test-Path -LiteralPath $_ }) {
        $candidate = Get-ChildItem -LiteralPath $root -Filter "signtool.exe" -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "signtool.exe was not found. Install the Windows SDK or add its x64 bin directory to PATH."
}

$signTool = Find-SignTool
$temporaryPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("nethog-codesign-{0}.pfx" -f [Guid]::NewGuid())
try {
    if ($usePfx) {
        [System.IO.File]::WriteAllBytes($temporaryPfx, [Convert]::FromBase64String($pfxBase64))
    }
    $timestampUrl = if ([string]::IsNullOrWhiteSpace($env:NETHOG_CODESIGN_TIMESTAMP_URL)) {
        "http://timestamp.digicert.com"
    } else {
        $env:NETHOG_CODESIGN_TIMESTAMP_URL
    }

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Cannot sign missing file: $path"
        }

        if ($usePfx) {
            & $signTool sign /fd SHA256 /f $temporaryPfx /p $pfxPassword /tr $timestampUrl /td SHA256 /d "NetHog" $path
        } else {
            # Development certificates are local-only and cannot be timestamped by a public TSA.
            & $signTool sign /fd SHA256 /sha1 $certificateThumbprint /d "NetHog" $path
        }
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $path (exit code $LASTEXITCODE)." }

        & $signTool verify /pa /all $path
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $path (exit code $LASTEXITCODE)." }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPfx) {
        Remove-Item -LiteralPath $temporaryPfx -Force -ErrorAction SilentlyContinue
    }
}
