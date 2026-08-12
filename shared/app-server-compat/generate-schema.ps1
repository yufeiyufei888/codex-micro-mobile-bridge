[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CodexPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$AdapterRevision,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$resolvedCodex = (Resolve-Path -LiteralPath $CodexPath).Path
$resolvedOutputParent = [System.IO.Path]::GetFullPath($OutputDirectory)
$actualVersion = (& $resolvedCodex --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read Codex CLI version (exit $LASTEXITCODE)."
}
if ($actualVersion -cne $ExpectedVersion) {
    throw "Codex CLI version mismatch. Expected '$ExpectedVersion', got '$actualVersion'."
}

$safeVersion = $actualVersion -replace '[^A-Za-z0-9._-]', '_'
$target = Join-Path $resolvedOutputParent $safeVersion
if (Test-Path -LiteralPath $target) {
    throw "Refusing to overwrite existing generated schema directory: $target"
}

$jsonOutput = Join-Path $target 'json'
$tsOutput = Join-Path $target 'ts'
[System.IO.Directory]::CreateDirectory($jsonOutput) | Out-Null
[System.IO.Directory]::CreateDirectory($tsOutput) | Out-Null

& $resolvedCodex app-server generate-json-schema --out $jsonOutput
if ($LASTEXITCODE -ne 0) {
    throw "generate-json-schema failed with exit code $LASTEXITCODE."
}
& $resolvedCodex app-server generate-ts --out $tsOutput
if ($LASTEXITCODE -ne 0) {
    throw "generate-ts failed with exit code $LASTEXITCODE."
}

$directoryDigestText = [System.Text.StringBuilder]::new()
Get-ChildItem -LiteralPath $target -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $prefix = $target.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        [void]$directoryDigestText.Append($relative)
        [void]$directoryDigestText.Append("`n")
        [void]$directoryDigestText.Append($hash)
        [void]$directoryDigestText.Append("`n")
    }
$directoryDigestInput = [System.Text.Encoding]::UTF8.GetBytes($directoryDigestText.ToString())
$hasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $directoryHash = -join ($hasher.ComputeHash($directoryDigestInput) | ForEach-Object { $_.ToString('x2') })
} finally {
    $hasher.Dispose()
}

$lock = [ordered]@{
    cliVersion = $actualVersion
    schemaDirectorySha256 = $directoryHash
    adapterRevision = $AdapterRevision
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$lockJson = $lock | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $target 'lock.json'),
    $lockJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)

Write-Output "Generated pinned app-server schemas at $target"
