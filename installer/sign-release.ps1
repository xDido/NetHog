$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$releaseArchives = @(Get-ChildItem -LiteralPath (Join-Path $root "artifacts") -Filter "release-v*.zip" -File)
$privateKeyPem = $env:NETHOG_UPDATE_SIGNING_KEY

if ([string]::IsNullOrWhiteSpace($privateKeyPem)) {
    throw "NETHOG_UPDATE_SIGNING_KEY is not set. Add the RSA private key as a GitHub Actions secret before publishing."
}

if ($releaseArchives.Count -eq 0) {
    throw "No release archive was found in the artifacts directory."
}

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($privateKeyPem)
    foreach ($archive in $releaseArchives) {
        $hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $stream = [System.IO.File]::OpenRead($archive.FullName)
            try {
                $hash = $hashAlgorithm.ComputeHash($stream)
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $hashAlgorithm.Dispose()
        }

        $signature = $rsa.SignHash(
            $hash,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $signaturePath = "$($archive.FullName).sig"
        [System.IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature))
        Write-Host "Signed $($archive.Name)" -ForegroundColor Green
    }
}
finally {
    $rsa.Dispose()
}
