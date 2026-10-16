$ErrorActionPreference = 'Stop'

# Keep these prefixes aligned with CA subjects minted by TestCertificateAuthority,
# HostedHttpTlsCertificateBed, and the guard-teeth drill. A prefix is only a candidate
# identifier; it does not prove that a certificate belongs to this repository.
$script:FixtureSubjectPatterns = @(
    '^CN=Vouchfx Test Root CA [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx Test Foreign Root CA [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx Test Offline Root [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx Test Issuing Intermediate [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx AIA Root [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx AIA Intermediate [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx Hosted TLS (?:Root|Issuer|Unrelated) [0-9A-F]{32} [0-9A-F]{8}(?:,|$)'
    '^CN=Vouchfx Guard Drill [0-9A-F]{8}(?:,|$)'
    '^CN=Imposter Root [0-9A-F]{8}(?:,|$)'
    '^CN=Unrelated Root [0-9A-F]{8}(?:,|$)'
)

function Get-ExpiredFixtureCertificateCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Store,
        [Parameter(Mandatory)][string] $Subject,
        [Parameter(Mandatory)][string] $Thumbprint,
        [Parameter(Mandatory)][DateTimeOffset] $NotAfter,
        [Parameter(Mandatory)][DateTimeOffset] $UtcNow
    )

    $subjectMatches = $false
    $options = [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [Text.RegularExpressions.RegexOptions]::CultureInvariant
    foreach ($pattern in $script:FixtureSubjectPatterns) {
        if ([regex]::IsMatch($Subject, $pattern, $options)) {
            $subjectMatches = $true
            break
        }
    }

    # Equality is deliberately not expired. Normalize both instants before comparing.
    $expiryUtc = $NotAfter.ToUniversalTime()
    if (-not $subjectMatches -or $expiryUtc -ge $UtcNow.ToUniversalTime()) {
        return $null
    }

    [pscustomobject][ordered]@{
        Store = $Store
        Subject = $Subject
        Thumbprint = $Thumbprint
        NotAfterUtc = $expiryUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-ExpiredFixtureCertificateReport {
    [CmdletBinding()]
    param([DateTimeOffset] $UtcNow = [DateTimeOffset]::UtcNow)

    if (-not [OperatingSystem]::IsWindows()) {
        throw 'This report reads the Windows CurrentUser\CA certificate cache and is supported on Windows only.'
    }

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::CertificateAuthority,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $rows = [System.Collections.Generic.List[object]]::new()
        foreach ($certificate in $store.Certificates) {
            try {
                $row = Get-ExpiredFixtureCertificateCandidate `
                    -Store 'CurrentUser\CA' `
                    -Subject $certificate.Subject `
                    -Thumbprint $certificate.Thumbprint `
                    -NotAfter ([DateTimeOffset]$certificate.NotAfter.ToUniversalTime()) `
                    -UtcNow $UtcNow
                if ($null -ne $row) { $rows.Add($row) }
            }
            finally {
                $certificate.Dispose()
            }
        }

        $rows | Sort-Object NotAfterUtc, Subject, Thumbprint
    }
    finally {
        $store.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Get-ExpiredFixtureCertificateReport
}
