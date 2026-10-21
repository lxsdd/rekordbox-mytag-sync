# GitHub Actions OWNER-ONLY / manual, NEVER publish a release.
# Exactly one read-only Windows ZIP, verified against a previously qualified FULL.
[CmdletBinding()]
param(
    [string]$SourceSha,
    [string]$ExpectedZipSha256,
    [string]$CandidateName,
    [string]$Acknowledgement,
    [switch]$SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ackText = 'I_ACCEPT_PRIVATE_MICROSOFT_DOTNET_AND_SDK_DISTRIBUTION_DUTIES'
function Assert-Inputs([string]$sha,[string]$zipHash,[string]$ack) {
    if ($sha -cnotmatch '\A[0-9a-f]{40}\z' -or
        $zipHash -cnotmatch '\A[0-9a-f]{64}\z' -or
        $ack -cne $ackText) { throw 'Source, ZIP hash, or explicit distributor acknowledgement invalid.' }
}
function Assert-Anonymous404([string]$Url) {
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or $uri.Host -cne 'github.com' -or
        -not $uri.AbsolutePath.StartsWith('/lxsdd/rekordbox-mytag-sync/releases/', [StringComparison]::Ordinal)) {
        throw 'Unexpected release URL: refusing an external privacy probe.'
    }
    # Never pass an authorization header. Reject redirects and any status
    # other than the observed anonymous 404 (including rate limits/errors).
    $response = Invoke-WebRequest -Uri $Url -MaximumRedirection 0 -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne 404) {
        throw 'Anonymous access did not return 404. Private release NOT CONFIRMED.'
    }
}
if ($SelfTest) {
    Assert-Inputs ('a' * 40) ('b' * 64) $ackText
    foreach ($test in @(@('bad',('b' * 64),$ackText),
                        @(('a' * 40),'bad',$ackText),
                        @(('a' * 40),('b' * 64),'YES'))) {
        $rejected = $false
        try { Assert-Inputs $test[0] $test[1] $test[2] } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid owner-input fixture accepted.' }
    }
    foreach ($badUrl in @('http://github.com/lxsdd/rekordbox-mytag-sync/releases/tag/a',
        'https://example.com/lxsdd/rekordbox-mytag-sync/releases/tag/a',
        'https://github.com/another/repo/releases/tag/a')) {
        $rejected = $false
        try { Assert-Anonymous404 $badUrl } catch { $rejected = $true }
        if (-not $rejected) { throw 'Unsafe privacy-probe URL escaped validation.' }
    }
    Write-Host 'MYTAGSYNC_OWNER_DRAFT_UPLOAD_SELFTEST=PASS'
    return
}
Assert-Inputs $SourceSha $ExpectedZipSha256 $Acknowledgement
$repo = $env:GITHUB_REPOSITORY
if ($repo -cne 'lxsdd/rekordbox-mytag-sync' -or
    $env:GITHUB_ACTOR -cne 'lxsdd' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'Only the original owner can manually dispatch from main.'
}
if ($CandidateName -cnotmatch '\A[A-Za-z0-9 ._+-]+\.zip\z' -or
    [IO.Path]::GetFileName($CandidateName) -cne $CandidateName) {
    throw 'Invalid candidate ZIP file name.'
}
$zip = Join-Path artifacts $CandidateName
$sidecar = $zip + '.sha256'
$report = 'artifacts/FULL-QUALIFICATION.json'
foreach ($path in @($zip,$sidecar,$report)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing qualified local file: $path" }
}
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -cne $ExpectedZipSha256) { throw 'New build differs from the completed FULL hash. NO UPLOAD.' }
$q = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($q.sourceSha -cne $SourceSha -or
    $q.artifactSha256 -cne $actual -or
    $q.artifactFile -cne $CandidateName -or
    $q.productVersion -notlike '0.1.0-dev*') {
    throw 'FULL qualification metadata inconsistent with pinned candidate.'
}
foreach ($gate in @('deterministicPublish','deterministicPackage','uiSmoke',
    'repositoryHygieneAudit','databaseAccessSourceAudit',
    'dependencyAudit','dependencyLockAudit','licenseAudit','vulnerabilityAudit')) {
    if ($q.$gate -cne 'PASS') { throw "Qualification gate not successful: $gate" }
}
$sidecarFields = (Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+',2
if ($sidecarFields.Count -ne 2 -or $sidecarFields[0] -cne $actual -or
    $sidecarFields[1] -cne $CandidateName) {
    throw 'Candidate SHA sidecar differs.'
}
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$api = "https://api.github.com/repos/$repo"
$main = Invoke-RestMethod -Uri "$api/branches/main" -Headers $headers
if ($main.commit.sha -cne $SourceSha) { throw 'main moved. Refusing stale test binary.' }
$tag = 'mytagsync-private-readonly-' + $SourceSha.Substring(0,12)
$existing = @(Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers)
if (@($existing | Where-Object { $_.tag_name -ceq $tag }).Count -ne 0) {
    throw 'Release already exists; no overwrite or accidental publication.'
}
$gitRef = Invoke-WebRequest -Uri "$api/git/ref/tags/$tag" -Headers $headers -SkipHttpErrorCheck
if ([int]$gitRef.StatusCode -ne 404) {
    throw 'An actual Git tag or inaccessible ref exists; abort.'
}
$description = "PRIVATE READ-ONLY owner hardware trial. NEVER PUBLISH. Source: $SourceSha; ZIP SHA-256: $actual. No live Apply or Restore. Delete this draft after client verification. Legal conditions were explicitly acknowledged by the owner at manual dispatch."
$payload = @{
    tag_name=$tag
    target_commitish=$SourceSha
    name="PRIVATE READ-ONLY MyTagSync - $($SourceSha.Substring(0,12)) - NEVER PUBLISH"
    body=$description
    draft=$true
    prerelease=$true
    make_latest='false'
} | ConvertTo-Json -Depth 4
$payloadFile = Join-Path $env:RUNNER_TEMP 'mytagsync-private-release.json'
Set-Content -LiteralPath $payloadFile -Encoding utf8 -Value $payload
$release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -InFile $payloadFile
if ($release.draft -ne $true -or $release.tag_name -cne $tag -or [int64]$release.id -le 0) {
    throw 'Release creation was not a private GitHub DRAFT.'
}
$id = [int64]$release.id
Write-Host "PRIVATE_DRAFT_CREATED id=$id draft=true"
# From this point onward, a failed upload must not leave a long-lived,
# partially populated binary-bearing draft behind when safe cleanup is possible.
try {
# Critical: verify this particular fresh Draft is anonymous-inaccessible
# BEFORE sending any executable bytes, not only after the ZIP upload.
Assert-Anonymous404 ([string]$release.html_url)
$emptyDraft = Invoke-RestMethod -Uri "$api/releases/$id" -Headers $headers
if ($emptyDraft.draft -ne $true -or [int64]$emptyDraft.id -ne $id -or
    $emptyDraft.tag_name -cne $tag -or @($emptyDraft.assets).Count -ne 0) {
    throw 'Fresh Draft changed, is public, or has unexpected assets. NO BINARIES UPLOADED.'
}
Write-Host 'PRIVATE_DRAFT_PREUPLOAD_ANONYMOUS_DENY=PASS'
$files = @(
    @{Path=$zip;Type='application/zip'},
    @{Path=$sidecar;Type='text/plain'},
    @{Path=$report;Type='application/json'}
)
$uploadedCount = 0
foreach ($file in $files) {
    $state = Invoke-RestMethod -Uri "$api/releases/$id" -Headers $headers
    if ($state.draft -ne $true -or [int64]$state.id -ne $id -or
        $state.tag_name -cne $tag -or @($state.assets).Count -ne $uploadedCount) {
        throw 'Draft identity or asset inventory changed; abort upload.'
    }
    $name = [IO.Path]::GetFileName([string]$file.Path)
    $upload = "https://uploads.github.com/repos/$repo/releases/$id/assets?name=$([uri]::EscapeDataString($name))"
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $file.Type -InFile $file.Path -TimeoutSec 900
    if ($asset.name -cne $name -or $asset.state -cne 'uploaded' -or
        [int64]$asset.size -ne (Get-Item -LiteralPath $file.Path).Length) {
        throw "Uploaded asset identity not confirmed: $name"
    }
    $digest = $asset.PSObject.Properties['digest']
    if ($null -ne $digest -and -not [string]::IsNullOrWhiteSpace([string]$digest.Value)) {
        $localHash = (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([string]$digest.Value -cne ('sha256:' + $localHash)) {
            throw "Server-reported asset digest differs: $name"
        }
    }
    # Fail closed before the next asset: no public item may escape unnoticed.
    Assert-Anonymous404 ([string]$asset.browser_download_url)
    $uploadedCount++
    $after = Invoke-RestMethod -Uri "$api/releases/$id" -Headers $headers
    if ($after.draft -ne $true -or [int64]$after.id -ne $id -or
        $after.tag_name -cne $tag -or @($after.assets).Count -ne $uploadedCount -or
        @($after.assets | Where-Object { $_.name -ceq $name }).Count -ne 1) {
        throw "Draft changed or unexpected uploaded asset inventory: $name"
    }
    Write-Host "PRIVATE_DRAFT_ASSET_OK name=$name anonymous=404"
}
$final = Invoke-RestMethod -Uri "$api/releases/$id" -Headers $headers
if ($final.draft -ne $true -or @($final.assets).Count -ne 3) {
    throw 'Final release is public or has an unexpected asset inventory.'
}
$binary = @($final.assets | Where-Object { $_.name -ceq $CandidateName })
if ($binary.Count -ne 1) { throw 'Candidate asset missing or duplicated.' }
foreach ($url in @([string]$final.html_url,[string]$binary[0].browser_download_url)) {
    Assert-Anonymous404 $url
}
Write-Host "PRIVATE_DRAFT_ANONYMOUS_DENY=PASS endpoints=2"
Write-Host "PRIVATE_DRAFT_OWNER_URL=$($final.html_url)"
Write-Host "PRIVATE_DRAFT_SOURCE_SHA=$SourceSha"
Write-Host "PRIVATE_DRAFT_ZIP_SHA256=$actual"
} catch {
    $failure = $_.Exception.Message
    $cleaned = $false
    try {
        $current = Invoke-RestMethod -Uri "$api/releases/$id" -Headers $headers
        # Delete only the uniquely identified draft just created by THIS run.
        # Never delete a public release, another draft, or a real Git tag.
        if ($current.draft -eq $true -and [long]$current.id -eq $id -and
            $current.tag_name -cne $tag) {
            Write-Warning 'Owner Draft tag changed: manual inspection required.'
        } elseif ($current.draft -eq $true -and [long]$current.id -eq $id -and
                  $current.tag_name -ceq $tag) {
            Invoke-RestMethod -Method Delete -Uri "$api/releases/$id" -Headers $headers | Out-Null
            $cleaned = $true
            Write-Host "PRIVATE_DRAFT_ABORT_CLEANUP=PASS id=$id"
        } else {
            Write-Warning "Unsafe to automatically remove release id=$id; manual inspection required."
        }
    } catch {
        Write-Warning "Failed to verify/delete the newly created private draft id=$id; manual inspection required."
    }
    if (-not $cleaned) { Write-Warning "Unverified private draft may remain: release id=$id" }
    throw "Owner-only private Draft handoff failed (never retry blindly): $failure"
}
