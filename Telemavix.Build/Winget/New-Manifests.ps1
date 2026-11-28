param(
	[Parameter(Mandatory)][string] $Tag,
	[Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$json = gh release view $Tag --repo sefinek/telemavix-studio --json 'isDraft,isPrerelease,publishedAt,assets'
if ($LASTEXITCODE -ne 0) { throw "Could not read the release $Tag" }
$release = $json | ConvertFrom-Json
if ($release.isDraft -or $release.isPrerelease) { throw "$Tag is a draft or a prerelease" }

$version = $Tag -replace '^v'

function Get-InstallerHash([string] $architecture) {
	$name = "Telemavix-$version-win-$architecture-setup.exe"
	$asset = $release.assets | Where-Object name -EQ $name
	if ($asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "$name is missing or has no SHA-256 digest" }
	$Matches[1].ToUpperInvariant()
}

$notes = & (Join-Path (Split-Path $PSScriptRoot) 'Get-ReleaseNotes.ps1') -Version $version
$notes = $notes -replace '\[([^\]]+)\]\([^)]+\)', '$1'
if ($notes.Length -gt 10000) { throw "The release notes have $($notes.Length) characters, winget allows 10000" }

$values = @{
	'__VERSION__'       = $version
	'__RELEASE_DATE__'  = ([datetime] $release.publishedAt).ToUniversalTime().ToString('yyyy-MM-dd')
	'__SHA256_X64__'    = Get-InstallerHash 'x64'
	'__SHA256_ARM64__'  = Get-InstallerHash 'arm64'
	'__RELEASE_NOTES__' = ($notes -split "`n" | ForEach-Object { "  $_".TrimEnd() }) -join "`n"
}

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
foreach ($template in Get-ChildItem $PSScriptRoot -Filter '*.yaml') {
	$content = Get-Content $template.FullName -Raw -Encoding utf8
	foreach ($key in $values.Keys) { $content = $content.Replace($key, $values[$key]) }
	Set-Content (Join-Path $OutputDirectory $template.Name) $content -Encoding utf8NoBOM -NoNewline
}
