# Fail closed if CI attempts to build a self-contained Windows candidate using
# pre-October 2026 .NET runtime packs. Synthetic test never requires dotnet.
[CmdletBinding()]
param([switch]$SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$minimum = [version]'10.0.13'
function Test-PatchedRuntime([string]$Sdk, [string[]]$RuntimeLines) {
    if ($Sdk.Trim() -cne '10.0.402') {
        throw "Expected patched .NET SDK 10.0.402, observed '$Sdk'."
    }
    foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $versions = @(
            foreach ($line in $RuntimeLines) {
                if ($line -match ('^' + [regex]::Escape($framework) + '\s+([0-9]+\.[0-9]+\.[0-9]+)\s+\[')) {
                    [version]$Matches[1]
                }
            }
        )
        $eligible = @($versions | Where-Object {
            $_.Major -eq 10 -and $_ -ge $minimum
        })
        if ($eligible.Count -eq 0) {
            throw "Missing patched $framework 10.0.13+ runtime in SDK 10.0.402."
        }
    }
}

if ($SelfTest) {
    $valid = @(
        'Microsoft.NETCore.App 10.0.12 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]',
        'Microsoft.NETCore.App 10.0.13 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]',
        'Microsoft.WindowsDesktop.App 10.0.13 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]'
    )
    Test-PatchedRuntime '10.0.402' $valid
    $scenarios = @(
        @{sdk='10.0.401'; runtime=$valid},
        @{sdk='10.0.402'; runtime=@($valid | Where-Object { $_ -notmatch '10.0.13' })},
        @{sdk='10.0.402'; runtime=@($valid | Where-Object { $_ -notmatch 'WindowsDesktop' })}
    )
    foreach ($case in $scenarios) {
        $blocked = $false
        try { Test-PatchedRuntime $case.sdk $case.runtime }
        catch { $blocked = $true }
        if (-not $blocked) { throw 'Unpatched or incomplete synthetic runtime was accepted.' }
    }
    Write-Host 'DOTNET_1013_SECURITY_GUARD_SELFTEST=PASS'
    exit 0
}

$installedSdk = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed.' }
$installedRuntimes = @(& dotnet --list-runtimes)
if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-runtimes failed.' }
Test-PatchedRuntime $installedSdk $installedRuntimes
Write-Host "DOTNET_1013_SECURITY_GUARD=PASS sdk=$installedSdk, NETCore/WindowsDesktop 10.0.13+"
