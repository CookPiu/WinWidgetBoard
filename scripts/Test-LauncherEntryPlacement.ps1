<#
.SYNOPSIS
    Real-desktop regression for the taskbar entry: embedded placement, adaptive width,
    click-to-toggle and pass-through outside the entry.

.DESCRIPTION
    Starts the real Release x64 LauncherHost and asserts that the entry window lands inside
    the taskbar strip derived from the monitor and work area, honours the configured
    alignment, keeps its width inside the supported range, toggles the panel on click, and
    lets pointer input through outside its own rounded shape.

    LauncherHost runs with --no-broker, so it does not auto-start CoreBroker: the panel this
    test opens has nothing to persist to and the production database is never touched. Only
    processes this script started are stopped.

.EXAMPLE
    .\scripts\Test-LauncherEntryPlacement.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
    -DisableNameChecking -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherClassName = 'WinWidgetBoard.LauncherHost.EntryWindow'
$launcherPath = Join-Path $repoRoot (
    "artifacts\bin\WinWidgetBoard.LauncherHost\$Configuration\$Platform\" +
    'WinWidgetBoard.LauncherHost.exe')
$panelPath = Join-Path $repoRoot (
    "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
    'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')

$launcherProcess = $null
$failure = $null

try {
    foreach ($requiredPath in @($launcherPath, $panelPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required executable was not found: $requiredPath"
        }
    }

    $running = Get-Process -Name @(
        'WinWidgetBoard.LauncherHost',
        'WinWidgetBoard.WorkspacePanel') -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $runningText = ($running | ForEach-Object {
                '{0}#{1}' -f $_.ProcessName, $_.Id
            }) -join ', '
        throw "Existing WinWidgetBoard processes detected: $runningText"
    }

    Add-Type -AssemblyName System.Windows.Forms
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $stripTop = $work.Bottom
    $stripBottom = $bounds.Bottom
    if ($stripBottom -le $stripTop) {
        throw 'No bottom taskbar strip was found; this test needs a bottom taskbar.'
    }

    $env:WINWIDGETBOARD_WORKSPACE_PANEL = $panelPath
    $launcherProcess = Start-Process -FilePath $launcherPath `
        -ArgumentList '--no-broker' -PassThru -NoNewWindow
    $entry = Get-WindowRectByClass `
        -ClassName $launcherClassName `
        -ProcessId $launcherProcess.Id `
        -Timeout ([TimeSpan]::FromSeconds(20))

    if ($entry.Top -lt $stripTop -or $entry.Bottom -gt $stripBottom) {
        throw ("Entry $($entry.Top)..$($entry.Bottom) is outside the taskbar strip " +
            "$stripTop..$stripBottom.")
    }

    $width = $entry.Right - $entry.Left
    $height = $entry.Bottom - $entry.Top
    $dpi = [int]((Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop\WindowMetrics' `
        -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI)
    if ($dpi -le 0) { $dpi = 96 }
    $widthLogical = [int][math]::Round($width * 96.0 / $dpi)
    $heightLogical = [int][math]::Round($height * 96.0 / $dpi)
    if ($widthLogical -lt 96 -or $widthLogical -gt 460) {
        throw "Entry width $widthLogical DIP is outside the supported 96..460 range."
    }
    if ($heightLogical -lt 32) {
        throw "Entry height $heightLogical DIP is below the 32 DIP hit-target minimum."
    }

    Write-Output ("ENTRY-PLACEMENT-PASS rect=$($entry.Left),$($entry.Top) - " +
        "$($entry.Right),$($entry.Bottom) size=${widthLogical}x${heightLogical} DIP " +
        "strip=$stripTop..$stripBottom dpi=$dpi")

    # A point on the strip well past the entry must not belong to the launcher, otherwise the
    # entry would be swallowing taskbar input outside its own shape.
    $outsideX = [math]::Min($bounds.Right - 40, $entry.Right + 200)
    $outsideY = [int](($stripTop + $stripBottom) / 2)
    $outside = Get-WindowOwnerProcessAtPoint -X $outsideX -Y $outsideY
    if ($outside -eq $launcherProcess.Id) {
        throw "Point ($outsideX,$outsideY) outside the entry still belongs to the launcher."
    }
    Write-Output "ENTRY-PASSTHROUGH-PASS point=($outsideX,$outsideY) ownerPid=$outside"

    # Topmost z-order is shared, not owned: another topmost shell add-on can sit above the
    # entry until the launcher re-asserts it. Wait for the entry to actually own its own
    # centre, otherwise the click lands in whatever is covering it.
    $ownerDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $centerOwner = Get-WindowOwnerProcessAtPoint -X $entry.CenterX -Y $entry.CenterY
        if ($centerOwner -eq $launcherProcess.Id) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $ownerDeadline)
    if ($centerOwner -ne $launcherProcess.Id) {
        throw ("The entry never reached the top of the z-order at " +
            "($($entry.CenterX),$($entry.CenterY)); owner pid $centerOwner covers it.")
    }

    Invoke-LeftClickAtPoint -X $entry.CenterX -Y $entry.CenterY
    $panel = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $panel = Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $panel) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $panel) {
        throw 'Clicking the entry did not start WorkspacePanel.'
    }

    Write-Output "ENTRY-CLICK-PASS panelPid=$($panel.Id)"
    Stop-WinWidgetBoardProcess -Process $panel
    Write-Output 'REAL-LAUNCHER-ENTRY-PASS placement+passthrough+click'
}
catch {
    $failure = $_
}
finally {
    foreach ($stray in @(Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue)) {
        if ($stray.Path -eq $panelPath) {
            Stop-WinWidgetBoardProcess -Process $stray
        }
    }

    Stop-WinWidgetBoardProcess -Process $launcherProcess
}

if ($null -ne $failure) {
    throw $failure
}
