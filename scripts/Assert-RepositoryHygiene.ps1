# One fail-closed implementation for FAST, FULL, owner-only private handoff.
# Preserve exact historical blob exceptions and reject all other sensitive data.
New-Item -ItemType Directory -Force qualification | Out-Null

$patternPath = ".github/security-patterns.json"
$patterns = @(Get-Content $patternPath -Raw | ConvertFrom-Json)
if ($patterns.Count -eq 0) {
  throw "Repository hygiene pattern set is empty."
}

$tracked = @(git ls-files | Where-Object { $_ -ne $patternPath })
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
  throw "Could not enumerate tracked repository files for hygiene audit."
}

# Immutable historical synthetic-only fixture blobs already committed
# before the no-user-profile fixture correction. The exception is for
# precisely these Git object identities and one regex category, never
# for arbitrary current content or newly added user paths/secrets.
$syntheticHistoryFixtureBlobs = @(
  '9e35339addd5cd07241e70cf2d8afadccd9cce42',
  '9fc78008a50e67f8ee01110f5ee4939935edb8a9'
)
$syntheticHistoryFixturePath = 'tests/RekordboxMyTagSync.SelfTests/DiagnosticHistoryStoreSelfTest.cs'

function Test-Content([string]$Label, [string]$Path, [string]$Content, [string]$BlobSha = '') {
  if ($Path -eq $patternPath) { return }

  # Historical versions of this workflow briefly contained the pattern
  # definitions inline. Remove only those scanner-definition lines so
  # they cannot self-trigger while the rest of the workflow stays scanned.
  if ($Path -eq ".github/workflows/full.yml") {
    $Content = (($Content -split "\r?\n") |
      Where-Object {
        $_ -notmatch 'Regex\s*=' -and
        $_ -notmatch '"regex"\s*:'
      }) -join [Environment]::NewLine
  }

  foreach ($pattern in $patterns) {
    if ($Content -match [string]$pattern.Regex) {
      if ($syntheticHistoryFixtureBlobs -ccontains $BlobSha -and
          $Path -ceq $syntheticHistoryFixturePath -and
          [string]$pattern.Name -ceq 'Local Windows user path' -and
          $Content.Contains('const string privatePath = @') -and
          $Content.Contains('private-file.flac') -and
          [regex]::Matches($Content, [string]$pattern.Regex).Count -eq 1) {
        # One documented, exact historical synthetic fixture only.
        # Never exempt a user path in any other file/blob, nor a
        # token/private key inside the same synthetic fixture.
        continue
      }
      $script:findings += [PSCustomObject]@{
        Location = $Label
        Pattern = [string]$pattern.Name
      }
    }
  }
}

# Self-test this *same* scanner against the exact immutable
# historical test blob. An unrelated identity/path, an extra
# profile-path occurrence, or an injected credential must be flagged.
# Assemble negative fixtures at runtime so the scanner will not
# self-trigger on its own regression definitions.
$verifiedHistoricalBlob = '9fc78008a50e67f8ee01110f5ee4939935edb8a9'
$fixtureObjectType = (& git cat-file -t $verifiedHistoricalBlob) -join ''
if ($LASTEXITCODE -ne 0 -or $fixtureObjectType -ne 'blob') {
  throw 'Immutable historical synthetic fixture blob is unavailable.'
}
$historicalFixture = (& git cat-file -p $verifiedHistoricalBlob) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0 -or -not $historicalFixture.Contains('private-file.flac')) {
  throw 'Immutable historical synthetic fixture could not be verified.'
}
$script:findings = @()
Test-Content 'selftest:pinned-history' $syntheticHistoryFixturePath $historicalFixture $verifiedHistoricalBlob
if ($script:findings.Count -ne 0) {
  throw 'Hygiene audit rejected an exact pinned historical synthetic test fixture.'
}
$separator = [string][char]92
$additionalProfile = 'C:' + $separator + 'Users' + $separator + 'InjectedFixture' + $separator + 'other.flac'
$fakeCredential = 'ghp_' + ('A' * 24)
$negativeCases = @(
  @{ kind = 'wrong-sha'; file = $syntheticHistoryFixturePath; text = $historicalFixture; sha = ('0' * 40) },
  @{ kind = 'wrong-path'; file = 'tests/unrelated.cs'; text = $historicalFixture; sha = $verifiedHistoricalBlob },
  @{ kind = 'second-profile'; file = $syntheticHistoryFixturePath; text = $historicalFixture + [Environment]::NewLine + $additionalProfile; sha = $verifiedHistoricalBlob },
  @{ kind = 'credential'; file = $syntheticHistoryFixturePath; text = $historicalFixture + [Environment]::NewLine + $fakeCredential; sha = $verifiedHistoricalBlob }
)
foreach ($case in $negativeCases) {
  $script:findings = @()
  Test-Content ("selftest:" + $case.kind) $case.file $case.text $case.sha
  if ($script:findings.Count -eq 0) {
    throw "Hygiene audit mistakenly accepted a negative fixture: $($case.kind)"
  }
}
$script:findings = @()
Write-Host 'MYTAGSYNC_STRICT_HISTORICAL_HYGIENE_REGRESSION=PASS'
foreach ($file in $tracked) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
  try {
    $content = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
  } catch {
    continue
  }
  Test-Content "current:$file" $file $content
}

$historyObjects = @{}
foreach ($line in @(git rev-list --objects --all)) {
  if ([string]::IsNullOrWhiteSpace($line)) { continue }
  $parts = $line -split ' ', 2
  if ($parts.Count -ne 2) { continue }
  $sha = $parts[0]
  $historyPath = $parts[1]
  if ($historyPath -eq $patternPath -or $historyObjects.ContainsKey($sha)) {
    continue
  }
  $historyObjects[$sha] = $historyPath
}
if ($LASTEXITCODE -ne 0) {
  throw "Could not enumerate repository history for hygiene audit."
}

foreach ($pair in $historyObjects.GetEnumerator()) {
  try {
    $objectType = (& git cat-file -t $pair.Key) -join ""
    if ($LASTEXITCODE -ne 0 -or $objectType -ne "blob") { continue }
    $content = (& git cat-file -p $pair.Key) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { continue }
  } catch {
    continue
  }
  Test-Content ("history:" + $pair.Value + "@" + $pair.Key.Substring(0, 12)) $pair.Value $content $pair.Key
}

if ($findings.Count -gt 0) {
  $findings |
    Sort-Object Location, Pattern -Unique |
    Format-Table |
    Out-String |
    Write-Host
  throw "Repository hygiene audit found credential-like or private local-path material."
}

@(
  "status=PASS"
  "tracked_files=$($tracked.Count)"
  "historical_blobs=$($historyObjects.Count)"
  "checked_patterns=$($patterns.Count)"
) | Set-Content qualification/repository-hygiene-audit.txt -Encoding ASCII
