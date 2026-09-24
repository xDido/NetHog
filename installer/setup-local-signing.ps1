$ErrorActionPreference = "Stop"

$subject = "CN=NetHog Development"
$existingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Subject -eq $subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($existingCertificate) {
    $certificate = $existingCertificate
    Write-Host "Using existing local certificate: $($certificate.Thumbprint)" -ForegroundColor Green
} else {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -FriendlyName "NetHog Development Code Signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(3)
    Write-Host "Created local certificate: $($certificate.Thumbprint)" -ForegroundColor Green
}

foreach ($storeName in @("Root", "TrustedPublisher")) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "CurrentUser")
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $alreadyTrusted = $store.Certificates | Where-Object Thumbprint -eq $certificate.Thumbprint
        if (-not $alreadyTrusted) {
            $store.Add($certificate)
            Write-Host "Trusted certificate in CurrentUser\$storeName." -ForegroundColor Green
        }
    }
    finally {
        $store.Close()
    }
}

Write-Host "Local NetHog signing is ready. Future local MSI builds will sign NetHog.exe and the MSI automatically." -ForegroundColor Green
Write-Host "This certificate is trusted only on this Windows user account; public releases need a CA-issued certificate." -ForegroundColor Yellow
