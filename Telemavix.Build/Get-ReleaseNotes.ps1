param([Parameter(Mandatory)][string] $Version)

$ErrorActionPreference = 'Stop'

$changelog = Join-Path (Split-Path $PSScriptRoot) 'CHANGELOG.md'
$inSection = $false
$lines = foreach ($line in Get-Content $changelog -Encoding utf8) {
	if ($line.StartsWith('## ')) { $inSection = $line.Substring(3).Trim() -eq $Version }
	elseif ($inSection) { $line }
}

$notes = ($lines -join "`n").Trim()
if (-not $notes) { throw "CHANGELOG.md has no notes under '## $Version'" }
$notes
