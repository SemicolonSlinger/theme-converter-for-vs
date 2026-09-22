#requires -Version 7
<#
.SYNOPSIS
    Converts a VS Code theme and packages it as a per-user Visual Studio theme VSIX.
.DESCRIPTION
    Builds the converter, converts -Theme to a .pkgdef (plus <name>.audit.txt), and builds
    ThemeVsix\ThemeVsix.csproj around it: the pkgdef plus the editor component (ThemeEditor.cs).
    The VSIX lands in dist\<name>.vsix.
    Needs the "Visual Studio extension development" workload. Installing the VSIX does not.
.EXAMPLE
    tools\build-theme-vsix.ps1 -Theme ..\naysayer88.json -Guid f34e238e-da21-4c85-917a-cc68c561860d `
        -License ..\naysayer88.LICENSE.txt -Publisher 'Your Name'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Theme,
    # Theme id; defaults to the converter's name-based id. Keep it stable across rebuilds.
    [string] $Guid,
    [string] $Version = '1.0.0',
    [string] $DisplayName,
    # Shown as the extension's publisher in the Extensions dialog and the Marketplace.
    [string] $Publisher = 'theme-converter-for-vs',
    # The source theme's licence. MIT and similar licences require the notice to ship with copies.
    [string] $License,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Theme = (Resolve-Path -LiteralPath $Theme).Path
if ($License) { $License = (Resolve-Path -LiteralPath $License).Path }
$name = [IO.Path]::GetFileNameWithoutExtension($Theme)
if (-not $DisplayName) { $DisplayName = "$name Theme" }

$work = Join-Path $repo "ThemeVsix\obj\theme\$name"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

# The converter resolves its mapping files relative to the working directory.
$converterProject = Join-Path $repo 'ThemeConverter\ThemeConverter\ThemeConverter.csproj'
dotnet build $converterProject -nologo -verbosity:quiet -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Converter build failed with exit code $LASTEXITCODE." }
$converterDir = Join-Path $repo "ThemeConverter\ThemeConverter\bin\$Configuration\net6.0"

$converterArgs = @('-i', $Theme, '-o', $work)
if ($Guid) { $converterArgs += @('-g', $Guid) }
Push-Location $converterDir
try {
    & .\ThemeConverter.exe @converterArgs
    if ($LASTEXITCODE -ne 0) { throw "Conversion failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

$pkgdef = Join-Path $work "$name.pkgdef"
# VSSDK packs a file under its own name, so the licence is staged as the name the manifest uses.
if ($License) {
    Copy-Item -LiteralPath $License -Destination (Join-Path $work 'LICENSE.txt')
    $License = Join-Path $work 'LICENSE.txt'
}
Get-Content -LiteralPath (Join-Path $work "$name.audit.txt")

$identity = "ThemeVsix.$($name -replace '[^A-Za-z0-9.]', '')"
$assembly = "ThemeVsix.$name"

# Per-theme constants: MEF layer names are global, so two installed themes must not share one.
$themeIdentity = Join-Path $work 'ThemeIdentity.cs'
@"
namespace ThemeVsix
{
    internal static class ThemeIdentity
    {
        public const string Name = "$($name -replace '[^A-Za-z0-9_.-]', '')";
    }
}
"@ | Set-Content -LiteralPath $themeIdentity -Encoding utf8
$manifest = Join-Path $work 'source.extension.vsixmanifest'
@"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Id="$identity" Version="$Version" Language="en-US" Publisher="$([Security.SecurityElement]::Escape($Publisher))" />
    <DisplayName>$([Security.SecurityElement]::Escape($DisplayName))</DisplayName>
    <Description xml:space="preserve">$([Security.SecurityElement]::Escape($name)) colour theme converted from Visual Studio Code.</Description>$(if ($License) { "`n    <License>LICENSE.txt</License>" })
    <Tags>theme</Tags>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0,)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
    <InstallationTarget Id="Microsoft.VisualStudio.Pro" Version="[18.0,)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
    <InstallationTarget Id="Microsoft.VisualStudio.Enterprise" Version="[18.0,)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
  </Installation>
  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[18.0,)" DisplayName="Visual Studio core editor" />
  </Prerequisites>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" Path="$name.pkgdef" />
    <Asset Type="Microsoft.VisualStudio.MefComponent" Path="$assembly.dll" />
  </Assets>
</PackageManifest>
"@ | Set-Content -LiteralPath $manifest -Encoding utf8

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Workload.VisualStudioExtension `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'No Visual Studio with the "Visual Studio extension development" workload found.' }

$project = Join-Path $repo 'ThemeVsix\ThemeVsix.csproj'
foreach ($dir in 'bin', 'obj\Release', 'obj\Debug') {
    $path = Join-Path $repo "ThemeVsix\$dir"
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
& $msbuild $project -restore -nologo -verbosity:minimal "-property:Configuration=$Configuration" `
    "-property:ThemeName=$name" "-property:ThemePkgdef=$pkgdef" "-property:ThemeManifest=$manifest" `
    "-property:ThemeIdentity=$themeIdentity" "-property:ThemeLicense=$License"
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }

$built = Get-ChildItem -LiteralPath (Join-Path $repo "ThemeVsix\bin\$Configuration") -Recurse -Filter "$name.vsix" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw "Build succeeded but no $name.vsix was produced." }

$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath $built.FullName -Destination (Join-Path $dist "$name.vsix") -Force
Write-Host "dist\$name.vsix  ($([math]::Round($built.Length / 1KB)) KB)"
