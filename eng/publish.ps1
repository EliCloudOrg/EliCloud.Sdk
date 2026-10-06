# EliCloud.Sdk release script: build, pack, and (optionally) push to a NuGet feed.
#
# Usage:
#   pwsh -File eng/publish.ps1                            # build + pack only (dry run)
#   pwsh -File eng/publish.ps1 -Push                      # build + pack + push
#   pwsh -File eng/publish.ps1 -Push -ApiKey <key>        # explicit key
#   pwsh -File eng/publish.ps1 -Push -Source <url>        # push to a private feed
#   pwsh -File eng/publish.ps1 -VersionSuffix beta.1      # override the prerelease suffix
#   pwsh -File eng/publish.ps1 -Push -ExpectedVersion 1.0.0-alpha.1
#
# -ExpectedVersion makes the script fail BEFORE pushing when the packed version
# is not the one expected (e.g. a release triggered by tag v1.2.3 that would
# otherwise upload 1.0.0). The GitHub publish workflow uses it.
#
# API key lookup order for -Push:
#   1. -ApiKey parameter
#   2. $env:NUGET_API_KEY
#   3. the <apikeys> section of your NuGet.Config (that is `dotnet nuget push` itself)
# nuget.org no longer issues long-lived keys: its publishing workflow uses Trusted
# Publishing (OIDC) and passes a short-lived key through NUGET_API_KEY.
#
# NOTE: keep this file ASCII-only (some hosts read BOM-less .ps1 with the ANSI
# codepage), and keep the paths forward-slash so the same script also runs on the
# Linux CI runner.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Push,
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [string]$VersionSuffix,
    [string]$ExpectedVersion,
    [switch]$SkipDuplicate
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'EliCloud.Sdk.slnx'
$output = Join-Path $root 'artifacts/packages'
$projects = @(
    (Join-Path $root 'src/EliCloud.Sdk/EliCloud.Sdk.csproj'),
    (Join-Path $root 'src/EliCloud.Sdk.AspNetCore/EliCloud.Sdk.AspNetCore.csproj')
)

# Work from a clean output directory. `dotnet pack` is incremental - when the
# .nupkg already exists and looks newer than its inputs, PackTask is skipped
# entirely - so a timestamp filter would find nothing on a repeat run. Wiping the
# directory instead makes "everything in here" mean "produced by this run", and
# no stale package can ever be pushed.
if (Test-Path $output) { Remove-Item -Recurse -Force $output }

# -VersionSuffix must reach both build and pack, otherwise the assembly
# informational version and the package version disagree.
$buildArgs = @('build', $solution, '-c', $Configuration, '--nologo')
$packArgs = @('-c', $Configuration, '--no-build', '--nologo')
if ($VersionSuffix) {
    $buildArgs += "-p:VersionSuffix=$VersionSuffix"
    $packArgs += "-p:VersionSuffix=$VersionSuffix"
}

dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($project in $projects) {
    dotnet pack $project @packArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$fresh = @(Get-ChildItem -Path $output -File |
    Where-Object { $_.Extension -in '.nupkg', '.snupkg' })

if ($fresh.Count -eq 0) {
    Write-Error "No package was produced in $output."
    exit 1
}

if ($ExpectedVersion) {
    $wrong = @($fresh | Where-Object { $_.BaseName -notlike "*.$ExpectedVersion" })
    if ($wrong.Count -gt 0) {
        Write-Error ("Expected version '$ExpectedVersion' but packed: " + (($wrong | ForEach-Object { $_.Name }) -join ', '))
        exit 1
    }
}

Write-Host ''
Write-Host 'Produced:'
foreach ($file in $fresh) {
    Write-Host ("  {0}  ({1:N0} bytes)" -f $file.Name, $file.Length)
}

if (-not $Push) {
    Write-Host ''
    Write-Host 'Dry run: nothing was pushed. Re-run with -Push to publish.'
    exit 0
}

# Push main packages first, then symbols: consumers should never see a snupkg
# whose nupkg failed to publish.
$ordered = @($fresh | Where-Object { $_.Extension -eq '.nupkg' }) +
           @($fresh | Where-Object { $_.Extension -eq '.snupkg' })

foreach ($file in $ordered) {
    $arguments = @('nuget', 'push', $file.FullName, '--source', $Source, '--nologo')
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) { $arguments += @('--api-key', $ApiKey) }
    if ($SkipDuplicate) { $arguments += '--skip-duplicate' }

    Write-Host ''
    Write-Host "Pushing $($file.Name) -> $Source"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ''
Write-Host 'Done. nuget.org usually indexes a new package within a few minutes.'
exit 0
