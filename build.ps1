[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$BuildElectronFallback,
    [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $root
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
$project = Join-Path $root 'native\PowerShellPlus.Native\PowerShellPlus.Native.csproj'
$nugetConfig = Join-Path $root 'native\NuGet.config'
$publish = Join-Path $root 'release-native'
$buildOutput = Join-Path $root 'native\PowerShellPlus.Native\bin\Release\net8.0-windows10.0.19041.0\win-x64\PowerShellPlus.exe'
$xtermAsset = Join-Path $root 'node_modules\@xterm\xterm\lib\xterm.js'

if (-not (Test-Path -LiteralPath $xtermAsset)) {
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
        throw 'The Remote Access web terminal assets require Node.js/npm for the first build. Install the current Node.js LTS release, reopen PowerShell, and run build.ps1 again.'
    }
    & npm.cmd ci --ignore-scripts --omit=dev
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $xtermAsset)) { throw 'Pinned xterm.js asset restore failed.' }
}

if (-not (Test-Path -LiteralPath $dotnet)) {
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    }
    & $installer -Channel 8.0 -InstallDir (Join-Path $root '.dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'The local .NET 8 SDK installation failed.' }
}

& $dotnet restore $project -r win-x64 --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { throw 'Native dependency restore failed.' }
& $dotnet build $project -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compilation failed.' }

function Invoke-NativeGate([string]$Executable, [string]$ArgumentName, [string]$ReportName) {
    $report = Join-Path $root "build\$ReportName"
    $process = Start-Process -FilePath $Executable -ArgumentList "--$ArgumentName=$report" -Wait -PassThru
    if (-not (Test-Path -LiteralPath $report)) { throw "$ArgumentName did not produce a report." }
    Get-Content -LiteralPath $report -TotalCount 8
    if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath $report -TotalCount 1) -notlike 'PASS*') {
        throw "$ArgumentName failed with exit code $($process.ExitCode)."
    }
}

if (-not $SkipTests) {
    Invoke-NativeGate $buildOutput 'smoke-test' 'native-conpty.txt'
    Invoke-NativeGate $buildOutput 'multi-smoke' 'native-multi-pane.txt'
    Invoke-NativeGate $buildOutput 'codex-smoke' 'native-codex.txt'
    Invoke-NativeGate $buildOutput 'persistence-smoke' 'native-persistence.txt'
    Invoke-NativeGate $buildOutput 'lan-remote-smoke' 'native-lan-remote.txt'
    Invoke-NativeGate $buildOutput 'handoff-smoke' 'native-handoff.txt'
}

& $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Native self-contained publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $publish 'README.md') -Force

$publishedExecutable = Join-Path $publish 'PowerShellPlus.exe'
if (-not $SkipTests) {
    Invoke-NativeGate $publishedExecutable 'smoke-test' 'published-native-conpty.txt'
    Invoke-NativeGate $publishedExecutable 'multi-smoke' 'published-native-multi-pane.txt'
    Invoke-NativeGate $publishedExecutable 'codex-smoke' 'published-native-codex.txt'
    Invoke-NativeGate $publishedExecutable 'persistence-smoke' 'published-native-persistence.txt'
    Invoke-NativeGate $publishedExecutable 'lan-remote-smoke' 'published-native-lan-remote.txt'
    Invoke-NativeGate $publishedExecutable 'handoff-smoke' 'published-native-handoff.txt'
}

$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
$packageSource = $publish
if (-not $StageOnly) {
    $expectedDist = [IO.Path]::GetFullPath("$root\dist")
    if ($dist -ne $expectedDist -or -not $dist.StartsWith([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected output directory: $dist"
    }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Get-ChildItem -LiteralPath $dist -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $publish '*') -Destination $dist -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $dist 'README.md') -Force
    $packageSource = $dist
}

$zipName = if ($StageOnly) { 'PowerShellPlus-win-x64-staged.zip' } else { 'PowerShellPlus-win-x64.zip' }
$zip = Join-Path $root $zipName
Compress-Archive -Path (Join-Path $packageSource '*') -DestinationPath $zip -Force

if ($BuildElectronFallback) {
    & npm.cmd install
    if ($LASTEXITCODE -ne 0) { throw 'Electron fallback dependency install failed.' }
    & npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw 'Electron fallback tests failed.' }
    & npm.cmd run dist
    if ($LASTEXITCODE -ne 0) { throw 'Electron fallback packaging failed.' }
}

Write-Host ''
Write-Host 'PowerShellPlus Native build complete.' -ForegroundColor Green
Write-Host "Application: $packageSource\PowerShellPlus.exe"
Write-Host "ZIP package: $zip"
if ($StageOnly) { Write-Host 'Deployment: staged only (dist was left untouched)' }
Write-Host 'Renderer: Microsoft TerminalControl (WPF) + ConPTY'
