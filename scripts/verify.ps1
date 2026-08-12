[CmdletBinding()]
param(
    [switch]$SkipBridge,
    [switch]$SkipAndroid
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-Tool {
    param(
        [Parameter(Mandatory)] [string]$BundledPath,
        [Parameter(Mandatory)] [string]$CommandName
    )

    if (Test-Path -LiteralPath $BundledPath) {
        return (Resolve-Path -LiteralPath $BundledPath).Path
    }

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required tool '$CommandName' was not found. Expected bundled path: $BundledPath"
    }
    return $command.Source
}

Push-Location $repoRoot
try {
    $node = Resolve-Tool -BundledPath (Join-Path $repoRoot 'work\node\node.exe') -CommandName 'node'
    & $node 'shared\protocol-v1\validate-fixtures.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Protocol fixture validation failed.' }

    if (-not $SkipBridge) {
        $dotnet = Resolve-Tool -BundledPath (Join-Path $repoRoot 'work\dotnet10\dotnet.exe') -CommandName 'dotnet'
        & $dotnet test 'bridge\CodexMicroBridge.sln' -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Windows Bridge tests failed.' }
    }

    if (-not $SkipAndroid) {
        $bundledJdkRoot = Join-Path $repoRoot 'work\jdk17'
        if (Test-Path -LiteralPath $bundledJdkRoot) {
            $jdk = Get-ChildItem -LiteralPath $bundledJdkRoot -Directory | Select-Object -First 1
            if ($null -eq $jdk) { throw "No JDK was found below $bundledJdkRoot" }
            $env:JAVA_HOME = $jdk.FullName
        } elseif ([string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
            throw 'JAVA_HOME is not set and no bundled JDK 17 exists.'
        }

        $bundledSdk = Join-Path $repoRoot 'work\android-sdk'
        if (Test-Path -LiteralPath $bundledSdk) {
            $env:ANDROID_SDK_ROOT = (Resolve-Path -LiteralPath $bundledSdk).Path
        } elseif ([string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT) -and
                  [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) {
            throw 'ANDROID_SDK_ROOT is not set and no bundled Android SDK exists.'
        }

        $env:GRADLE_USER_HOME = Join-Path $repoRoot 'work\gradle-cache'
        $wrapper = Join-Path $repoRoot 'android\gradlew.bat'
        if (Test-Path -LiteralPath $wrapper) {
            $gradle = $wrapper
        } else {
            $gradle = Resolve-Tool `
                -BundledPath (Join-Path $repoRoot 'work\gradle\gradle-8.11.1\bin\gradle.bat') `
                -CommandName 'gradle'
        }

        & $gradle -p 'android' testDebugUnitTest assembleDebug --no-daemon --stacktrace
        if ($LASTEXITCODE -ne 0) { throw 'Android tests or APK build failed.' }
    }
}
finally {
    Pop-Location
}
