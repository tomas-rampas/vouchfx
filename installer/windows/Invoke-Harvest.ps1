<#
.SYNOPSIS
    Generates installer\windows\AppFiles.wxs from a dotnet publish output directory.

.DESCRIPTION
    WiX v4.0.x does not support the Files/glob harvesting element.  This script
    enumerates all files in the publish directory and produces a WXS Fragment
    containing one Component per file, all nested correctly in a <DirectoryRef>
    subtree under INSTALLDIR.

    Satellite-assembly subdirectories (cs/, de/, etc.) are declared as nested
    <Directory> elements to avoid ICE30 duplicate-destination violations.

    The generated AppFiles.wxs is fed to wix build alongside vouchfx.wxs.

.PARAMETER SourceDir
    Path to the self-contained publish directory (win-x64 output of dotnet publish).

.PARAMETER OutputFile
    Path for the generated WXS file.  Defaults to installer\windows\AppFiles.wxs
    relative to the script's location.

.EXAMPLE
    # From the repo root:
    pwsh installer\windows\Invoke-Harvest.ps1 -SourceDir publish\win-x64

.EXAMPLE
    # Explicit output path:
    pwsh installer\windows\Invoke-Harvest.ps1 `
        -SourceDir D:\artifacts\publish\win-x64 `
        -OutputFile D:\work\AppFiles.wxs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$SourceDir,

    [string]$OutputFile = (Join-Path $PSScriptRoot 'AppFiles.wxs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolved = (Resolve-Path $SourceDir).Path.TrimEnd('\', '/')
$files    = Get-ChildItem -LiteralPath $resolved -Recurse -File | Sort-Object FullName

if ($files.Count -eq 0) {
    Write-Error "No files found under $resolved"
}

Write-Host "Harvesting $($files.Count) files from: $resolved"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Convert a relative path to a valid WiX Id (alphanumeric + underscore, starts
# with a letter).  Leading digits are prefixed with 'F_'.
function ConvertTo-WixId([string]$relativePath) {
    $safe = $relativePath -replace '[^a-zA-Z0-9]', '_'
    if ($safe -match '^[^a-zA-Z]') { $safe = "F_$safe" }
    # Truncate to 72 chars (WiX limit) using a suffix hash to avoid collisions.
    if ($safe.Length -gt 72) {
        $hash = ([System.Security.Cryptography.SHA256]::Create().ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes($relativePath)) |
            ForEach-Object { $_.ToString('x2') }) -join ''
        $safe = $safe.Substring(0, 60) + '_' + $hash.Substring(0, 8)
    }
    return $safe
}

# ---------------------------------------------------------------------------
# Build a directory tree from all file paths (ignoring root-level files).
# Tree is a hashtable of name -> node, where node = @{ id, name, children }
# ---------------------------------------------------------------------------
$dirTree = [ordered]@{}

function Add-ToTree {
    param([System.Collections.Specialized.OrderedDictionary]$Tree,
          [string[]]$DirParts,
          [string]$RelPrefix)
    if ($DirParts.Count -eq 0) { return }
    $name = $DirParts[0]
    if (-not $Tree.Contains($name)) {
        $relPath  = if ($RelPrefix) { "$RelPrefix\$name" } else { $name }
        $Tree[$name] = [ordered]@{
            id       = 'dir_' + (ConvertTo-WixId $relPath)
            name     = $name
            relPath  = $relPath
            children = [ordered]@{}
        }
    }
    if ($DirParts.Count -gt 1) {
        Add-ToTree -Tree $Tree[$name].children `
                   -DirParts $DirParts[1..($DirParts.Count-1)] `
                   -RelPrefix $Tree[$name].relPath
    }
}

# Also maintain a flat lookup: relPath (backslash-separated) -> dirId
$dirIdMap = @{ '' = 'INSTALLDIR' }

function Populate-DirIdMap {
    param([System.Collections.Specialized.OrderedDictionary]$Tree)
    foreach ($key in $Tree.Keys) {
        $node = $Tree[$key]
        $dirIdMap[$node.relPath] = $node.id
        Populate-DirIdMap -Tree $node.children
    }
}

foreach ($file in $files) {
    $rel     = $file.FullName.Substring($resolved.Length).TrimStart('\', '/')
    $dirPart = [System.IO.Path]::GetDirectoryName($rel)
    if ($dirPart) {
        $parts = $dirPart.Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
        Add-ToTree -Tree $dirTree -DirParts $parts -RelPrefix ''
    }
}

Populate-DirIdMap -Tree $dirTree

# ---------------------------------------------------------------------------
# Emit nested <Directory> elements (recursive)
# ---------------------------------------------------------------------------
function Emit-DirElements {
    param([System.Collections.Specialized.OrderedDictionary]$Tree, [string]$Indent)
    foreach ($key in $Tree.Keys) {
        $node = $Tree[$key]
        if ($node.children.Count -gt 0) {
            $null = $sb.AppendLine("$Indent<Directory Id=""$($node.id)"" Name=""$($node.name)"">")
            Emit-DirElements -Tree $node.children -Indent ($Indent + '  ')
            $null = $sb.AppendLine("$Indent</Directory>")
        } else {
            $null = $sb.AppendLine("$Indent<Directory Id=""$($node.id)"" Name=""$($node.name)"" />")
        }
    }
}

# ---------------------------------------------------------------------------
# Generate the WXS fragment
# ---------------------------------------------------------------------------
$sb = [System.Text.StringBuilder]::new()

$null = $sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
$null = $sb.AppendLine('<!--')
$null = $sb.AppendLine("  Generated by installer\windows\Invoke-Harvest.ps1")
$null = $sb.AppendLine("  Source : $resolved")
$null = $sb.AppendLine("  Files  : $($files.Count)")
$null = $sb.AppendLine("  DO NOT edit by hand — regenerate by running Invoke-Harvest.ps1.")
$null = $sb.AppendLine('-->')
$null = $sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$null = $sb.AppendLine('  <Fragment>')

# Directory tree (only emitted when there are subdirectories)
if ($dirTree.Count -gt 0) {
    $null = $sb.AppendLine('    <DirectoryRef Id="INSTALLDIR">')
    Emit-DirElements -Tree $dirTree -Indent '      '
    $null = $sb.AppendLine('    </DirectoryRef>')
    $null = $sb.AppendLine()
}

$null = $sb.AppendLine('    <ComponentGroup Id="AppFiles">')

foreach ($file in $files) {
    $rel    = $file.FullName.Substring($resolved.Length).TrimStart('\', '/')
    $dirPart = [System.IO.Path]::GetDirectoryName($rel) -replace '/', '\'
    $dirId  = $dirIdMap[$dirPart]
    if (-not $dirId) {
        Write-Warning "No directory Id found for '$dirPart' — using INSTALLDIR"
        $dirId = 'INSTALLDIR'
    }

    $fileId = ConvertTo-WixId $rel
    $compId = "C_$fileId"
    $source = $file.FullName

    $null = $sb.AppendLine("      <Component Id=""$compId"" Directory=""$dirId"" Guid=""*"">")
    $null = $sb.AppendLine("        <File Id=""$fileId"" Source=""$source"" KeyPath=""yes"" />")
    $null = $sb.AppendLine("      </Component>")
}

$null = $sb.AppendLine('    </ComponentGroup>')
$null = $sb.AppendLine('  </Fragment>')
$null = $sb.AppendLine('</Wix>')

# Write output
$outDir = Split-Path $OutputFile -Parent
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force $outDir | Out-Null
}

[IO.File]::WriteAllText($OutputFile, $sb.ToString(), [Text.Encoding]::UTF8)
Write-Host "Generated: $OutputFile ($($files.Count) components, $($dirIdMap.Count - 1) subdirectories)"
