<#
.SYNOPSIS
    End-to-end verification that the MapperPillow package actually delivers
    compile-time mapping — including under trimming and Native AOT.

.DESCRIPTION
    Unit tests cannot catch the failures this guards against. A package can build,
    test and pack perfectly while shipping without its generator, or while quietly
    degrading every call site to the runtime reflection fallback. Both look green
    right up until a consumer publishes trimmed and gets a half-mapped object or an
    exception.

    So this packs the library, consumes the .nupkg from a local feed exactly as a
    real user would, and asserts on observable behaviour:

      1. the package contains the generator under analyzers/dotnet/cs
      2. the published metadata is complete and no sibling project can pack
      3. a bare PackageReference is enough — no interceptor opt-in needed
      4. mappings are served by the generated interceptor, not by reflection
      5. a trimmed publish emits no IL warnings
      6. the trimmer actually removes the reflection branch from the assembly
      7. a Native AOT binary runs and still maps correctly

    Run it locally before releasing; CI runs it on every push.

.PARAMETER TargetFrameworks
    Consumer target frameworks to verify. Defaults to every framework the library
    multi-targets.

.PARAMETER SkipAot
    Skip the Native AOT step, which needs a platform C toolchain (MSVC on Windows,
    clang on Linux) and is by far the slowest part.
#>
[CmdletBinding()]
param(
    [string[]] $TargetFrameworks = @('net8.0', 'net9.0', 'net10.0'),
    [string]   $Configuration    = 'Release',
    [switch]   $SkipAot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Step   { param([string] $Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Pass   { param([string] $Message) Write-Host "  PASS  $Message" -ForegroundColor Green }
function Write-Fail   { param([string] $Message) Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures.Add($Message) }

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if ($Condition) { Write-Pass $Message } else { Write-Fail $Message }
}

# The runtime identifier of the machine we are running on.
$rid = if ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-x64' } else { 'win-x64' }

# A string that exists only in the reflection fallback's code path. If the trimmer
# folded the feature switch and dropped that branch, it disappears from the assembly.
$reflectionMarker = 'requires the source generator'

$work = Join-Path ([IO.Path]::GetTempPath()) "mapperpillow-verify"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$feed = Join-Path $work 'feed'
New-Item -ItemType Directory -Path $feed -Force | Out-Null

try {
    # ---------------------------------------------------------------- pack ----
    Write-Step 'Packing MapperPillow'

    $packOutput = & dotnet pack (Join-Path $repoRoot 'src/MapperPillow') `
        -c $Configuration --nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed:`n$packOutput" }

    Assert-Condition (-not ($packOutput -match 'warning NU')) 'pack emits no NuGet warnings'

    # The extension guard is not redundant: on Windows -Filter falls back to legacy
    # wildcard semantics that also match longer extensions, so '*.nupkg' can return
    # the .snupkg sitting next to it.
    $nupkg = Get-ChildItem (Join-Path $repoRoot "src/MapperPillow/bin/$Configuration") -Filter '*.nupkg' |
        Where-Object { $_.Extension -eq '.nupkg' } |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $nupkg) { throw 'No .nupkg was produced.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        $entries = $zip.Entries.FullName
        Assert-Condition ([bool]($entries -match '^analyzers/dotnet/cs/.*\.dll$')) `
            'package ships the generator under analyzers/dotnet/cs'
        Assert-Condition ([bool]($entries -match '^build/.*\.targets$')) `
            'package ships its MSBuild targets'

        # nuget.org renders the readme as the package landing page; without this
        # entry the listing is a bare description and the first impression is gone.
        Assert-Condition ([bool]($entries -contains 'README.md')) `
            'package ships its readme'

        # --------------------------------------------------------- metadata ----
        # Everything below is what a consumer sees before they trust the package:
        # who published it, where the source is, and which commit produced it.
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if (-not $nuspecEntry) { throw 'The package has no .nuspec.' }

        $reader = [IO.StreamReader]::new($nuspecEntry.Open())
        try { $meta = ([xml]$reader.ReadToEnd()).package.metadata } finally { $reader.Dispose() }

        # An unset <Authors> silently defaults to the assembly name, which looks
        # published-by-nobody on nuget.org. Assert it was actually chosen.
        Assert-Condition ($meta.authors -and $meta.authors -ne $meta.id) 'nuspec declares real authors'
        Assert-Condition ([bool]$meta.copyright)                          'nuspec declares a copyright'
        Assert-Condition ([bool]$meta.tags)                               'nuspec declares search tags'
        Assert-Condition ($meta.readme -eq 'README.md')                   'nuspec points at the readme'
        Assert-Condition ($meta.license.'#text' -eq 'MIT')                'nuspec declares the MIT license'
        Assert-Condition ($meta.projectUrl -match 'github\.com')          'nuspec declares a project url'

        # Repository url + commit are what make the package auditable: they let a
        # consumer diff the shipped binary against the exact source that built it.
        Assert-Condition ($meta.repository.url -match 'github\.com') 'nuspec declares the repository url'
        Assert-Condition ($meta.repository.commit -match '^[0-9a-f]{40}$') `
            'nuspec records the exact source commit'
    }
    finally { $zip.Dispose() }

    # A symbols package is the difference between a consumer stepping into
    # MapperPillow and hitting a decompiled wall. Pack must produce one.
    $snupkg = Get-ChildItem (Join-Path $repoRoot "src/MapperPillow/bin/$Configuration") -Filter '*.snupkg' |
        Sort-Object LastWriteTime | Select-Object -Last 1
    Assert-Condition ([bool]$snupkg) 'pack produces a .snupkg symbols package'

    # ------------------------------------------------ no accidental packages ----
    # `dotnet pack` on the solution must yield exactly one package. A sample or a
    # test harness reaching nuget.org under this account cannot be taken back:
    # nuget.org has no unpublish, only delist.
    foreach ($sibling in @(
        'src/MapperPillow.Generator/MapperPillow.Generator.csproj',
        'samples/MapperPillow.Sample/MapperPillow.Sample.csproj',
        'tests/MapperPillow.Tests/MapperPillow.Tests.csproj',
        'tests/MapperPillow.Generator.Tests/MapperPillow.Generator.Tests.csproj',
        'tests/MapperPillow.EfCore.Tests/MapperPillow.EfCore.Tests.csproj')) {

        $isPackable = (& dotnet msbuild (Join-Path $repoRoot $sibling) `
            -getProperty:IsPackable -nologo 2>&1 | Out-String).Trim()
        Assert-Condition ($isPackable -eq 'false') "$(Split-Path $sibling -Leaf) opts out of packing"
    }

    Copy-Item $nupkg.FullName $feed
    $packageVersion = $nupkg.BaseName -replace '^MapperPillow\.', ''

    # Force resolution from the local feed rather than a cached copy.
    $cached = Join-Path $HOME '.nuget/packages/mapperpillow'
    if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }

    # ------------------------------------------------------------ consumer ----
    foreach ($tfm in $TargetFrameworks) {
        Write-Step "Verifying a $tfm consumer"

        $app = Join-Path $work "consumer-$tfm"
        New-Item -ItemType Directory -Path $app -Force | Out-Null

        # Deliberately minimal: one PackageReference and nothing else. No
        # InterceptorsNamespaces — the shipped targets must supply it.
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$tfm</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>$rid</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MapperPillow" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $app 'Consumer.csproj') -Encoding UTF8

        @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $app 'nuget.config') -Encoding UTF8

        # Exercises the features whose generated code differs most from what the
        # reflection fallback could produce: multi-level flattening, enum to string,
        # and a collection-valued property.
        @'
using MapperPillow;

var order = new Order
{
    Id = 42,
    Customer = new Customer { Name = "Ada", Address = new Address { City = "London" } },
    Status = Status.Shipped,
    Items = new List<Item> { new() { Sku = "A1" }, new() { Sku = "B2" } },
};

MapperPillowTelemetry.Reset();
OrderDto dto = order.MapTo<OrderDto>();

var ok = dto.Id == 42
    && dto.CustomerAddressCity == "London"
    && dto.Status == "Shipped"
    && dto.Items.Count == 2
    && MapperPillowTelemetry.InterceptedCount == 1;

Console.WriteLine(ok ? "VERIFIED" : $"BROKEN id={dto.Id} city={dto.CustomerAddressCity} " +
    $"status={dto.Status} items={dto.Items.Count} intercepted={MapperPillowTelemetry.InterceptedCount}");
return ok ? 0 : 1;

public enum Status { New, Shipped }
public sealed class Address { public string City { get; set; } = ""; }
public sealed class Customer { public string Name { get; set; } = ""; public Address Address { get; set; } = new(); }
public sealed class Item { public string Sku { get; set; } = ""; }
public sealed class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
    public Status Status { get; set; }
    public List<Item> Items { get; set; } = new();
}
public sealed class ItemDto { public string Sku { get; set; } = ""; }
public sealed class OrderDto
{
    public int Id { get; set; }
    public string CustomerAddressCity { get; set; } = "";
    public string Status { get; set; } = "";
    public List<ItemDto> Items { get; set; } = new();
}
'@ | Set-Content (Join-Path $app 'Program.cs') -Encoding UTF8

        # 1. Plain run, with no interceptor opt-in in the project file.
        $runOutput = & dotnet run --project $app -c $Configuration --nologo 2>&1 | Out-String
        Assert-Condition ($runOutput -match 'VERIFIED') `
            "$tfm : a bare PackageReference maps through the generated interceptor"

        # 2. Trimmed publish.
        $publishDir = Join-Path $app "bin/$Configuration/$tfm/$rid/publish"
        $trimOutput = & dotnet publish $app -c $Configuration -r $rid `
            -p:PublishTrimmed=true -p:IsAotCompatible=true --nologo 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Fail "$tfm : trimmed publish failed`n$trimOutput"; continue }

        Assert-Condition (-not ($trimOutput -match 'warning IL')) `
            "$tfm : trimmed publish emits no IL warnings"

        $exe = Get-ChildItem $publishDir -Filter 'Consumer*' |
            Where-Object { $_.Extension -in @('.exe', '') -and -not $_.PSIsContainer } |
            Select-Object -First 1
        $trimRun = & $exe.FullName 2>&1 | Out-String
        Assert-Condition ($trimRun -match 'VERIFIED') "$tfm : the trimmed binary maps correctly"

        # 3. The trimmer must have removed the reflection branch, not merely
        #    stopped warning about it. This is what the feature switch buys, and on
        #    net8.0 it is the only proof that the attribute polyfill is honoured.
        $lib = Join-Path $publishDir 'MapperPillow.dll'
        if (Test-Path $lib) {
            $bytes  = [IO.File]::ReadAllBytes($lib)
            $needle = [Text.Encoding]::Unicode.GetBytes($reflectionMarker)
            $found  = $false
            for ($i = 0; $i -le $bytes.Length - $needle.Length -and -not $found; $i++) {
                if ($bytes[$i] -ne $needle[0]) { continue }
                $match = $true
                for ($j = 1; $j -lt $needle.Length; $j++) {
                    if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break }
                }
                $found = $match
            }
            Assert-Condition (-not $found) "$tfm : the trimmer removed the reflection branch"
        }
        else {
            Write-Fail "$tfm : MapperPillow.dll not found in the trimmed output"
        }

        # 4. Native AOT.
        if ($SkipAot) {
            Write-Host "  SKIP  $tfm : Native AOT (--SkipAot)" -ForegroundColor Yellow
            continue
        }

        $aotOutput = & dotnet publish $app -c $Configuration -r $rid `
            -p:PublishAot=true --nologo 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Fail "$tfm : Native AOT publish failed`n$aotOutput"; continue }

        Assert-Condition (-not ($aotOutput -match 'warning IL')) `
            "$tfm : Native AOT publish emits no IL warnings"

        $native = Get-ChildItem $publishDir |
            Where-Object { $_.BaseName -eq 'Consumer' -and $_.Extension -in @('.exe', '') } |
            Select-Object -First 1
        $aotRun = & $native.FullName 2>&1 | Out-String
        Assert-Condition ($aotRun -match 'VERIFIED') "$tfm : the Native AOT binary maps correctly"
    }
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'All package verification checks passed.' -ForegroundColor Green
exit 0
