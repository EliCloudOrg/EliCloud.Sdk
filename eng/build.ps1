# EliCloud.Sdk build script.
#
# Usage:
#   pwsh -File eng/build.ps1
#   pwsh -File eng/build.ps1 -Configuration Release
#   pwsh -File eng/build.ps1 -Pack
#
# NOTE: keep this file ASCII-only. PowerShell reads BOM-less .ps1 files using the
# system ANSI codepage on some hosts, which corrupts non-ASCII text and produces
# bogus parse errors. Explanations live in README.md instead.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Pack
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'EliCloud.Sdk.slnx'

dotnet build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Pack) {
    # Forward slashes on purpose: the same script runs on the Linux CI runner,
    # where a literal backslash is not a path separator.
    $projects = @(
        (Join-Path $root 'src/EliCloud.Sdk/EliCloud.Sdk.csproj'),
        (Join-Path $root 'src/EliCloud.Sdk.AspNetCore/EliCloud.Sdk.AspNetCore.csproj')
    )

    foreach ($project in $projects) {
        dotnet pack $project -c $Configuration --no-build --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

exit 0
