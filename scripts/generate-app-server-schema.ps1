[CmdletBinding()]
param(
    [string]$CodexPath,
    [string]$ExpectedVersion = '0.147.0-alpha.6.5',
    [switch]$AllowVersionChange
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($CodexPath)) {
    $bundled = Join-Path $repoRoot 'work\codex.exe'
    if (Test-Path -LiteralPath $bundled) {
        $CodexPath = $bundled
    } else {
        $command = Get-Command codex -ErrorAction SilentlyContinue
        if ($null -eq $command) { throw 'Codex CLI was not found. Pass -CodexPath explicitly.' }
        $CodexPath = $command.Source
    }
}

$versionOutput = (& $CodexPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^codex-cli\s+(.+)$') {
    throw "Unable to read Codex CLI version from: $versionOutput"
}
$version = $Matches[1]
if (-not $AllowVersionChange -and $version -ne $ExpectedVersion) {
    throw "Codex CLI $version does not match the locked version $ExpectedVersion. Upgrade only with -AllowVersionChange and the full regression suite."
}

$safeVersion = [Regex]::Replace($version, '[^A-Za-z0-9._-]', '_')
$output = Join-Path $repoRoot "shared\app-server-schema\codex-$safeVersion"
if (Test-Path -LiteralPath $output) {
    throw "Schema directory already exists and will not be overwritten: $output"
}

& $CodexPath app-server generate-json-schema --out $output
if ($LASTEXITCODE -ne 0) { throw 'App Server schema generation failed.' }
Write-Host "Generated App Server schema for Codex CLI $version at $output"
