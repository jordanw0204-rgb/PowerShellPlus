[CmdletBinding()]
param(
    [switch]$SkipAppBuild,
    [switch]$RunSmokeTest,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $root
$project = Join-Path $root 'native\PowerShellPlus.Native\PowerShellPlus.Native.csproj'
$release = Join-Path $root 'release-native'
$stagedZip = Join-Path $root 'PowerShellPlus-win-x64-staged.zip'
$output = [IO.Path]::GetFullPath((Join-Path $root 'build\installer'))
$script = Join-Path $root 'installer\PowerShellPlus.iss'
$installerSource = Get-Content -LiteralPath $script -Raw
if ($installerSource -notmatch [regex]::Escape('Name: "{autodesktop}\PowerShellPlus"')
    -or $installerSource -notmatch [regex]::Escape('Filename: "{app}\PowerShellPlus.exe"')
    -or $installerSource -notmatch 'ShouldCreateDesktopShortcut') {
    throw 'The installer must create a stable PowerShellPlus desktop shortcut by default.'
}

if (-not $SkipAppBuild)
{
    & (Join-Path $root 'build.ps1') -StageOnly
    if ($LASTEXITCODE -ne 0) { throw 'The PowerShellPlus application build failed.' }
}

[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The project does not define a release version.' }
if ($ExpectedVersion -and $version -ne $ExpectedVersion) {
    throw "The tag version '$ExpectedVersion' does not match project version '$version'."
}
if (-not (Test-Path -LiteralPath (Join-Path $release 'PowerShellPlus.exe'))) { throw 'The published application is missing.' }
if (-not (Test-Path -LiteralPath $stagedZip)) { throw 'The staged portable archive is missing.' }

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
)
$iscc = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget.' }

New-Item -ItemType Directory -Force -Path $output | Out-Null
& $iscc "/DMyAppVersion=$version" "/DMySourceDir=$release" "/DMyOutputDir=$output" $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installer = Join-Path $output 'PowerShellPlus-Setup-x64.exe'
$portable = Join-Path $output 'PowerShellPlus-Portable-x64.zip'
if (-not (Test-Path -LiteralPath $installer)) { throw 'The installer was not produced.' }
Copy-Item -LiteralPath $stagedZip -Destination $portable -Force
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$portableHash = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant()
@(
    "$installerHash  PowerShellPlus-Setup-x64.exe",
    "$portableHash  PowerShellPlus-Portable-x64.zip"
) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii

if ($RunSmokeTest)
{
    $smokeDir = [IO.Path]::GetFullPath((Join-Path $root 'build\installer-smoke-install'))
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'build'))
    if (-not $smokeDir.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer smoke path escaped the build directory.' }
    if (Test-Path -LiteralPath $smokeDir) { Remove-Item -LiteralPath $smokeDir -Recurse -Force }
    $install = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS','/NODESKTOPSHORTCUT=1',"/DIR=$smokeDir",'/UPDATE=0') -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Installer smoke install failed with exit code $($install.ExitCode)." }
    $installedExe = Join-Path $smokeDir 'PowerShellPlus.exe'
    $uninstaller = Join-Path $smokeDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $installedExe) -or -not (Test-Path -LiteralPath $uninstaller)) { throw 'Installer smoke output was incomplete.' }
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    if (-not $installedVersion.StartsWith($version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer smoke deployed unexpected version '$installedVersion'."
    }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Installer smoke uninstall failed with exit code $($uninstall.ExitCode)." }
    if (Test-Path -LiteralPath $smokeDir) { Remove-Item -LiteralPath $smokeDir -Recurse -Force }
    "PASS Installer installed and uninstalled PowerShellPlus $version in an isolated directory." |
        Set-Content -LiteralPath (Join-Path $output 'installer-smoke.txt')
}

Write-Host "Installer: $installer" -ForegroundColor Green
Write-Host "Portable:  $portable"
Write-Host "Checksums: $(Join-Path $output 'SHA256SUMS.txt')"
