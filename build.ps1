param(
    [switch]$NoRestore,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$SPTPath
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$properties = @()
if ($SPTPath) { $properties += "-p:SPTPath=$SPTPath" }
Push-Location $PSScriptRoot
try {
    if (!(Test-Path 'config/target-lock.json')) { throw 'Run tools/stage_inputs.py to freeze the target references first' }
    foreach ($project in @('interchange-client/interchange-client.csproj', 'interchange-server/interchange-server.csproj', 'tools/ContractVerification/ContractVerification.csproj', 'tools/ServerDataProbe/ServerDataProbe.csproj')) {
        # release packaging builds only the shipped plugins; the tools arent part of the published tree
        if ($Configuration -eq 'Release' -and $project.StartsWith('tools/')) { continue }
        if (!$NoRestore) {
            dotnet restore $project --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -v minimal @properties
            if ($LASTEXITCODE -ne 0) { throw 'Restore failed: ' + $project }
        }
        dotnet build $project -c $Configuration --no-restore -v minimal @properties
        if ($LASTEXITCODE -ne 0) { throw 'Build failed: ' + $project }
    }
} finally { Pop-Location }
