<#
.SYNOPSIS
    Local pre-flight for a MapperPillow release: proves the tree is releasable and
    produces the exact artifacts CI will produce, without publishing anything.

.DESCRIPTION
    MapperPillow publishes through Trusted Publishing from .github/workflows/release.yml,
    so nothing here holds a credential and nothing here can push. This script exists
    to find the problems on your machine, in minutes, instead of after a `v*` tag has
    already been pushed and a release run is waiting on your approval.

    It refuses to pack a tree you could not defend:

      1. the working tree must be clean, so the package matches a real commit
      2. the version must not already be published — nuget.org accepts a version
         exactly once, with no unpublish and no overwrite
      3. the full test suite must pass on every target framework
      4. eng/Verify-Package.ps1 must pass — the checks `dotnet test` cannot make
      5. the pack runs with ContinuousIntegrationBuild, matching the CI build

    When it passes, tag the commit. The tag is the release trigger; the `release`
    environment gate in GitHub is the release button.

.PARAMETER SkipAot
    Forwarded to eng/Verify-Package.ps1. Use only to iterate on the script itself;
    the release workflow always runs the Native AOT step.

.PARAMETER AllowDirtyTree
    Pack from a dirty tree. For local experiments only — never for a real release.

.EXAMPLE
    ./eng/Release.ps1
    Verifies everything and writes artifacts/, then prints the tag command.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipAot,
    [switch] $AllowDirtyTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'

function Write-Step { param([string] $Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }

# ------------------------------------------------------------------ clean tree --
Write-Step 'Checking the working tree'

$dirty = & git -C $repoRoot status --porcelain
if ($dirty -and -not $AllowDirtyTree) {
    throw "The working tree has uncommitted changes. A published package records the " +
          "commit it was built from; releasing a dirty tree makes that record a lie.`n" +
          ($dirty -join "`n")
}

$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
Write-Host "  HEAD $commit"

# --------------------------------------------------------------------- version --
$version = (& dotnet msbuild (Join-Path $repoRoot 'src/MapperPillow/MapperPillow.csproj') `
    -getProperty:Version -nologo | Out-String).Trim()
if (-not $version) { throw 'Could not read <Version> from src/MapperPillow/MapperPillow.csproj.' }
Write-Host "  Version $version"

# nuget.org accepts a version once. Catch the collision here, where it is a
# message, rather than in the release run, where it is a burned version number.
$published = $null
try {
    $published = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/mapperpillow/index.json' `
        -ErrorAction Stop
}
catch {
    # A 404 is the expected state before the very first release, and an offline
    # machine is not a reason to refuse a local pack. Do not filter by exception
    # type here: PowerShell 7 raises HttpResponseException, Windows PowerShell
    # raises WebException, and getting that wrong turns the first release into a crash.
    Write-Host "  nuget.org lists no published version for this id yet ($($_.Exception.Message))."
}

if ($published -and ($published.PSObject.Properties.Name -contains 'versions') -and
    $published.versions -contains $version) {
    throw "Version $version is already published on nuget.org. Bump <Version> in " +
          'src/MapperPillow/MapperPillow.csproj; a published version can never be replaced.'
}

# A tag that disagrees with <Version> fails the release workflow. Say so now.
$existingTag = & git -C $repoRoot tag --list "v$version"
if ($existingTag) {
    Write-Host "  WARNING  tag v$version already exists locally." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------- verify --
Write-Step 'Running the test suite'
& dotnet test (Join-Path $repoRoot 'MapperPillow.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing was packed.' }

Write-Step 'Verifying the package end to end'
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Configuration $Configuration -SkipAot:$SkipAot
if ($LASTEXITCODE -ne 0) { throw 'Package verification failed. Nothing was packed.' }

# ------------------------------------------------------------------------ pack --
Write-Step "Packing into $artifacts"
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

& dotnet pack (Join-Path $repoRoot 'src/MapperPillow/MapperPillow.csproj') `
    -c $Configuration -o $artifacts --nologo -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

$package = Get-ChildItem $artifacts -Filter '*.nupkg' |
    Where-Object { $_.Extension -eq '.nupkg' } | Select-Object -First 1
$symbols = Get-ChildItem $artifacts -Filter '*.snupkg' | Select-Object -First 1

if (-not $package) { throw 'No .nupkg was produced.' }
if (-not $symbols) { throw 'No .snupkg was produced; consumers would have no symbols.' }

Get-ChildItem $artifacts | ForEach-Object {
    Write-Host ('  {0}  ({1:N0} bytes)' -f $_.Name, $_.Length) -ForegroundColor Green
}

# ------------------------------------------------------------------- next step --
Write-Host "`nThis commit is releasable. Publishing is a tag away:" -ForegroundColor Cyan
Write-Host "  git tag -a v$version -m 'MapperPillow v$version' $commit"
Write-Host "  git push origin v$version"
Write-Host "`nThe tag starts .github/workflows/release.yml, which re-runs every check" -ForegroundColor Cyan
Write-Host "above and then waits for you to approve the 'release' environment."
Write-Host "The artifacts here are a local preview; CI publishes the ones it builds."
