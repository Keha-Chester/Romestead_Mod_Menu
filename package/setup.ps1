# Romestead Cheat Menu setup (run through Install.bat / Uninstall.bat).
#   install    Copies the mod into <game>\CheatMenu when it runs from somewhere else, then registers
#              CheatMenu\RomesteadCheatMenu.dll as a .NET startup hook in Romestead.runtimeconfig.json.
#   uninstall  Removes that registration. The CheatMenu folder can be deleted afterwards.
# The startup hook entry is the only change made to the game's files.
param(
    [ValidateSet('install', 'uninstall')]
    [string]$Action = 'install',
    # Romestead folder; found automatically when omitted.
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'

$ModFiles = 'RomesteadCheatMenu.dll', '0Harmony.dll', 'Install.bat', 'Uninstall.bat', 'setup.ps1', 'README.txt', 'THIRD-PARTY-NOTICES.txt'
$HookPattern = '"STARTUP_HOOKS"\s*:\s*"((?:[^"\\]|\\.)*)"'

function Find-GameDir {
    # Normally this script is already in <game>\CheatMenu.
    $candidates = @(Split-Path -Parent $PSScriptRoot)
    # Otherwise look for the game in every Steam library.
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $steam = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        foreach ($steamDir in @($steam.SteamPath, $steam.InstallPath)) {
            if (-not $steamDir) { continue }
            $candidates += [IO.Path]::Combine($steamDir, 'steamapps\common\romestead')
            $libraries = [IO.Path]::Combine($steamDir, 'steamapps\libraryfolders.vdf')
            if (Test-Path -LiteralPath $libraries) {
                foreach ($library in [regex]::Matches([IO.File]::ReadAllText($libraries), '"path"\s+"([^"]+)"')) {
                    $candidates += [IO.Path]::Combine($library.Groups[1].Value.Replace('\\', '\'), 'steamapps\common\romestead')
                }
            }
        }
    }
    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath ([IO.Path]::Combine($dir, 'Romestead.exe'))) {
            return [IO.Path]::GetFullPath($dir)
        }
    }
    return $null
}

function Read-Config([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw 'Romestead.runtimeconfig.json was not found next to Romestead.exe.' }
    return [IO.File]::ReadAllText($path)
}

function Save-Config([string]$path, [string]$json) {
    # The game does not start with a broken runtimeconfig, so only write text that still parses.
    try { $null = ConvertFrom-Json -InputObject $json }
    catch { throw 'Romestead.runtimeconfig.json has an unexpected format; it was left unchanged.' }
    [IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
}

# STARTUP_HOOKS entries that are not this mod (other mods can use the same mechanism).
function Get-OtherHooks([string]$hooks) {
    return @($hooks -split ';' | Where-Object { $_ -and ($_.Replace('\\', '\') -notlike '*\RomesteadCheatMenu.dll') })
}

function Copy-ModFiles([string]$target) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Get-ChildItem -LiteralPath $target -Filter '*.old-*' | Remove-Item -Force -ErrorAction SilentlyContinue
    foreach ($name in $ModFiles) {
        $source = Join-Path $PSScriptRoot $name
        $destination = Join-Path $target $name
        if (-not (Test-Path -LiteralPath $source)) { continue }
        if ((Test-Path -LiteralPath $destination) -and ((Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $source).Hash)) { continue }
        try {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
        catch {
            # A running game keeps its dlls open, but Windows still allows renaming them out of the way.
            Rename-Item -LiteralPath $destination -NewName ("$name.old-" + (Get-Date -Format 'yyyyMMddHHmmss'))
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
}

function Install-Mod([string]$gameDir) {
    $target = Join-Path $gameDir 'CheatMenu'
    if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($target).TrimEnd('\')) {
        Copy-ModFiles $target
    }
    $hook = Join-Path $target 'RomesteadCheatMenu.dll'
    if (-not (Test-Path -LiteralPath $hook)) { throw "RomesteadCheatMenu.dll was not found in $target." }

    $config = Join-Path $gameDir 'Romestead.runtimeconfig.json'
    $json = Read-Config $config
    if (-not (Test-Path -LiteralPath "$config.cheatmenu-backup")) {
        Copy-Item -LiteralPath $config -Destination "$config.cheatmenu-backup"
    }

    $existing = [regex]::Match($json, $HookPattern)
    $hooks = @()
    if ($existing.Success) { $hooks = @(Get-OtherHooks $existing.Groups[1].Value) }
    $hooks += $hook.Replace('\', '\\')
    $property = '"STARTUP_HOOKS": "' + ($hooks -join ';') + '"'
    if ($existing.Success) {
        $json = $json.Remove($existing.Index, $existing.Length).Insert($existing.Index, $property)
    }
    else {
        $section = [regex]::Match($json, '"configProperties"\s*:\s*\{')
        if ($section.Success) {
            $json = $json.Insert($section.Index + $section.Length, "`r`n      $property,")
        }
        else {
            $section = [regex]::Match($json, '"runtimeOptions"\s*:\s*\{')
            if (-not $section.Success) { throw 'Romestead.runtimeconfig.json has an unexpected format; it was left unchanged.' }
            $json = $json.Insert($section.Index + $section.Length, "`r`n    `"configProperties`": { $property },")
        }
    }
    Save-Config $config $json

    Write-Host "Romestead Cheat Menu is installed in $target" -ForegroundColor Green
    if (Get-Process -Name Romestead -ErrorAction SilentlyContinue) {
        Write-Host 'Romestead is running: the mod loads the next time you start the game.'
    }
    else {
        Write-Host 'Start the game, load a world and press Ctrl+0.'
    }
    Write-Host "If Ctrl+0 stops working after a game update or 'Verify integrity of game files', run Install.bat again."
    Write-Host 'Run Uninstall.bat before deleting the CheatMenu folder, otherwise the game will not start.'
}

function Uninstall-Mod([string]$gameDir) {
    $config = Join-Path $gameDir 'Romestead.runtimeconfig.json'
    $json = Read-Config $config
    $existing = [regex]::Match($json, $HookPattern)
    $all = @()
    $others = @()
    if ($existing.Success) {
        $all = @($existing.Groups[1].Value -split ';' | Where-Object { $_ })
        $others = @(Get-OtherHooks $existing.Groups[1].Value)
    }
    if ($all.Count -eq $others.Count) {
        Write-Host 'Romestead Cheat Menu is not registered in this game folder.'
        return
    }
    if ($others.Count -gt 0) {
        $json = $json.Remove($existing.Index, $existing.Length).Insert($existing.Index, '"STARTUP_HOOKS": "' + ($others -join ';') + '"')
    }
    else {
        # Drop the whole property with its comma, then fix the comma before '}' if it was the last property.
        $whole = [regex]::Match($json, $HookPattern + '\s*,?\s*')
        $json = [regex]::Replace($json.Remove($whole.Index, $whole.Length), ',(\s*[}\]])', '$1')
    }
    Save-Config $config $json
    Write-Host 'Romestead Cheat Menu is uninstalled. You can delete the CheatMenu folder now.' -ForegroundColor Green
}

try {
    if ($GameDir) {
        if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Romestead.exe'))) { throw "Romestead.exe was not found in '$GameDir'." }
        $GameDir = [IO.Path]::GetFullPath($GameDir)
    }
    else {
        $GameDir = Find-GameDir
        if (-not $GameDir) {
            throw 'Romestead was not found. Extract the CheatMenu folder into the game folder (Steam > Romestead > Manage > Browse local files) and run Install.bat from there.'
        }
    }
    if ($Action -eq 'install') { Install-Mod $GameDir } else { Uninstall-Mod $GameDir }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
