$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publish = Join-Path $root 'release-native'
$dist = Join-Path $root 'dist'
if (-not (Test-Path (Join-Path $publish 'PowerShellPlus.exe'))) { throw 'release-native is missing; run the publish step first.' }
if (Get-Process PowerShellPlus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" }) { throw 'PowerShellPlus is running from dist; close it first.' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Get-ChildItem -LiteralPath $dist -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $publish '*') -Destination $dist -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $dist 'README.md') -Force
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath (Join-Path $root 'PowerShellPlus-win-x64.zip') -Force
Write-Output "Deployed release-native to dist and refreshed PowerShellPlus-win-x64.zip"
