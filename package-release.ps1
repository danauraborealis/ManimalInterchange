# Build fresh plugins and package only manifest-declared runtime payloads.
# Authored Unity bundles, sidecars and server db files are supplied by a staged
# install or -AssetSourcePath; native donor files stay in the game install.
[CmdletBinding()]
param(
    [string]$SPTPath,
    [string]$AssetSourcePath,
    [string]$OutputDirectory,
    [switch]$TestPackage,
    [switch]$Release,
    [switch]$UpdatePackage,
    [switch]$ValidateOnly,
    [switch]$StageOnly
)

# Windows associates .ps1 files with Windows PowerShell on many systems. This
# project builds against modern .NET, so transparently move the same command to
# PowerShell 7 instead of failing before useful output can be shown.
if ($PSVersionTable.PSVersion -lt [version]'7.2') {
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    $pwshPath = if ($pwsh) { $pwsh.Source } else { $null }
    if (-not $pwshPath) {
        # Explorer does not inherit Codex's runtime additions to PATH.
        foreach ($candidate in @(
            (Join-Path $env:ProgramFiles 'PowerShell/7/pwsh.exe'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Links/pwsh.exe'),
            (Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe')
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $pwshPath = $candidate
                break
            }
        }
    }
    if (-not $pwshPath) {
        throw 'PowerShell 7.2 or newer is required. Install PowerShell 7, then run package-release.cmd again.'
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($PSBoundParameters.ContainsKey('SPTPath')) { $arguments += @('-SPTPath', $SPTPath) }
    if ($PSBoundParameters.ContainsKey('AssetSourcePath')) { $arguments += @('-AssetSourcePath', $AssetSourcePath) }
    if ($PSBoundParameters.ContainsKey('OutputDirectory')) { $arguments += @('-OutputDirectory', $OutputDirectory) }
    if ($TestPackage) { $arguments += '-TestPackage' }
    if ($Release) { $arguments += '-Release' }
    if ($UpdatePackage) { $arguments += '-UpdatePackage' }
    if ($ValidateOnly) { $arguments += '-ValidateOnly' }
    if ($StageOnly) { $arguments += '-StageOnly' }
    & $pwshPath @arguments
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$transcriptStarted = $false
try {
    $logDirectory = Join-Path $root 'build/package-logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $logPath = Join-Path $logDirectory ('package-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
    Start-Transcript -LiteralPath $logPath | Out-Null
    $transcriptStarted = $true
    Write-Host "Packaging log: $logPath"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'dist' }
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$identity = $props.Project.PropertyGroup
$version = [string]$identity.ModVersion
$sourceUrl = [string]$identity.ModSourceUrl
$targetBuild = [string]$identity.TargetClientBuild
$modGuid = [string]$identity.ModGuid
$packageName = ([string]$identity.ModPackageName).Replace('$(ModUsername)', [string]$identity.ModUsername)
if ([string]::IsNullOrWhiteSpace($packageName) -or $packageName -notmatch '^[A-Za-z0-9.-]+$') { throw 'ModPackageName must be a safe archive name.' }
if (-not $SPTPath) { $SPTPath = [string]$identity.SPTPath.'#text' }
$clientRelative = 'BepInEx/plugins/ManimalInterchange'
$serverRelative = 'SPT_Runtime/user/mods/ManimalInterchange'
if (-not $AssetSourcePath) {
    # the dev install is often a stale earlier version; prefer whichever staged
    # output already carries the version in Directory.Build.props
    $candidates = @((Join-Path $root 'build/install-test'), (Join-Path $root 'build/release-package'), $SPTPath)
    $tried = [Collections.Generic.List[string]]::new()
    foreach ($candidate in $candidates) {
        $candidateManifest = Join-Path $candidate "$clientRelative/interchange-content.json"
        if (-not (Test-Path -LiteralPath $candidateManifest -PathType Leaf)) { continue }
        $candidateVersion = (Get-Content -LiteralPath $candidateManifest -Raw | ConvertFrom-Json).ModVersion
        $tried.Add("$candidate ($candidateVersion)")
        if ($candidateVersion -eq $version) { $AssetSourcePath = $candidate; break }
    }
    if (-not $AssetSourcePath) { throw "No staged install carries mod version $version. Checked: $($tried -join '; '). Run tools/stage_fullmap_test.py --stage (or pass -AssetSourcePath)." }
}
$AssetSourcePath = (Resolve-Path -LiteralPath $AssetSourcePath).Path
$clientSource = Join-Path $AssetSourcePath $clientRelative
$serverSource = Join-Path $AssetSourcePath $serverRelative
$manifestPath = Join-Path $clientSource 'interchange-content.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
# The staging tool always writes a test manifest with a false feature ledger,
# and ManifestRules refuse a Ready manifest unless every feature is true.
# -Release is the explicit human sign-off that marks the ledger verified;
# without it (the double-click default) a test package is produced.
if ($TestPackage -and $Release) { throw 'Choose either -TestPackage or -Release.' }
$unverified = @($manifest.Features.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object Name)
if ($Release) {
    if ([string]::IsNullOrWhiteSpace($sourceUrl)) { throw 'Public release requires ModSourceUrl in Directory.Build.props and a source link on the mod listing.' }
    if ($unverified.Count -ne 0) {
        Write-Warning "Release sign-off: marking these manifest features verified on your authority: $($unverified -join ', ')."
        foreach ($name in $unverified) { $manifest.Features.$name = $true }
    }
} else {
    if (-not $TestPackage) {
        $reason = if ([string]::IsNullOrWhiteSpace($sourceUrl)) { 'ModSourceUrl is not set' } else { "manifest features not yet verified ($($unverified -join ', '))" }
        Write-Warning "Building a test package: $reason. Pass -Release to sign off a public release."
    }
    $TestPackage = $true
    Write-Warning 'Local test package only: not ready for public publication.'
}
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne (Get-FileHash -LiteralPath (Join-Path $serverSource 'interchange-content.json')).Hash) { throw 'Asset source has mismatched client/server manifests.' }
if ($manifest.Schema -ne 1 -or $manifest.TargetClientBuild -ne $targetBuild -or @($manifest.Scenes).Count -ne 18) { throw 'Unsupported or incomplete Interchange content manifest.' }
if ($manifest.ModVersion -ne $version) { throw "Asset source manifest is for mod version $($manifest.ModVersion); Directory.Build.props is $version. Restage the install first." }
if (($manifest.Mode -ne 'test' -or $manifest.Ready) -and ($manifest.Mode -ne 'rework' -or -not $manifest.Ready)) { throw 'Only complete test or rework payloads can be packaged.' }
Write-Host "Asset source: $AssetSourcePath"
if ($TestPackage) {
    $manifest.Mode = 'test'
    $manifest.Ready = $false
} else {
    $manifest.Mode = 'rework'
    $manifest.Ready = $true
    $manifest.ContentId = $manifest.ContentId.Replace('-test-', '-rework-')
}
function Resolve-Payload([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or $Relative.Contains(':') -or $Relative.StartsWith('/') -or ($Relative.Split('/') | Where-Object { $_ -in '', '.', '..' })) { throw "Unsafe payload path: $Relative" }
    $prefix = [IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath((Join-Path $prefix $Relative))
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escapes root.' }
    return $resolved
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$inputs = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Input([string]$Source, [string]$Destination, [string]$Expected = '') {
    if (-not $seen.Add($Destination)) { throw "Duplicate package path: $Destination" }
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Missing package input: $Source" }
    if ($Destination -match '(?i)(\.(md|txt|xml|pdb|cs|csproj|ps1|py|bak)$|/(diagnostics|dumps|cache)/)') { throw "Development file cannot ship: $Destination" }
    $actual = Hash $Source
    if ($Expected -and $actual -ne $Expected) { throw "Asset checksum mismatch: $Source" }
    $inputs.Add([pscustomobject]@{source=$Source;path=$Destination;sha256=$actual;bytes=(Get-Item -LiteralPath $Source).Length})
}
Write-Host 'Checking authored bundles and sidecars...'
$authoredPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.Bundles) + @($manifest.Sidecars)) {
    Add-Input (Resolve-Payload $clientSource $entry.Path) "$clientRelative/$($entry.Path)" $entry.Sha256
    [void]$authoredPaths.Add("$clientRelative/$($entry.Path)")
}
# Server db and item files are staged next to the bundles rather than kept in
# the repo; the manifest hashes pin them to the same authoring pass.
Write-Host 'Checking server database and item files...'
if (@($manifest.ServerFiles).Count -ne 7 -or @($manifest.ItemFiles).Count -ne 2) { throw 'Full-map content needs seven server files and two item files.' }
foreach ($entry in @($manifest.ServerFiles) + @($manifest.ItemFiles)) {
    Add-Input (Resolve-Payload $serverSource $entry.Path) "$serverRelative/$($entry.Path)" $entry.Sha256
}
if ($ValidateOnly) { Write-Host "Verified $($inputs.Count) authored runtime inputs. Mode: $($manifest.Mode)."; return }
& (Join-Path $root 'build.ps1') -Configuration Release -SPTPath $SPTPath
if (-not $?) { throw 'Build failed.' }
Add-Input (Join-Path $root 'interchange-client/bin/Release/netstandard2.1/ManimalInterchange.dll') "$clientRelative/ManimalInterchange.dll"
Add-Input (Join-Path $root 'interchange-components/bin/Release/netstandard2.1/ManimalInterchange.Components.dll') "$clientRelative/ManimalInterchange.Components.dll"
Add-Input (Join-Path $root 'interchange-shared/bin/Release/netstandard2.1/ManimalInterchange.Shared.dll') "$clientRelative/ManimalInterchange.Shared.dll"
Add-Input (Join-Path $root 'interchange-server/bin/Release/net10.0/ManimalInterchange.Server.dll') "$serverRelative/ManimalInterchange.Server.dll"
Add-Input (Join-Path $root 'interchange-shared/bin/Release/netstandard2.1/ManimalInterchange.Shared.dll') "$serverRelative/ManimalInterchange.Shared.dll"
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$manifestJson = $manifest | ConvertTo-Json -Depth 30
# run the shipped manifest through the same rules the plugins enforce at load
Add-Type -Path (Join-Path $root 'interchange-shared/bin/Release/netstandard2.1/ManimalInterchange.Shared.dll')
$manifestOptions = [System.Text.Json.JsonSerializerOptions]::new()
$manifestOptions.IncludeFields = $true
$checkedManifest = [System.Text.Json.JsonSerializer]::Deserialize($manifestJson, [Manimal.Interchange.Shared.ContentManifest], $manifestOptions)
[Manimal.Interchange.Shared.ManifestRules]::Validate($checkedManifest)
$packageFiles = [Collections.Generic.List[object]]::new()
foreach ($entry in $inputs) { $packageFiles.Add($entry) }
function Add-Generated([string]$Destination, [string]$Content) {
    if (-not $seen.Add($Destination)) { throw "Duplicate package path: $Destination" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Content)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $sha = $hasher.ComputeHash($bytes) }
    finally { $hasher.Dispose() }
    $shaText = ([BitConverter]::ToString($sha)).Replace('-', '').ToLowerInvariant()
    $packageFiles.Add([pscustomobject]@{source=$null;content=$bytes;path=$Destination;sha256=$shaText;bytes=$bytes.Length})
}
foreach ($side in @($clientRelative,$serverRelative)) { Add-Generated "$side/interchange-content.json" $manifestJson }
if ($manifest.Mode -eq 'test') {
    # test content is opt-in on both sides: server sentinel file + client config flag
    Add-Generated "$serverRelative/allow-fullmap-test" ''
    Add-Generated "BepInEx/config/$modGuid.cfg" "[Development]`nAllowFullMapTest = true`n"
}
$packageIndex = @($packageFiles | ForEach-Object { [pscustomobject]@{path=$_.path;sha256=$_.sha256;bytes=$_.bytes} })
$manifestRecord = @($packageIndex | Where-Object path -eq "$clientRelative/interchange-content.json")
if ($manifestRecord.Count -ne 1) { throw 'Generated client manifest is missing.' }
$report = [pscustomobject]@{status='plan-verified';readyToDeploy=$false;mode=$manifest.Mode;contentId=$manifest.ContentId;streamedDirectly=$true;version=$version;sourceUrl=$sourceUrl;manifestSha256=$manifestRecord[0].sha256;files=$packageIndex}
$tag = if ($manifest.Mode -eq 'test') { '-test' } else { '' }
$reportPath = Join-Path $OutputDirectory "package-verification-$version$tag.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath
if ($StageOnly) { Write-Host "Verified package plan: $($packageFiles.Count) runtime files; no staging copy created."; return }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePaths = [Collections.Generic.List[string]]::new()
# -UpdatePackage ships DLLs, database files and manifests only; the installed bundles and sidecars must already match.
$archiveKinds = if ($UpdatePackage) { @('update') } else { @('full','update','binaries') }
foreach ($kind in $archiveKinds) {
    $suffix = if ($kind -eq 'full') { '' } else { "-$kind" }
    $zipPath = Join-Path $OutputDirectory "$packageName$suffix-$version$tag.zip"
    Write-Host "Creating $kind archive..."
    # Stream source files straight into the archive instead of copying the
    # multi-gigabyte map payload into build/ first.
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    $selected = @($packageFiles | Where-Object {
        $kind -eq 'full' -or ($kind -eq 'update' -and -not $authoredPaths.Contains($_.path)) -or ($kind -eq 'binaries' -and $_.path.EndsWith('.dll'))
    })
    try {
        foreach ($entry in $selected) {
            if ($entry.source) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entry.source, $entry.path, [IO.Compression.CompressionLevel]::Fastest)
            } else {
                $generated = $zip.CreateEntry($entry.path, [IO.Compression.CompressionLevel]::Fastest)
                $generatedStream = $generated.Open()
                try { $generatedStream.Write($entry.content, 0, $entry.content.Length) }
                finally { $generatedStream.Dispose() }
            }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    # Reopen the archive and verify its exact entry list and lengths.
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne $selected.Count) { throw 'Archive entry count mismatch.' }
        foreach ($entry in $selected) {
            $archived = $zip.GetEntry($entry.path)
            if (-not $archived -or $archived.Length -ne $entry.bytes) { throw "Archive entry mismatch: $($entry.path)" }
        }
    } finally { $zip.Dispose() }
    $archivePaths.Add($zipPath)
}
$archivePaths | ForEach-Object { '{0}  {1}' -f (Hash $_), [IO.Path]::GetFileName($_) } | Set-Content -LiteralPath (Join-Path $OutputDirectory "SHA256SUMS-$packageName-$version$tag.txt")
$report.status = 'passed'
$report.readyToDeploy = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath
Write-Host "Packages verified in $OutputDirectory. Update ZIPs require the same authored bundles already installed."
} catch {
    # Record the error before closing the transcript, then preserve a failing exit status.
    Write-Host ($_ | Out-String) -ForegroundColor Red
    throw
} finally {
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
