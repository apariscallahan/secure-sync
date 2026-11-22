<#
.SYNOPSIS
  Builds SecureSync for Android: a signed APK you can install on a phone.

.DESCRIPTION
  Output:
    dist\android\SecureSync-<version>.apk

  First run sets everything up automatically, without admin rights:
    - a private .NET 10 SDK with the Android workload in .tools\dotnet (unless your installed SDK already has it)
    - the Android SDK packages it needs (into your existing Android SDK, or .tools\android-sdk)
    - a Java JDK (Android Studio's bundled one is used if present, otherwise one is put in .tools\jdk)
    - a signing key in .keys\ — BACK THIS FOLDER UP. Android only installs updates signed with the same key.

.PARAMETER SkipTests
  Don't run the test suite first.
.PARAMETER AndroidSdk
  Use this Android SDK folder instead of finding one.
.PARAMETER JavaSdk
  Use this JDK folder instead of finding one.
#>
param([switch]$SkipTests, [string]$AndroidSdk, [string]$JavaSdk)

. (Join-Path $PSScriptRoot "common.ps1")

$project = Join-Path $RepoRoot "src\SecureSync.Android\SecureSync.Android.csproj"
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup | Where-Object { $_.ApplicationDisplayVersion } | Select-Object -First 1).ApplicationDisplayVersion
$outDir = Join-Path $DistDir "android"

$dotnet = Get-DotNet -NeedAndroid
Write-Host "SecureSync $version for Android (using $dotnet)"

if (-not $SkipTests) {
    Write-Step "Running tests"
    Invoke-Checked $dotnet @("test", (Join-Path $RepoRoot "tests\SecureSync.Tests"), "-c", "Release", "--nologo", "-v", "quiet")
}

# --- Android SDK and JDK ---------------------------------------------------------------------------
if (-not $AndroidSdk) {
    $AndroidSdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA "Android\Sdk"), "${env:ProgramFiles(x86)}\Android\android-sdk") |
        Where-Object { $_ -and (Test-Path (Join-Path $_ "platform-tools")) } | Select-Object -First 1
    if (-not $AndroidSdk) { $AndroidSdk = Join-Path $ToolsDir "android-sdk" }
}
if (-not $JavaSdk) {
    $JavaSdk = @($env:JAVA_HOME, "$env:ProgramFiles\Android\Android Studio\jbr", (Join-Path $ToolsDir "jdk")) +
        @(Get-ChildItem "$env:ProgramFiles\Microsoft\jdk-*" -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) |
        Where-Object { $_ -and (Test-Path (Join-Path $_ "bin\keytool.exe")) } | Select-Object -First 1
    if (-not $JavaSdk) { $JavaSdk = Join-Path $ToolsDir "jdk" }
}
Write-Host "Android SDK: $AndroidSdk"
Write-Host "Java JDK:    $JavaSdk"
$sdkArgs = @("-p:AndroidSdkDirectory=$AndroidSdk", "-p:JavaSdkDirectory=$JavaSdk")

Write-Step "Making sure the Android SDK packages are installed"
Invoke-Checked $dotnet (@("build", $project, "-t:InstallAndroidDependencies", "-f", "net10.0-android",
    "-p:AcceptAndroidSdkLicenses=True", "--nologo", "-v", "quiet") + $sdkArgs)

# --- Signing key (created once, reused for every build so updates install over the old app) -------
$keysDir = Join-Path $RepoRoot ".keys"
$keystore = Join-Path $keysDir "securesync-release.keystore"
$passwordFile = Join-Path $keysDir "keystore-password.txt"
if (-not (Test-Path $keystore)) {
    Write-Step "Creating the app signing key in .keys\ (back this folder up!)"
    New-Item -ItemType Directory -Force $keysDir | Out-Null
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes) # works on Windows PowerShell 5.1 too
    $password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
    Set-Content -Path $passwordFile -Value $password -NoNewline -Encoding ascii
    Invoke-Checked (Join-Path $JavaSdk "bin\keytool.exe") @("-genkeypair", "-keystore", $keystore, "-alias", "securesync",
        "-keyalg", "RSA", "-keysize", "4096", "-validity", "10000", "-dname", "CN=SecureSync",
        "-storepass", $password, "-keypass", $password, "-noprompt")
}

# --- Build -----------------------------------------------------------------------------------------
Write-Step "Building and signing the APK"
# Passed via an environment variable: apksigner reads a shared password *file* line by line (store, then key password).
$env:SECURESYNC_SIGNING_PASSWORD = (Get-Content $passwordFile -Raw).Trim()
$publishDir = Join-Path $RepoRoot "src\SecureSync.Android\bin\Release\publish"
# Start from a clean Release build, so an interrupted signing step can never leave an unsigned APK behind.
foreach ($dir in "bin\Release", "obj\Release") {
    $path = Join-Path $RepoRoot "src\SecureSync.Android\$dir"
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}
Invoke-Checked $dotnet (@("publish", $project, "-c", "Release", "-f", "net10.0-android", "-o", $publishDir, "--nologo", "-v", "quiet",
    "-p:AndroidPackageFormats=apk", "-p:AndroidKeyStore=true", "-p:AndroidSigningKeyStore=$keystore",
    "-p:AndroidSigningKeyAlias=securesync", "-p:AndroidSigningKeyPass=env:SECURESYNC_SIGNING_PASSWORD",
    "-p:AndroidSigningStorePass=env:SECURESYNC_SIGNING_PASSWORD") + $sdkArgs)

# Refuse to hand out an APK that isn't properly signed (Android would reject it).
$apkSigner = Get-ChildItem (Join-Path $AndroidSdk "build-tools") -Directory | Sort-Object { [version]($_.Name -replace '[^\d.]', '') } |
    Select-Object -Last 1 | ForEach-Object { Join-Path $_.FullName "lib\apksigner.jar" }

$apk = Get-ChildItem $publishDir -Filter "*-Signed.apk" | Select-Object -First 1
if (-not $apk) { throw "The build didn't produce a signed APK." }
Write-Step "Verifying the signature"
Invoke-Checked (Join-Path $JavaSdk "bin\java.exe") @("-jar", $apkSigner, "verify", $apk.FullName)
New-Item -ItemType Directory -Force $outDir | Out-Null
$target = Join-Path $outDir "SecureSync-$version.apk"
Copy-Item $apk.FullName $target -Force

Write-Step "Done"
Write-Host "APK: $target"
Write-Host ""
Write-Host "To install: copy the APK to your phone and open it (allow 'Install unknown apps' for your file manager"
Write-Host "when asked), or with the phone connected over USB debugging run:"
Write-Host "  `"$AndroidSdk\platform-tools\adb.exe`" install -r `"$target`""
