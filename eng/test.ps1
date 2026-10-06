# EliCloud.Sdk test script.
#
# Usage:
#   pwsh -File eng/test.ps1                  # standard `dotnet test`
#   pwsh -File eng/test.ps1 -Runner xunit    # xunit console runner (see below)
#   pwsh -File eng/test.ps1 -Filter Pkce     # only tests whose name contains Pkce
#   pwsh -File eng/test.ps1 -Live            # also run live integration tests
#
# Why -Runner xunit exists:
#   `dotnet test` boots the VSTest testhost, which grabs a handle to its PARENT
#   process to watch for parent exit (Microsoft.TestPlatform ...ProcessHelper
#   .SetExitCallback). Under some sandboxed/restricted hosts that handle request is
#   denied -> "Win32Exception (5): Access is denied" and zero tests run. The xunit
#   console runner is a plain console app and does no such watching, so tests still
#   run there. This is a host limitation, not a code problem.
#
# -Live needs ELICLOUD_LIVE_BASE (this repository deliberately carries no real
# deployment address) and targets <ELICLOUD_LIVE_BASE>/auth.
#
# NOTE: keep this file ASCII-only. PowerShell reads BOM-less .ps1 files using the
# system ANSI codepage on some hosts, which corrupts non-ASCII text and produces
# bogus parse errors. Explanations live in README.md instead.

[CmdletBinding()]
param(
    [string]$Filter,
    [switch]$Live,
    [ValidateSet('dotnet', 'xunit')]
    [string]$Runner = 'dotnet',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    # xunit console runner directory. Defaults to the local NuGet package cache
    # (NUGET_PACKAGES, else ~/.nuget/packages) so this file carries no machine
    # specific path. The version must match Directory.Packages.props.
    [string]$XunitConsole
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Forward slashes on purpose: the same script runs on the Linux CI runner,
# where a literal backslash is not a path separator.
$project = Join-Path $root 'tests/EliCloud.Sdk.Tests/EliCloud.Sdk.Tests.csproj'

if (-not $XunitConsole) {
    $packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
    $XunitConsole = Join-Path $packagesRoot 'xunit.runner.console/2.9.2/tools/net6.0'
}

if ($Live) {
    # This repository intentionally carries no real deployment address, so the live
    # tests take their target from the environment.
    if ([string]::IsNullOrWhiteSpace($env:ELICLOUD_LIVE_BASE)) {
        throw "-Live requires ELICLOUD_LIVE_BASE, e.g. `$env:ELICLOUD_LIVE_BASE = 'https://api.example.com'."
    }
    $env:ELICLOUD_LIVE_TESTS = '1'
}
else {
    Remove-Item env:ELICLOUD_LIVE_TESTS -ErrorAction SilentlyContinue
}

if ($Runner -eq 'dotnet') {
    $cliArgs = @('test', $project, '-c', $Configuration, '--nologo')
    if ($Filter) { $cliArgs += @('--filter', $Filter) }
    dotnet @cliArgs
    exit $LASTEXITCODE
}

$runnerDll = Join-Path $XunitConsole 'xunit.console.dll'
if (-not (Test-Path $runnerDll)) {
    throw "xunit console runner not found at '$XunitConsole'. Use -XunitConsole, or -Runner dotnet."
}

dotnet build $project -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out = Join-Path $root "tests/EliCloud.Sdk.Tests/bin/$Configuration/net10.0"
Copy-Item (Join-Path $XunitConsole '*') $out -Force

$cliArgs = @(
    'exec', '--roll-forward', 'LatestMajor',
    '--depsfile', (Join-Path $out 'EliCloud.Sdk.Tests.deps.json'),
    '--runtimeconfig', (Join-Path $out 'EliCloud.Sdk.Tests.runtimeconfig.json'),
    (Join-Path $out 'xunit.console.dll'),
    (Join-Path $out 'EliCloud.Sdk.Tests.dll'),
    '-nologo',
    '-parallel', 'none'
)

if ($Filter) { $cliArgs += @('-method', ('*' + $Filter + '*')) }

dotnet @cliArgs
exit $LASTEXITCODE
