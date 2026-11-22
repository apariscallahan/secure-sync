# Shared helpers for the build scripts.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ToolsDir = Join-Path $RepoRoot ".tools"
$DistDir = Join-Path $RepoRoot "dist"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# Runs a program and fails on a non-zero exit code. Many tools (keytool, dotnet) write progress to stderr, which
# Windows PowerShell would otherwise treat as an error, so only the exit code decides success.
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Exe $($Arguments -join ' ')" }
}

function Test-DotNet10([string]$Exe) {
    if (-not $Exe -or -not (Test-Path $Exe)) { return $false }
    $sdks = & $Exe --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

function Test-AndroidWorkload([string]$Exe) {
    $list = & $Exe workload list 2>$null
    return [bool]($list | Where-Object { $_ -match '^\s*(android|maui-android|maui)\s' })
}

# Returns the path of a dotnet.exe with the .NET 10 SDK (and, if asked, the Android workload).
# Uses the installed SDK when it's suitable. Otherwise sets up a private SDK in .tools\dotnet — no admin rights or
# system changes needed, because workloads can't be added to a system-wide SDK without elevation.
function Get-DotNet([switch]$NeedAndroid) {
    $global = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (Test-DotNet10 $global) {
        if (-not $NeedAndroid -or (Test-AndroidWorkload $global)) { return $global }
    }

    $private = Join-Path $ToolsDir "dotnet\dotnet.exe"
    if (-not (Test-DotNet10 $private)) {
        Write-Step "Setting up a private .NET 10 SDK in .tools\dotnet (one-time download, about 250 MB)"
        New-Item -ItemType Directory -Force $ToolsDir | Out-Null
        $installer = Join-Path $ToolsDir "dotnet-install.ps1"
        Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer -UseBasicParsing
        & $installer -Channel 10.0 -InstallDir (Join-Path $ToolsDir "dotnet") -NoPath | Out-Host
        if (-not (Test-DotNet10 $private)) { throw "Couldn't set up the .NET 10 SDK." }
    }
    $env:DOTNET_ROOT = Split-Path -Parent $private
    $env:DOTNET_MULTILEVEL_LOOKUP = "0"
    if ($NeedAndroid -and -not (Test-AndroidWorkload $private)) {
        Write-Step "Installing the .NET Android + MAUI workload into the private SDK (one-time download, about 1 GB)"
        Invoke-Checked $private @("workload", "install", "maui-android") | Out-Host # keep install output out of the return value
    }
    return $private
}

function Get-AppVersion {
    [xml]$project = Get-Content (Join-Path $RepoRoot "src\SecureSync.App\SecureSync.App.csproj")
    $version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $version) { throw "No <Version> in SecureSync.App.csproj" }
    return $version
}
