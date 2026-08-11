#Requires -Version 7.0
<#
.SYNOPSIS
    Generates the throwaway certificates that examples/security-mtls.e2e.yaml declares.

.DESCRIPTION
    ###########################################################################
    #  THIS IS A TEST CERTIFICATE AUTHORITY.  IT IS NOT SECURE.               #
    #                                                                         #
    #  Every private key it writes is unencrypted, world-generatable and      #
    #  reproducible by anyone who runs this file.  The authority signs        #
    #  anything asked of it and is trusted by nothing.  Do not install it,    #
    #  do not copy it into another project, and never present anything it     #
    #  issues to a system you did not create for the purpose.                 #
    ###########################################################################

    WHY A SCRIPT AND NOT A STEP.  vouchfx checks every path under a `security:`
    block — that it stays inside the suite directory, and that the file is
    actually there — BEFORE it starts a single container.  A `script.csharp`
    step runs long after that gate, so no step can create this material in
    time.  A real adopter has the same constraint and solves it the same way:
    the certificates come from the PKI (or from a pipeline step) before the
    suite is handed to vouchfx.  This script stands in for that.

    WHY .NET HERE AND openssl IN THE .sh SIBLING.  Each script uses the tool
    its own platform guarantees.  PowerShell 7 is built on .NET, so the
    certificate APIs below are already present on any Windows machine that can
    run this file — whereas openssl is not reliably installed there.  On Linux
    and macOS the reverse holds, which is what the .sh sibling uses.

    Both scripts write exactly the same seven files into the certificates
    directory, with the same owner-only permissions on the three that contain a
    private key, and neither writes anything else there.  One internal
    difference, stated because it is a security property rather than a detail:
    this script never puts the CA's own private key on disk at all — it lives in
    an RSA object for the length of the run — while the .sh must hand openssl a
    key file and therefore writes it to a temporary directory OUTSIDE the
    repository.

.PARAMETER Force
    Regenerate even when the existing material is still valid.

.EXAMPLE
    ./examples/security-mtls.setup.ps1

.EXAMPLE
    ./examples/security-mtls.setup.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$CaSubject     = 'Vouchfx Example mTLS CA'
$ClientSubject = 'vouchfx-example-client'
$ServerSubject = 'localhost'
$Days          = 30

$certsDir = Join-Path $PSScriptRoot 'security-mtls/certs'

# Staging sits BESIDE the destination so publishing is a rename within one filesystem
# rather than a cross-volume copy. See the publish step for why that matters.
$stagingDir = "$certsDir.new"

# The seven files the suite declares. Named once, checked once, listed once.
$outputs = @(
    'ca.pem',
    'client.pem', 'client-key.pem',
    'server.pem', 'server-key.pem',
    'kafka.keystore.pem', 'kafka.truststore.pem'
)

# ── Is the existing material still usable? ───────────────────────────────────
# Three questions, not one, because "the files are there" is the weakest of them.
#
#   1. every file present and non-empty;
#   2. both leaves actually CHAIN to the ca.pem sitting beside them — which catches a set
#      assembled from two different authorities (an interrupted regeneration over an
#      existing set used to be able to produce exactly that) and a hand-edited file;
#   3. the anchor is good for another day, so a set that is valid now but expires mid-run
#      is replaced before it is used rather than after it fails.
#
# (2) subsumes an expiry check on the leaves — chain building rejects an expired
# certificate — so only the renewal MARGIN in (3) is left to ask separately.
function Test-MaterialIsCurrent {
    foreach ($file in $outputs) {
        $path = Join-Path $certsDir $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        if ((Get-Item -LiteralPath $path).Length -eq 0) { return $false }
    }

    # CreateFromPem, not CreateFromPemFile: the latter also wants a PRIVATE KEY
    # and throws on an anchor that (correctly) carries none.
    try {
        $ca = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem(
            [System.IO.File]::ReadAllText((Join-Path $certsDir 'ca.pem')))
    }
    catch {
        return $false
    }

    try {
        if ($ca.NotAfter -le (Get-Date).AddDays(1)) { return $false }

        foreach ($leafFile in @('server.pem', 'client.pem')) {
            $leaf = $null
            $chain = $null
            try {
                $leaf = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem(
                    [System.IO.File]::ReadAllText((Join-Path $certsDir $leafFile)))

                $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
                # CustomRootTrust: validate against the ca.pem beside the leaf and NOTHING
                # else. Using the machine trust store would ask the wrong question — and
                # would answer it wrongly on a host where someone had installed a test root.
                $chain.ChainPolicy.TrustMode =
                    [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
                $chain.ChainPolicy.RevocationMode =
                    [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                $null = $chain.ChainPolicy.CustomTrustStore.Add($ca)

                if (-not $chain.Build($leaf)) { return $false }
            }
            catch {
                return $false
            }
            finally {
                if ($null -ne $chain) { $chain.Dispose() }
                if ($null -ne $leaf) { $leaf.Dispose() }
            }
        }

        return $true
    }
    finally {
        $ca.Dispose()
    }
}

# ── Narrow a private key to its owner ────────────────────────────────────────
# The POSIX script sets 0600; this is the same intent on whichever platform is running.
# Without it a key inherits the repository directory's ACL, which on a normal Windows
# checkout grants Authenticated Users write access — so the two scripts would not in fact
# be producing "the same seven files" on the property that matters most about three of them.
function Set-OwnerOnlyAccess {
    param([string] $Path)

    if (-not $IsWindows) {
        [System.IO.File]::SetUnixFileMode(
            $Path,
            [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
        return
    }

    $acl = Get-Acl -LiteralPath $Path
    # Stop inheriting, and do NOT copy the inherited rules down as explicit ones.
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($existing in @($acl.Access)) { $null = $acl.RemoveAccessRule($existing) }

    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetAccessRule(
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            $me,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Allow))

    Set-Acl -LiteralPath $Path -AclObject $acl
}

if (-not $Force -and (Test-MaterialIsCurrent)) {
    Write-Host "security-mtls: certificates in $certsDir are current — nothing to do."
    Write-Host 'security-mtls: pass -Force to regenerate them anyway.'
    exit 0
}

# ── Certificate helpers ──────────────────────────────────────────────────────

$serverAuthOid = '1.3.6.1.5.5.7.3.1'
$clientAuthOid = '1.3.6.1.5.5.7.3.2'

function Add-CaExtension {
    param([System.Security.Cryptography.X509Certificates.CertificateRequest] $Request)

    $Request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
            $true, $false, 0, $true))
    $Request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
    $Request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
            $Request.PublicKey, $false))
}

# Issues one leaf under $Issuer and returns its certificate plus its key in PEM.
# Both leaves carry serverAuth AND clientAuth: SChannel refuses a client
# certificate lacking clientAuth and a server certificate lacking serverAuth,
# and this pair has to work on whichever side it is used.
function New-Leaf {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Issuer,
        [string] $CommonName,
        [switch] $WithLocalhostSans,
        [datetime] $NotBefore,
        [datetime] $NotAfter
    )

    $key = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$CommonName", $key,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                $false, $false, 0, $true))
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))

        $ekus = [System.Security.Cryptography.OidCollection]::new()
        $null = $ekus.Add([System.Security.Cryptography.Oid]::new($serverAuthOid))
        $null = $ekus.Add([System.Security.Cryptography.Oid]::new($clientAuthOid))
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekus, $false))

        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
                $request.PublicKey, $false))

        if ($WithLocalhostSans) {
            $sans = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
            $sans.AddDnsName('localhost')
            $sans.AddIpAddress([System.Net.IPAddress]::Loopback)
            $request.CertificateExtensions.Add($sans.Build())
        }

        $serial = [byte[]]::new(8)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($serial)

        $signed = $request.Create($Issuer, $NotBefore, $NotAfter, $serial)
        try {
            return [pscustomobject]@{
                CertificatePem = $signed.ExportCertificatePem()
                PrivateKeyPem  = $key.ExportPkcs8PrivateKeyPem()
            }
        }
        finally {
            $signed.Dispose()
        }
    }
    finally {
        $key.Dispose()
    }
}

# ── Generate ─────────────────────────────────────────────────────────────────

Write-Host "security-mtls: generating a TEST certificate authority in $certsDir"

$notBefore = (Get-Date).ToUniversalTime().AddMinutes(-5)
$notAfter  = $notBefore.AddDays($Days)

# One authority signs both the API's certificate and the broker's, because that
# is what an internal PKI looks like: a single anchor the suite declares once as
# `caCert` and both secured targets are validated against.
$caKey = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $caRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$CaSubject", $caKey,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    Add-CaExtension -Request $caRequest

    $ca = $caRequest.CreateSelfSigned($notBefore, $notAfter)
    try {
        $caPem = $ca.ExportCertificatePem() + "`n"

        # CN and SANs are `localhost`/127.0.0.1 because that is the address the
        # engine reaches an engine-started container on, and the probe does not
        # relax hostname verification. A certificate for any other name fails.
        $server = New-Leaf -Issuer $ca -CommonName $ServerSubject -WithLocalhostSans `
            -NotBefore $notBefore -NotAfter $notAfter

        # No subject alternative name: this identity is never dialled, only
        # presented. Its common name is what nginx reports as $ssl_client_s_dn
        # and what the broker logs as its peer principal — the example's own
        # evidence of WHO was authenticated.
        $client = New-Leaf -Issuer $ca -CommonName $ClientSubject `
            -NotBefore $notBefore -NotAfter $notAfter
    }
    finally {
        $ca.Dispose()
    }
}
finally {
    $caKey.Dispose()
}

# Everything is written into a STAGING DIRECTORY and published by swapping the directory
# itself — not by moving seven files into the live one.
#
# The difference is the whole point rather than a refinement. Publishing file by file over
# an EXISTING set has an interruption window in which the directory holds a mixture of two
# certificate authorities: every file present, every file individually well-formed, and the
# leaves no longer chaining to the anchor beside them. Nothing downstream notices — the
# currency guard used to say "nothing to do", and the compile gate only checks that the
# files exist — so the reader's next run is the first thing to fail, at the handshake, with
# a message about TLS rather than about a half-written fixture.
#
# With a directory swap the only interruption states are "old set intact" and "no set at
# all", and the second regenerates on the next run. The guard's chain check above closes
# the same hole from the other side, for any set this script did not write.
$work = $stagingDir
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$null = New-Item -ItemType Directory -Path $work -Force
try {
    # LF endings and no BOM: every one of these files is read by a JVM or by
    # nginx inside a Linux container, and the broker's key store is concatenated
    # by hand below.
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    function Write-Pem {
        param([string] $Name, [string] $Content)
        [System.IO.File]::WriteAllText(
            (Join-Path $work $Name), ($Content -replace "`r`n", "`n"), $utf8NoBom)
    }

    Write-Pem 'ca.pem'         $caPem
    Write-Pem 'server.pem'     ($server.CertificatePem + "`n")
    Write-Pem 'server-key.pem' $server.PrivateKeyPem
    Write-Pem 'client.pem'     ($client.CertificatePem + "`n")
    Write-Pem 'client-key.pem' $client.PrivateKeyPem

    # Kafka has accepted `ssl.keystore.type=PEM` since 2.7. Its key store is ONE
    # file — the private key followed by the certificate — and its trust store is
    # simply the anchor. Using PEM rather than a JKS is what keeps this script
    # free of a JDK dependency: no keytool, no throwaway container.
    Write-Pem 'kafka.keystore.pem'   ($server.PrivateKeyPem + "`n" + $server.CertificatePem + "`n")
    Write-Pem 'kafka.truststore.pem' $caPem

    # Keys are narrowed BEFORE the swap, so they are never briefly readable at the live path.
    foreach ($key in @('client-key.pem', 'server-key.pem', 'kafka.keystore.pem')) {
        Set-OwnerOnlyAccess -Path (Join-Path $work $key)
    }

    if (Test-Path -LiteralPath $certsDir) {
        Remove-Item -LiteralPath $certsDir -Recurse -Force
    }

    Move-Item -LiteralPath $work -Destination $certsDir
}
catch {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host "security-mtls: wrote $($outputs.Count) files to $certsDir"
Write-Host 'security-mtls:   ca.pem              the private CA both services chain to'
Write-Host 'security-mtls:   client.pem/-key     the identity the suite presents'
Write-Host 'security-mtls:   server.pem/-key     nginx''s own certificate'
Write-Host 'security-mtls:   kafka.keystore.pem  the broker''s key store (key + certificate)'
Write-Host 'security-mtls:   kafka.truststore.pem the broker''s trust store (= ca.pem)'
Write-Host "security-mtls: valid for $Days days. THIS IS TEST MATERIAL — the keys are"
Write-Host 'security-mtls: unencrypted and the authority is trusted by nothing. Never reuse it.'
