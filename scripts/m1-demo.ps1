<#
.SYNOPSIS
    Milestone 1 (M1) Phase-Exit Evidence Demo Script

.DESCRIPTION
    Assembles and executes the four demonstrable Milestone 1 exit criteria for the steering review.
    Runs from a clean checkout and produces a reproducible, timestamped summary of all four pillars.
    This is the M1 phase-exit review package (task S02-D-02).

    Milestone 1 exit gates (MVP §8.1):
    1. Memory model sound over full Core-provider dependency closure, gated in CI.
    2. Provider contract exercised end-to-end by a reflectively-discovered reference provider.
    3. Event-stream schema + first JSON Schema draft exist; reporting exercised.
    4. Stub topology starts reliably; environment errors classified distinctly.

.PARAMETER IncludeDocker
    When set, pillar 4 (Docker orchestration tests) is executed. When not set, pillar 4 is SKIPPED.
    Default: $false

.PARAMETER MemoryIterations
    Number of iterations for the memory harness closure test.
    Default: 5000

.PARAMETER MemoryThresholdBytes
    Net-delta memory threshold (bytes) for the closure test.
    Default: 2000000

.PARAMETER ReliabilityRuns
    Number of health-gate reliability runs for Docker tests (sets $env:VOUCHFX_RELIABILITY_RUNS).
    Default: 50

.PARAMETER SkipBuild
    When set, skips restore and build, using pre-built binaries. Useful for re-running tests.
    Default: $false

.EXAMPLE
    # Run all four pillars (including Docker) with default parameters
    pwsh -File scripts/m1-demo.ps1 -IncludeDocker

.EXAMPLE
    # Run pillars 0-3 (no Docker) with custom memory iterations
    pwsh -File scripts/m1-demo.ps1 -MemoryIterations 2000

.EXAMPLE
    # Re-run tests without rebuilding
    pwsh -File scripts/m1-demo.ps1 -SkipBuild -IncludeDocker

.EXIT-CODE-CONTRACT
    - 0 (success): All non-skipped pillars PASSED.
    - 1 (failure): At least one non-skipped pillar FAILED or BUILD failed.
    - SKIPPED pillars do not affect exit code.

.PILLAR-CONTRACT
    Each pillar prints a clear banner, runs its proof, records PASS/FAIL/SKIPPED, and continues
    even if failed (so all evidence is collected). Final summary table shows each pillar's status
    and key metric. Summary JSON is emitted to stdout and optionally written to a results file.

#>

[CmdletBinding()]
param(
    [switch]$IncludeDocker,
    [int]$MemoryIterations = 5000,
    [long]$MemoryThresholdBytes = 2000000,
    [int]$ReliabilityRuns = 50,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

#region Helper Functions

function Write-Banner {
    param([string]$Text)
    Write-Host ""
    Write-Host "=== $Text ===" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Success {
    param([string]$Text)
    Write-Host "$Text" -ForegroundColor Green
}

function Write-Error-Message {
    param([string]$Text)
    Write-Host "$Text" -ForegroundColor Red
}

function Write-Warning-Message {
    param([string]$Text)
    Write-Host "$Text" -ForegroundColor Yellow
}

function Invoke-NativeCommand {
    <#
    .SYNOPSIS
    Invokes a native command and checks exit code; non-zero throws.
    #>
    param(
        [string]$Description,
        [scriptblock]$Command
    )
    Write-Host "  [*] $Description..." -ForegroundColor Gray
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "[$Description] exited with code $LASTEXITCODE"
    }
}

#endregion

#region Setup

# Resolve repo root from script location.
# Script is at: <repoRoot>/scripts/m1-demo.ps1
# $PSScriptRoot is: <repoRoot>/scripts
$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "vouchfx.sln"

Write-Host ""
Write-Host "Milestone 1 Phase-Exit Review Package" -ForegroundColor Cyan
Write-Host "Repository: $repoRoot"
Write-Host "Solution: $solutionPath"
Write-Host ""

# Verify solution exists.
if (-not (Test-Path $solutionPath)) {
    Write-Error-Message "ERROR: vouchfx.sln not found at $solutionPath"
    exit 1
}

# Track overall status.
$pillars = @{}
$overallSuccess = $true
$globalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

#endregion

#region Pillar 0: Restore & Build

if (-not $SkipBuild) {
    Write-Banner "Pillar 0: Restore & Build"
    $buildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Push-Location $repoRoot

        Invoke-NativeCommand "dotnet restore" { dotnet restore $solutionPath }
        Invoke-NativeCommand "dotnet build (Release)" { dotnet build $solutionPath -c Release --no-restore }

        $buildStopwatch.Stop()
        Write-Success "Pillar 0 PASSED (build+restore: $($buildStopwatch.Elapsed.TotalSeconds)s)"
        $pillars["Pillar 0: Build"] = @{
            Status = "PASS"
            Duration = $buildStopwatch.Elapsed
            Detail = ""
        }
    }
    catch {
        Write-Error-Message "Pillar 0 FAILED: $_"
        Write-Error-Message "Build failure blocks all downstream pillars. Aborting."
        Pop-Location
        exit 1
    }
    finally {
        Pop-Location
    }
} else {
    Write-Banner "Pillar 0: Build"
    Write-Warning-Message "SKIPPED (SkipBuild enabled)"
    $pillars["Pillar 0: Build"] = @{
        Status = "SKIPPED"
        Duration = [timespan]::Zero
        Detail = ""
    }
}

#endregion

#region Pillar 1: Memory Proof (closure harness)

Write-Banner "Pillar 1: Memory Model (Provider Closure)"
$p1Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    Push-Location $repoRoot

    $memoryHarnessProject = Join-Path $repoRoot "tests" "Platform.Engine.Compilation.MemoryHarness"
    Write-Host "  [*] Running closure harness (iterations=$MemoryIterations, threshold=$MemoryThresholdBytes bytes)..." -ForegroundColor Gray

    # Capture output.
    $output = @()
    & dotnet run -c Release --project $memoryHarnessProject -- --mode closure --iterations $MemoryIterations --threshold-bytes $MemoryThresholdBytes 2>&1 | Tee-Object -Variable output | Out-Null

    $exitCode = $LASTEXITCODE

    # Parse JSON result from last line starting with '{'.
    $jsonLine = $null
    foreach ($line in $output) {
        if ($line -match '^\{') {
            $jsonLine = $line
        }
    }

    if ($exitCode -eq 0 -and $jsonLine) {
        try {
            $result = $jsonLine | ConvertFrom-Json

            $passed = $result.passed -eq $true
            $contextReclaimed = $result.contextReclaimed -eq $true
            $netDeltaBytes = $result.netDeltaBytes

            if ($passed -and $contextReclaimed) {
                $p1Stopwatch.Stop()
                Write-Success "Pillar 1 PASSED"
                Write-Host "  ├─ passed: $passed"
                Write-Host "  ├─ contextReclaimed: $contextReclaimed"
                Write-Host "  └─ netDeltaBytes: $netDeltaBytes (threshold: $MemoryThresholdBytes)" -ForegroundColor Green

                $pillars["Pillar 1: Memory (Closure)"] = @{
                    Status = "PASS"
                    Duration = $p1Stopwatch.Elapsed
                    Detail = "netDelta=$netDeltaBytes bytes, threshold=$MemoryThresholdBytes bytes"
                    Metric = @{
                        netDeltaBytes = $netDeltaBytes
                        threshold = $MemoryThresholdBytes
                    }
                }
            } else {
                throw "Memory test failed: passed=$passed, contextReclaimed=$contextReclaimed"
            }
        }
        catch {
            $p1Stopwatch.Stop()
            throw "Failed to parse memory harness result: $_"
        }
    } else {
        $p1Stopwatch.Stop()
        throw "Memory harness exited with code $exitCode or no JSON result found"
    }
}
catch {
    Write-Error-Message "Pillar 1 FAILED: $_"
    $pillars["Pillar 1: Memory (Closure)"] = @{
        Status = "FAIL"
        Duration = $p1Stopwatch.Elapsed
        Detail = $_.Exception.Message
    }
    $overallSuccess = $false
}
finally {
    Pop-Location
}

#endregion

#region Pillar 2 & 3: Non-Docker Tests (unified)

Write-Banner "Pillar 2 & 3: Non-Docker Test Suite (Provider Contract + Schema + Reporting)"
$p23Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    Push-Location $repoRoot

    Write-Host "  [*] Running non-Docker test suite..." -ForegroundColor Gray
    Write-Host "    - Platform.Sdk.Tests (reference provider + registry)" -ForegroundColor Gray
    Write-Host "    - Platform.Engine.Abstractions.Tests (event schema)" -ForegroundColor Gray
    Write-Host "    - Platform.Engine.Compilation.Tests (JSON Schema)" -ForegroundColor Gray
    Write-Host "    - Platform.Engine.Reporting.Tests (terminal renderer)" -ForegroundColor Gray

    & dotnet test $solutionPath -c Release --no-build --filter "requires!=docker" 2>&1 | Out-Null

    if ($LASTEXITCODE -eq 0) {
        $p23Stopwatch.Stop()
        Write-Success "Pillars 2 & 3 PASSED"
        Write-Host "  ├─ Reference provider contract (end-to-end compilation + registry)" -ForegroundColor Green
        Write-Host "  ├─ Event-stream schema (scenario/step events)" -ForegroundColor Green
        Write-Host "  └─ Reporting + JSON Schema validation" -ForegroundColor Green

        $pillars["Pillar 2: Provider Contract"] = @{
            Status = "PASS"
            Duration = $p23Stopwatch.Elapsed
            Detail = "Reference provider e2e + reflective registry"
        }
        $pillars["Pillar 3: Schema & Reporting"] = @{
            Status = "PASS"
            Duration = $p23Stopwatch.Elapsed
            Detail = "Event schema + JSON Schema + terminal renderer"
        }
    } else {
        $p23Stopwatch.Stop()
        throw "Non-Docker test suite exited with code $LASTEXITCODE"
    }
}
catch {
    Write-Error-Message "Pillars 2 & 3 FAILED: $_"
    $pillars["Pillar 2: Provider Contract"] = @{
        Status = "FAIL"
        Duration = $p23Stopwatch.Elapsed
        Detail = $_.Exception.Message
    }
    $pillars["Pillar 3: Schema & Reporting"] = @{
        Status = "FAIL"
        Duration = $p23Stopwatch.Elapsed
        Detail = $_.Exception.Message
    }
    $overallSuccess = $false
}
finally {
    Pop-Location
}

#endregion

#region Pillar 4: Docker Orchestration Tests

if ($IncludeDocker) {
    Write-Banner "Pillar 4: Orchestration Reliability & Environment Error Classification (Docker)"
    $p4Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Push-Location $repoRoot

        # Set environment variable for reliability test count.
        $env:VOUCHFX_RELIABILITY_RUNS = $ReliabilityRuns
        Write-Host "  [*] Running Docker orchestration tests (reliability runs: $ReliabilityRuns)..." -ForegroundColor Gray

        & dotnet test tests/Platform.Engine.Orchestration.Tests -c Release --no-build --filter "requires=docker" 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            $p4Stopwatch.Stop()
            Write-Success "Pillar 4 PASSED"
            Write-Host "  ├─ Health-gate reliability (≥ $ReliabilityRuns consecutive clean startups)" -ForegroundColor Green
            Write-Host "  ├─ Stub topology startup < 90s (diagnostics)" -ForegroundColor Green
            Write-Host "  └─ Environment-error classification (distinct from Fail)" -ForegroundColor Green

            $pillars["Pillar 4: Orchestration (Docker)"] = @{
                Status = "PASS"
                Duration = $p4Stopwatch.Elapsed
                Detail = "Reliability=$ReliabilityRuns runs, health gates + env-error classification"
            }
        } else {
            $p4Stopwatch.Stop()
            throw "Docker orchestration tests exited with code $LASTEXITCODE"
        }
    }
    catch {
        Write-Error-Message "Pillar 4 FAILED: $_"
        $pillars["Pillar 4: Orchestration (Docker)"] = @{
            Status = "FAIL"
            Duration = $p4Stopwatch.Elapsed
            Detail = $_.Exception.Message
        }
        $overallSuccess = $false
    }
    finally {
        # Clean up env var.
        Remove-Item -Path "env:VOUCHFX_RELIABILITY_RUNS" -ErrorAction SilentlyContinue
        Pop-Location
    }
} else {
    Write-Banner "Pillar 4: Orchestration (Docker)"
    Write-Warning-Message "SKIPPED (Docker not requested; run with -IncludeDocker to include)"
    $pillars["Pillar 4: Orchestration (Docker)"] = @{
        Status = "SKIPPED"
        Duration = [timespan]::Zero
        Detail = ""
    }
}

#endregion

#region Summary

$globalStopwatch.Stop()

Write-Banner "Summary"

# Build summary table.
$summaryData = @()
foreach ($pillarName in $pillars.Keys | Sort-Object) {
    $pillar = $pillars[$pillarName]
    $statusColor = switch ($pillar.Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "SKIPPED" { "Yellow" }
        default { "White" }
    }

    $duration = $pillar.Duration.TotalSeconds
    $durationStr = "{0:F2}s" -f $duration

    Write-Host "  $pillarName`:" -ForegroundColor Cyan
    Write-Host "    Status:   " -NoNewline
    Write-Host $pillar.Status -ForegroundColor $statusColor
    Write-Host "    Duration: $durationStr"
    if ($pillar.Detail) {
        Write-Host "    Detail:   $($pillar.Detail)"
    }
    if ($pillar.Metric) {
        Write-Host "    Metrics:  $($pillar.Metric | ConvertTo-Json -Compress)"
    }
    Write-Host ""

    $summaryData += [PSCustomObject]@{
        Pillar = $pillarName
        Status = $pillar.Status
        DurationSeconds = $duration
        Detail = $pillar.Detail
    }
}

Write-Host "Total elapsed time: $($globalStopwatch.Elapsed.TotalSeconds)s" -ForegroundColor Cyan
Write-Host ""

# Machine-readable summary (JSON Lines).
$summaryJson = @{
    timestamp = [datetime]::UtcNow.ToString('o')
    repository = $repoRoot
    solution = $solutionPath
    totalElapsedSeconds = $globalStopwatch.Elapsed.TotalSeconds
    pillars = @{}
    overallStatus = if ($overallSuccess) { "PASS" } else { "FAIL" }
    exitCode = if ($overallSuccess) { 0 } else { 1 }
}

foreach ($pillar in $pillars.GetEnumerator()) {
    $summaryJson.pillars[$pillar.Key] = @{
        status = $pillar.Value.Status
        durationSeconds = $pillar.Value.Duration.TotalSeconds
        detail = $pillar.Value.Detail
    }
    if ($pillar.Value.Metric) {
        $summaryJson.pillars[$pillar.Key].metrics = $pillar.Value.Metric
    }
}

$summaryJsonString = ConvertTo-Json $summaryJson -Depth 5
Write-Host $summaryJsonString -ForegroundColor Gray
Write-Host ""

# Emit final result.
if ($overallSuccess) {
    Write-Success "✓ All non-skipped pillars PASSED. Exit code: 0"
    exit 0
} else {
    Write-Error-Message "✗ At least one pillar FAILED. Exit code: 1"
    exit 1
}

#endregion
