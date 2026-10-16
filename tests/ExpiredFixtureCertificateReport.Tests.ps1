$ErrorActionPreference = 'Stop'
$reportScript = Join-Path $PSScriptRoot '..\scripts\report-expired-fixture-certificates.ps1'
. $reportScript

$now = [DateTimeOffset]::Parse('2026-10-16T12:00:00Z')
$checks = 0

function Assert-Candidate {
    param([string] $Name, [string] $Subject, [DateTimeOffset] $NotAfter, [bool] $Expected)

    $actual = Get-ExpiredFixtureCertificateCandidate `
        -Store 'CurrentUser\CA' -Subject $Subject -Thumbprint '001122' `
        -NotAfter $NotAfter -UtcNow $now
    if (($null -ne $actual) -ne $Expected) {
        throw "${Name}: expected candidate=$Expected, actual candidate=$($null -ne $actual)."
    }
    if ($null -ne $actual -and $actual.Store -ne 'CurrentUser\CA') {
        throw "${Name}: report omitted the store name."
    }
    $script:checks++
}

Assert-Candidate 'expired fixture authority' 'CN=Imposter Root 01234567' $now.AddTicks(-1) $true
Assert-Candidate 'unexpired fixture authority' 'CN=Unrelated Root 01234567' $now.AddTicks(1) $false
Assert-Candidate 'offset expiry boundary' 'CN=Vouchfx AIA Root 01234567' ([DateTimeOffset]::Parse('2026-10-16T13:00:00+01:00')) $false
Assert-Candidate 'unknown subject' 'CN=Example Test Root 01234567' $now.AddDays(-1) $false
Assert-Candidate 'similar lookalike' 'CN=Vouchfx Test Root CA Backup 01234567' $now.AddDays(-1) $false
Assert-Candidate 'long token lookalike' 'CN=Imposter Root 0123456789' $now.AddDays(-1) $false
Assert-Candidate 'prefix inside nested DN' 'OU=Fixture, CN=Imposter Root 01234567' $now.AddDays(-1) $false
Assert-Candidate 'hosted issuer prefix' 'CN=Vouchfx Hosted TLS Issuer abcdef0123456789abcdef0123456789 01234567' $now.AddDays(-1) $true
Assert-Candidate 'guard drill prefix' 'CN=Vouchfx Guard Drill 01234567' $now.AddDays(-1) $true

if ($checks -ne 9) { throw "Expected 9 classifier checks, ran $checks." }
Write-Output "Expired fixture certificate classifier: $checks checks passed."
