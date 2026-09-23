<#
.SYNOPSIS
    Installs or updates Glassy System Gadget.
.DESCRIPTION
    Installs the .NET 8 Desktop Runtime via winget if it's missing, downloads the
    latest release zip from GitHub, extracts it to %LocalAppData%\GlassySystemGadget,
    and creates a hidden (no console window) Startup shortcut. Safe to re-run: it
    stops a running copy first and overwrites the install folder, so running it
    again is how you update.
.NOTES
    Targets Windows PowerShell 5.1 as well as PowerShell 7, since it's meant to run
    via `irm ... | iex` on whatever PowerShell a given machine defaults to.
#>

$ErrorActionPreference = 'Stop'

$RepoOwner  = 'SuperBartimus'
$RepoName   = 'GlassySystemGadget'
$InstallDir = Join-Path $env:LocalAppData 'GlassySystemGadget'

function Test-DotNet8DesktopRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
    } catch {
        return $false
    }
    return [bool]($runtimes | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 8.*' })
}

function Install-DotNet8DesktopRuntime {
    Write-Host 'Installing .NET 8 Desktop Runtime via winget...'
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'winget was not found. Install the .NET 8 Desktop Runtime manually (https://dotnet.microsoft.com/download/dotnet/8.0) and re-run this script.'
    }
    & winget.exe install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget exited with code $LASTEXITCODE installing the .NET 8 Desktop Runtime." }
}

function Stop-RunningGadget {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like '*Glassy.App.dll*' }
    foreach ($p in $procs) {
        Write-Host "Stopping running Glassy System Gadget (pid $($p.ProcessId))..."
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($procs) { Start-Sleep -Seconds 1 }   # let the DLLs unlock
}

function Get-LatestReleaseZipUrl {
    $api = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
    $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'GlassySystemGadget-installer' }
    $asset = $release.assets | Where-Object { $_.name -like '*win-x64*.zip' } | Select-Object -First 1
    if (-not $asset) { throw "No win-x64 zip asset found on the latest $RepoName release." }
    return $asset.browser_download_url
}

function New-HiddenStartupShortcut {
    param([Parameter(Mandatory)][string] $InstallDir)

    $vbsPath = Join-Path $InstallDir 'Glassy.vbs'
    $vbsContent = 'CreateObject("Wscript.Shell").Run "dotnet.exe ""' + (Join-Path $InstallDir 'Glassy.App.dll') + '""", 0, False'
    [System.IO.File]::WriteAllText($vbsPath, $vbsContent, (New-Object System.Text.UTF8Encoding($false)))

    $startupDir = [Environment]::GetFolderPath('Startup')
    $shortcutPath = Join-Path $startupDir 'Glassy System Gadget.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = 'wscript.exe'
    $shortcut.Arguments = '"' + $vbsPath + '"'
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = 'Glassy System Gadget'
    $shortcut.Save()
    return $shortcutPath
}

# ---- main ----
if (-not (Test-DotNet8DesktopRuntime)) { Install-DotNet8DesktopRuntime }

Stop-RunningGadget

Write-Host 'Downloading latest release...'
$zipUrl = Get-LatestReleaseZipUrl
$zipPath = Join-Path $env:TEMP 'GlassySystemGadget-latest.zip'
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Remove-Item $zipPath -Force

$shortcut = New-HiddenStartupShortcut -InstallDir $InstallDir
Write-Host "Installed to $InstallDir"
Write-Host "Starts automatically at login (shortcut: $shortcut)."
Write-Host 'Starting it now...'
& wscript.exe (Join-Path $InstallDir 'Glassy.vbs')
