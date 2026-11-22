<#
.SYNOPSIS
  Builds SecureSync for Windows: an installer (MSI) that adds Desktop and Start menu shortcuts, plus a portable .exe.

.DESCRIPTION
  Output:
    dist\windows\SecureSync-Setup-<version>.msi   double-click to install (per user, no admin needed)
    dist\windows\portable\SecureSync.exe          runs without installing

  Needs only the .NET 10 SDK (set up automatically if missing). The installer tooling (WiX) comes from NuGet.

.PARAMETER SkipTests
  Don't run the test suite first.
#>
param([switch]$SkipTests)

. (Join-Path $PSScriptRoot "common.ps1")

$dotnet = Get-DotNet
$version = Get-AppVersion
$outDir = Join-Path $DistDir "windows"
$portableDir = Join-Path $outDir "portable"
Write-Host "SecureSync $version for Windows (using $dotnet)"

if (-not $SkipTests) {
    Write-Step "Running tests"
    Invoke-Checked $dotnet @("test", (Join-Path $RepoRoot "tests\SecureSync.Tests"), "-c", "Release", "--nologo", "-v", "quiet")
}

Write-Step "Publishing the self-contained app"
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
Invoke-Checked $dotnet @("publish", (Join-Path $RepoRoot "src\SecureSync.App"), "-c", "Release", "-o", $portableDir, "--nologo", "-v", "quiet")
$exe = Join-Path $portableDir "SecureSync.exe"
if (-not (Test-Path $exe)) { throw "Publish didn't produce $exe" }

Write-Step "Building the installer"
$msiBuild = Join-Path $RepoRoot "installer\bin\Release"
Invoke-Checked $dotnet @("build", (Join-Path $RepoRoot "installer\SecureSync.Installer.wixproj"), "-c", "Release",
    "-p:AppExe=$exe", "-p:Version=$version", "-o", $msiBuild, "--no-incremental", "--nologo", "-v", "quiet") # always re-embed the fresh exe
$msi = Join-Path $outDir "SecureSync-Setup-$version.msi"
Copy-Item (Join-Path $msiBuild "SecureSync-Setup.msi") $msi -Force

Write-Step "Done"
Write-Host "Installer: $msi"
Write-Host "Portable:  $exe"
Write-Host ""
Write-Host "Double-click the installer to install SecureSync for this Windows user. It adds a Desktop shortcut and a"
Write-Host "Start menu entry, and can be removed from Settings > Apps. Uninstalling never deletes your encrypted files."
