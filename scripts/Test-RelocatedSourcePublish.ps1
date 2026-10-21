# Fail-closed cross-directory source-build reproducibility probe.
# A PASS is byte-identity evidence, not a private delivery channel or license approval.
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$BaselinePublishDir,
    [string]$ExpectedSourceSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-IdenticalPublish([string]$Left, [string]$Right) {
    if (-not (Test-Path -LiteralPath $Left -PathType Container) -or
        -not (Test-Path -LiteralPath $Right -PathType Container)) {
        throw 'One of the independent publish directories is absent.'
    }
    $maps = @()
    foreach ($directory in @($Left, $Right)) {
        $root = (Resolve-Path -LiteralPath $directory).Path
        $map = @{}
        foreach ($item in @(Get-ChildItem -LiteralPath $root -File -Recurse)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A publish output contains a reparse point.'
            }
            $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\\', '/')
            if ($map.ContainsKey($relative)) { throw 'Duplicate publish member.' }
            $map[$relative] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $maps += ,$map
    }
    if ($maps[0].Count -lt 1 -or $maps[0].Count -ne $maps[1].Count) {
        throw 'Independent publish file counts differ or are empty.'
    }
    foreach ($name in $maps[0].Keys) {
        if (-not $maps[1].ContainsKey($name) -or
            $maps[0][$name] -cne $maps[1][$name]) {
            throw "Relocated source publish differs: $name"
        }
    }
    if (-not $maps[0].ContainsKey('rekordbox MyTagSync.exe')) {
        throw 'Expected WPF executable is absent from both publishes.'
    }
    return $maps[0].Count
}

if ($SelfTest) {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mytag-relocated-selftest-' + [guid]::NewGuid().ToString('N'))
    try {
        $left = Join-Path $testRoot 'left'
        $right = Join-Path $testRoot 'right'
        New-Item -ItemType Directory -Path $left, $right -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $left 'rekordbox MyTagSync.exe'), 'synthetic executable')
        [IO.File]::WriteAllText((Join-Path $right 'rekordbox MyTagSync.exe'), 'synthetic executable')
        if ((Assert-IdenticalPublish $left $right) -ne 1) {
            throw 'Identical synthetic files were rejected.'
        }
        [IO.File]::WriteAllText((Join-Path $right 'rekordbox MyTagSync.exe'), 'changed')
        $rejected = $false
        try { $null = Assert-IdenticalPublish $left $right } catch { $rejected = $true }
        if (-not $rejected) { throw 'Mutated executable falsely passed.' }
        [IO.File]::WriteAllText((Join-Path $right 'rekordbox MyTagSync.exe'), 'synthetic executable')
        [IO.File]::WriteAllText((Join-Path $right 'unaccounted.dll'), 'extra')
        $rejected = $false
        try { $null = Assert-IdenticalPublish $left $right } catch { $rejected = $true }
        if (-not $rejected) { throw 'Extra sidecar falsely passed.' }
        Write-Host 'MYTAGSYNC_RELOCATED_SOURCE_PUBLISH_SELFTEST=PASS'
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
    return
}

if ($ExpectedSourceSha -cnotmatch '^[0-9a-f]{40}$') {
    throw 'ExpectedSourceSha must be the exact lowercase 40-character commit SHA.'
}
if (-not $BaselinePublishDir) { throw 'BaselinePublishDir is required.' }
$baseline = (Resolve-Path -LiteralPath $BaselinePublishDir -ErrorAction Stop).Path
$actualSha = (& git rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $actualSha.Trim() -cne $ExpectedSourceSha) {
    throw 'Checked-out source commit does not match the requested SHA.'
}
$repositoryRoot = (& git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve repository root.' }
$repositoryRoot = $repositoryRoot.Trim()

# git archive is an immutable commit snapshot: no local files, user data,
# workflow secrets or hidden Git metadata are copied to the relocated build.
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('mytag-relocated-publish-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $workRoot -ErrorAction Stop | Out-Null
    $archive = Join-Path $workRoot 'source.zip'
    $source = Join-Path $workRoot 'source'
    $publish = Join-Path $workRoot 'publish'
    & git -C $repositoryRoot archive --format=zip "--output=$archive" HEAD
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw 'Exact-commit source archive failed.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $source -ErrorAction Stop
    $project = Join-Path $source 'src/RekordboxMyTagSync/RekordboxMyTagSync.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw 'Archived source project missing.'
    }
    Push-Location $source
    try {
        & dotnet restore $project -r win-x64 --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Relocated locked dependency restore failed.' }
        & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
            '-p:RuntimeFrameworkVersion=10.0.13' '-p:PublishSingleFile=true' `
            '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:ContinuousIntegrationBuild=true' `
            '-p:Deterministic=true' '-p:DeterministicSourcePaths=true' `
            '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishReadyToRun=false' `
            "-p:SourceRevisionId=$ExpectedSourceSha" -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'Relocated independent Windows publish failed.' }
    } finally {
        Pop-Location
    }
    $count = Assert-IdenticalPublish $baseline $publish
    Write-Host "MYTAGSYNC_RELOCATED_SOURCE_PUBLISH=PASS source=$ExpectedSourceSha files=$count"
    Write-Host 'This is reproducibility evidence ONLY; legal approval and private delivery remain blocked.'
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
