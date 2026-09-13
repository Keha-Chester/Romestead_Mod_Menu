# Builds the mod and packs dist\RomesteadCheatMenu-v<version>.zip, whose CheatMenu folder is extracted into the game folder.
#   -Deploy   also installs the fresh build into the local game (package\setup.ps1 install)
#   -GameDir  Romestead folder, when it is not in the default Steam library
param(
    [switch]$Deploy,
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$modDir = Join-Path $dist 'CheatMenu'

# Start from an empty folder so nothing stale ends up in the archive; the Release build copies the files in.
if (Test-Path $modDir) { Remove-Item -Recurse -Force $modDir }
$buildArgs = @('build', (Join-Path $root 'RomesteadCheatMenu.csproj'), '-c', 'Release', '--nologo')
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'Plugin.cs')), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
$zip = Join-Path $dist "RomesteadCheatMenu-v$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# tar.exe writes zip entries with forward slashes; Compress-Archive in Windows PowerShell 5.1 does not.
& tar.exe -a -c -f $zip -C $dist CheatMenu
if ($LASTEXITCODE -ne 0) { throw 'Packing failed.' }
Write-Host "Packed $zip"

if ($Deploy) {
    $setup = Join-Path $modDir 'setup.ps1'
    if ($GameDir) { & $setup install -GameDir $GameDir } else { & $setup install }
}
