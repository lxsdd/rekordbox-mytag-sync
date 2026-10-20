# Verifies one independently delivered, SHA-bound MyTagSync Windows 11 candidate.
# Never downloads anything and never opens a real rekordbox DB or user media.
# -SelfTest uses synthetic disposable ZIP bytes only.
[CmdletBinding()]
param(
    [string]$CandidateZip,
    [string]$ExpectedSourceSha,
    [string]$ExpectedZipSha,
    [switch]$SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Hex([string]$Value, [int]$Size, [string]$Name) {
    if ([string]::IsNullOrEmpty($Value) -or
        $Value -cnotmatch ('\A[0-9a-fA-F]{' + $Size + '}\z')) {
        throw "$Name must be exactly $Size hexadecimal characters."
    }
}

function Read-ZipText($Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function To-Hex([byte[]]$Bytes) {
    return [BitConverter]::ToString($Bytes).Replace('-','').ToLowerInvariant()
}

function Confirm-Zip([string]$ZipPath, [string]$SourceSha, [string]$ZipSha) {
    Assert-Hex $SourceSha 40 'ExpectedSourceSha'
    Assert-Hex $ZipSha 64 'ExpectedZipSha'
    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw 'Candidate ZIP not found.' }
    $actualZip = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    if ($actualZip -cne $ZipSha.ToUpperInvariant()) { throw 'Candidate ZIP SHA-256 mismatch.' }

    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
    try {
        $entries = [Collections.Generic.Dictionary[string,object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($e in $zip.Entries) {
            $n = $e.FullName
            if ([string]::IsNullOrWhiteSpace($n) -or
                $n.StartsWith('/') -or $n.StartsWith('\') -or
                $n.Contains('\') -or $n.Contains(':') -or
                $n.Contains([char]0) -or
                @($n -split '/' | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -eq '' }).Count -gt 0 -or
                $n.EndsWith('/')) {
                throw 'Unsafe ZIP entry name.'
            }
            if ($entries.ContainsKey($n)) { throw 'Duplicate/case-colliding ZIP entry.' }
            $entries.Add($n, $e)
        }
        foreach ($name in @('SOURCE_SHA.txt','candidate-manifest.json','rekordbox MyTagSync.exe')) {
            if (-not $entries.ContainsKey($name)) { throw 'Required candidate ZIP entry absent.' }
        }
        $source = (Read-ZipText $entries['SOURCE_SHA.txt']).Trim()
        if ($source -cne $SourceSha.ToLowerInvariant()) { throw 'ZIP SOURCE_SHA does not match expected source.' }
        $manifest = Read-ZipText $entries['candidate-manifest.json'] | ConvertFrom-Json
        if ($manifest.schemaVersion -ne 1 -or
            $manifest.sourceSha -cne $source -or
            $manifest.runtime -cne 'win-x64' -or
            $manifest.selfContained -ne $true -or
            $manifest.singleFile -ne $true -or
            [string]$manifest.productVersion -notlike '0.1.0-dev*') {
            throw 'Unsupported source, manifest, platform or candidate version.'
        }
        $records = @($manifest.files)
        if ($records.Count + 1 -ne $entries.Count) { throw 'Manifest does not cover exact ZIP entries.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($row in $records) {
            $n = [string]$row.path
            if ([string]::IsNullOrWhiteSpace($n) -or $n -ceq 'candidate-manifest.json' -or
                -not $seen.Add($n) -or -not $entries.ContainsKey($n)) {
                throw 'Duplicate, absent or invalid manifest entry.'
            }
            Assert-Hex ([string]$row.sha256) 64 'Manifest entry SHA-256'
            $e = $entries[$n]
            if ([long]$row.bytes -ne [long]$e.Length) { throw 'Manifest byte length mismatch.' }
            $sha = [Security.Cryptography.SHA256]::Create()
            $stream = $e.Open()
            try { $actual = To-Hex ($sha.ComputeHash($stream)) }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($actual -cne ([string]$row.sha256).ToLowerInvariant()) {
                throw 'Manifest SHA-256 mismatch.'
            }
        }
        return [pscustomobject]@{
            SourceSha = $source
            ZipSha256 = $actualZip.ToLowerInvariant()
            ProductVersion = [string]$manifest.productVersion
            FileCount = $records.Count
        }
    }
    finally { $zip.Dispose() }
}

function New-SyntheticCandidate([string]$FilePath, [string]$SourceSha, [switch]$Traversal) {
    $files = [ordered]@{
        'SOURCE_SHA.txt' = [Text.Encoding]::UTF8.GetBytes($SourceSha + [Environment]::NewLine)
        'rekordbox MyTagSync.exe' = [byte[]](77,90,0,0)
        'license-audit.tsv' = [Text.Encoding]::UTF8.GetBytes('SYNTHETIC TEST ONLY')
    }
    $records = @()
    foreach ($name in $files.Keys) {
        $bytes = [byte[]]$files[$name]
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = To-Hex ($sha.ComputeHash($bytes)) }
        finally { $sha.Dispose() }
        $records += [ordered]@{path=$name;sha256=$digest;bytes=$bytes.Length}
    }
    $manifest = [ordered]@{
        schemaVersion=1;sourceSha=$SourceSha;productVersion='0.1.0-dev+test'
        runtime='win-x64';selfContained=$true;singleFile=$true;files=$records
    }
    $files['candidate-manifest.json'] = [Text.Encoding]::UTF8.GetBytes(
        ($manifest | ConvertTo-Json -Depth 8))
    if ($Traversal) { $files['../bad.txt'] = [byte[]](0) }
    $stream = [IO.File]::Open($FilePath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($name in $files.Keys) {
                $entry = $archive.CreateEntry($name,[IO.Compression.CompressionLevel]::NoCompression)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
                $output = $entry.Open()
                try {
                    $bytes = [byte[]]$files[$name]
                    $output.Write($bytes,0,$bytes.Length)
                } finally { $output.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

if ($SelfTest) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('mytagsync-candidate-preflight-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    try {
        $sha = 'a' * 40
        $good = Join-Path $root 'synthetic-good.zip'
        New-SyntheticCandidate $good $sha
        $hash = (Get-FileHash -LiteralPath $good -Algorithm SHA256).Hash
        $result = Confirm-Zip $good $sha $hash
        if ($result.FileCount -ne 3) { throw 'Valid fixture did not pass.' }
        foreach ($scenario in @('wrong_zip_hash','wrong_source','unsafe_archive')) {
            $rejected = $false
            try {
                if ($scenario -eq 'wrong_zip_hash') {
                    Confirm-Zip $good $sha ('b' * 64) | Out-Null
                } elseif ($scenario -eq 'wrong_source') {
                    Confirm-Zip $good ('b' * 40) $hash | Out-Null
                } else {
                    $bad = Join-Path $root 'synthetic-unsafe.zip'
                    New-SyntheticCandidate $bad $sha -Traversal
                    Confirm-Zip $bad $sha (Get-FileHash $bad -Algorithm SHA256).Hash | Out-Null
                }
            } catch { $rejected = $true }
            if (-not $rejected) { throw "Unsafe synthetic $scenario was accepted." }
        }
        Write-Host 'WINDOWS11_CANDIDATE_PREFLIGHT_SELFTEST=PASS'
    } finally { Remove-Item -LiteralPath $root -Force -Recurse }
    exit 0
}

# Only real Windows 11 client, never Windows Server hosted CI.
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'This is a Windows 11 client preflight.'
}
$os = Get-CimInstance -ClassName Win32_OperatingSystem
if ([int]$os.BuildNumber -lt 22000 -or $os.Caption -notmatch 'Windows 11' -or
    -not [Environment]::Is64BitOperatingSystem -or
    -not [Environment]::Is64BitProcess) {
    throw 'Require actual Windows 11 x64 client and 64-bit PowerShell.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Do not launch this acceptance as Administrator.'
}
$verified = Confirm-Zip $CandidateZip $ExpectedSourceSha $ExpectedZipSha
$destination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) (
    'MyTagSync\qualification\' + $verified.SourceSha)
if (Test-Path -LiteralPath $destination) {
    throw 'Candidate already extracted; refusing overwrite.'
}
[IO.Compression.ZipFile]::ExtractToDirectory(
    (Resolve-Path -LiteralPath $CandidateZip).Path,$destination)
$exe = Join-Path $destination 'rekordbox MyTagSync.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--ui-smoke' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Published WPF synthetic UI smoke failed.' }
$report = [ordered]@{
    sourceSha=$verified.SourceSha;zipSha256=$verified.ZipSha256
    productVersion=$verified.ProductVersion;fileCount=$verified.FileCount
    windowsCaption='Windows 11';windowsBuild=[int]$os.BuildNumber
    integrity='PASS';syntheticUiSmoke='PASS';liveDatabase='NOT_OPENED'
    realRekordboxAcceptance='PENDING';liveWrites='NOT_AUTHORIZED'
}
$report | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $destination 'READONLY-PREFLIGHT.json') -Encoding UTF8
Write-Host "WINDOWS11_CANDIDATE_PREFLIGHT=PASS source=$($verified.SourceSha)"
Write-Host 'No real rekordbox DB, tracks, tags, cues or XDJ-RX3 were accessed.'
Write-Host 'Continue the documented Windows 11 read-only GUI/device acceptance separately.'
