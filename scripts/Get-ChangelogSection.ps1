<#
.SYNOPSIS
    Prints the CHANGELOG.md section for one version; used as the release notes by the release workflow.
.DESCRIPTION
    Matches "## vX.Y.Z" exactly or followed by a space (a date), so 1.0.1 never picks up 1.0.10.
    Fails when the section is missing or empty, so a tag cannot ship without notes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$heading = "## v$($Version.TrimStart('v'))"
$inSection = $false
$lines = foreach ($line in Get-Content -Path $Path -Encoding utf8) {
    if ($line.StartsWith('## ')) {
        $inSection = $line -eq $heading -or $line.StartsWith("$heading ")
        continue
    }
    if ($inSection) { $line }
}

$notes = ($lines -join "`n").Trim()
if (-not $notes) { throw "$Path has no entries under '$heading'." }
$notes
