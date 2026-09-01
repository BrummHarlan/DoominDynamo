#Requires -Version 5.1
<#
.SYNOPSIS
  Builds DoomInDynamo in Release and lays out a ready-to-install Dynamo package
  folder under dist\DoomInDynamo\ (pkg.json + bin\ + extra\).

.DESCRIPTION
  This script only writes under .\dist - it does NOT copy anything into your
  Dynamo packages folder. To install, copy the resulting dist\DoomInDynamo
  folder yourself into:
    %AppData%\Dynamo\Dynamo Revit\<version>\packages\DoomInDynamo
  (on this machine, Revit 2027's Dynamo build looks for packages under
  "Dynamo Revit\27.0\packages" - check Dynamo's Package Manager > "Local
  packages" locations if that folder doesn't exist yet).

.PARAMETER Configuration
  Build configuration to publish. Defaults to Release.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$slnx = Join-Path $root "DoomInDynamo.slnx"
$distDir = Join-Path $root "dist\DoomInDynamo"
$binDir = Join-Path $distDir "bin"
$extraDir = Join-Path $distDir "extra"

Write-Host "Building $slnx ($Configuration)..."
dotnet build $slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
New-Item -ItemType Directory -Path $extraDir -Force | Out-Null

$nodeBinDir = Join-Path $root "src\DoomInDynamo\bin\$Configuration"

# Everything the build produced: DoomInDynamo.dll, ManagedDoom.Engine.dll,
# the audio deps it pulls in (DrippyAL/MeltySynth/Silk.NET.OpenAL/...), and the
# runtimes\win-x64\native\soft_oal.dll native OpenAL-soft binary underneath -
# CopyLocalLockFileAssemblies in Directory.Build.props is what makes all of
# that land here instead of just the two DLLs we wrote ourselves.
Copy-Item "$nodeBinDir\*" $binDir -Recurse -Force

# The zero-touch half of the package builds as its own project (no project
# reference ties it to the NodeModel assembly - they share nothing but the
# package), so its output has to be collected separately. Both DLLs are listed
# in pkg.json's node_libraries. Its Revit/Dynamo references are Private=false,
# so only the one assembly (plus pdb/deps.json) lands here - Revit's own DLLs
# never get redistributed.
$functionsBinDir = Join-Path $root "src\DoomInDynamo.Functions\bin\$Configuration"
Copy-Item "$functionsBinDir\*" $binDir -Recurse -Force

# Trim the non-Windows OpenAL-soft native variants that Silk.NET.OpenAL.Soft.Native
# ships for every platform - Dynamo for Revit only runs on win-x64, and dropping
# the linux/osx ones (and win-arm64/win-x86) keeps the installed package smaller.
$runtimesDir = Join-Path $binDir "runtimes"
if (Test-Path $runtimesDir) {
    Get-ChildItem $runtimesDir -Directory | Where-Object { $_.Name -ne "win-x64" } | Remove-Item -Recurse -Force
}

Copy-Item (Join-Path $root "package\pkg.json") $distDir

Write-Host ""
Write-Host "Package staged at: $distDir"
Write-Host "Copy that folder into your Dynamo packages directory to install it, e.g.:"
Write-Host "  `$dst = `"`$env:APPDATA\Dynamo\Dynamo Revit\27.0\packages\DoomInDynamo`""
Write-Host "  Copy-Item `"$distDir`" `$dst -Recurse -Force"
