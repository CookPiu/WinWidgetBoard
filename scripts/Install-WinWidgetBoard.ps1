<#
.SYNOPSIS
    Installs WinWidgetBoard for everyday use: copies the Release x64 build to a stable
    location, creates a Start menu shortcut, and optionally starts it at sign-in.

.DESCRIPTION
    The installed entry is self-sufficient. LauncherHost starts CoreBroker itself, generates
    the session token in memory, and binds every child process to a job object, so a plain
    shortcut to WinWidgetBoard.LauncherHost.exe brings the whole product up with no wrapper
    script and no console window.

    Layout under the install root:

        WinWidgetBoard.LauncherHost.exe   the entry itself
        WorkspacePanel\                   the panel and its dependencies
        CoreBroker\                       the broker and its dependencies

    LauncherHost resolves both children from those subdirectories, so the three .NET
    applications keep their own dependency sets instead of being flattened together.

    This installs for the current user only: no elevation, no services, no registry classes,
    and nothing outside the install root and the two shortcut locations. -Uninstall reverses
    all of it.

.PARAMETER Uninstall
    Stops the installed processes, removes both shortcuts and deletes the install root. User
    data under %LOCALAPPDATA%\WinWidgetBoard\data is left alone unless -PurgeData is given.

.EXAMPLE
    .\scripts\Install-WinWidgetBoard.ps1

.EXAMPLE
    .\scripts\Install-WinWidgetBoard.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'WinWidgetBoard\app'),

    # Skip the sign-in shortcut. The Start menu shortcut is still created.
    [switch]$NoAutoStart,

    # Do not start the entry after installing.
    [switch]$NoLaunch,

    [switch]$Uninstall,

    # Only meaningful with -Uninstall: also delete the local database and settings.
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$shortcutName = 'WinWidgetBoard.lnk'
$startMenuShortcut = Join-Path $env:APPDATA (
    "Microsoft\Windows\Start Menu\Programs\$shortcutName")
$startupShortcut = Join-Path $env:APPDATA (
    "Microsoft\Windows\Start Menu\Programs\Startup\$shortcutName")
$launcherExecutable = Join-Path $InstallRoot 'WinWidgetBoard.LauncherHost.exe'

function Stop-InstalledProcesses {
    param([string]$Root)

    $names = @(
        'WinWidgetBoard.LauncherHost',
        'WinWidgetBoard.WorkspacePanel',
        'WinWidgetBoard.CoreBroker')
    $stopped = @()
    foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { }
        # Only touch processes running out of the install root: a developer build running
        # from artifacts\ is somebody else's session.
        if ($null -ne $path -and $path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
            try {
                $process.Kill()
                [void]$process.WaitForExit(5000)
                $stopped += $process.ProcessName
            }
            catch { }
        }
    }

    if ($stopped.Count -gt 0) {
        Write-Output "  stopped: $($stopped -join ', ')"
    }
}

function New-ApplicationShortcut {
    param([string]$Path, [string]$Target, [string]$WorkingDirectory)

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $Target
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = 'WinWidgetBoard taskbar entry'
        $shortcut.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

if ($Uninstall) {
    Write-Output "Uninstalling from $InstallRoot"
    Stop-InstalledProcesses -Root $InstallRoot

    foreach ($shortcut in @($startMenuShortcut, $startupShortcut)) {
        if (Test-Path -LiteralPath $shortcut) {
            Remove-Item -LiteralPath $shortcut -Force
            Write-Output "  removed shortcut: $shortcut"
        }
    }

    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
        Write-Output "  removed: $InstallRoot"
    }

    if ($PurgeData) {
        $dataRoot = Join-Path $env:LOCALAPPDATA 'WinWidgetBoard'
        if (Test-Path -LiteralPath $dataRoot) {
            Remove-Item -LiteralPath $dataRoot -Recurse -Force
            Write-Output "  removed data: $dataRoot"
        }
    }
    else {
        Write-Output '  local data was kept; pass -PurgeData to delete it as well'
    }

    Write-Output 'UNINSTALL-COMPLETE'
    return
}

$components = @(
    [pscustomobject]@{
        Name = 'LauncherHost'
        Source = Join-Path $repoRoot (
            "artifacts\bin\WinWidgetBoard.LauncherHost\$Configuration\$Platform")
        Destination = $InstallRoot
        Executable = 'WinWidgetBoard.LauncherHost.exe'
    },
    [pscustomobject]@{
        Name = 'WorkspacePanel'
        Source = Join-Path $repoRoot (
            "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
            'net10.0-windows10.0.26100.0\win-x64')
        Destination = Join-Path $InstallRoot 'WorkspacePanel'
        Executable = 'WinWidgetBoard.WorkspacePanel.exe'
    },
    [pscustomobject]@{
        Name = 'CoreBroker'
        Source = Join-Path $repoRoot (
            "artifacts\bin\WinWidgetBoard.CoreBroker\$Configuration\" +
            'net10.0-windows10.0.26100.0\win-x64')
        Destination = Join-Path $InstallRoot 'CoreBroker'
        Executable = 'WinWidgetBoard.CoreBroker.exe'
    })

foreach ($component in $components) {
    $executablePath = Join-Path $component.Source $component.Executable
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw (
            "$($component.Name) is not built: $executablePath is missing. " +
            'Build it first - see the Build section of CLAUDE.md, or run ' +
            '.\scripts\Start-DevSandbox.ps1 -Task Build.')
    }
}

Write-Output "Installing to $InstallRoot"
Stop-InstalledProcesses -Root $InstallRoot

# Replace the whole root rather than merging into it, so a file dropped between builds
# cannot linger and shadow a newer one.
if (Test-Path -LiteralPath $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

foreach ($component in $components) {
    New-Item -ItemType Directory -Path $component.Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $component.Source '*') `
        -Destination $component.Destination -Recurse -Force
    $fileCount = @(Get-ChildItem -LiteralPath $component.Destination -Recurse -File).Count
    Write-Output ("  {0,-15} {1,4} files -> {2}" -f $component.Name, $fileCount, $component.Destination)
}

New-ApplicationShortcut -Path $startMenuShortcut -Target $launcherExecutable `
    -WorkingDirectory $InstallRoot
Write-Output "  Start menu shortcut: $startMenuShortcut"

if ($NoAutoStart) {
    if (Test-Path -LiteralPath $startupShortcut) {
        Remove-Item -LiteralPath $startupShortcut -Force
    }
    Write-Output '  sign-in start: disabled (-NoAutoStart)'
}
else {
    New-ApplicationShortcut -Path $startupShortcut -Target $launcherExecutable `
        -WorkingDirectory $InstallRoot
    Write-Output "  sign-in shortcut:    $startupShortcut"
}

if (-not $NoLaunch) {
    Start-Process -FilePath $launcherExecutable -WorkingDirectory $InstallRoot | Out-Null
    Write-Output '  started'
}

Write-Output 'INSTALL-COMPLETE'
Write-Output "Remove with: .\scripts\Install-WinWidgetBoard.ps1 -Uninstall"
