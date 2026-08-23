<#
.SYNOPSIS
    Real-desktop regression for the taskbar entry: embedded placement, adaptive width,
    full-screen hiding, click-to-toggle and pass-through outside the entry.

.DESCRIPTION
    Starts the real Release x64 LauncherHost and asserts that the entry window lands inside
    the taskbar strip derived from the monitor and work area, honours the configured
    alignment, keeps its width inside the supported range, stays hidden through a placement
    refresh while a foreground window covers the monitor, restores after full-screen exits,
    toggles the panel on click, and lets pointer input through outside its own rounded shape.

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
    [string]$Platform = 'x64',

    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'

if (-not ('WinWidgetBoardLauncherEntryTestNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardLauncherEntryTestNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);
}
'@
}

$perMonitorV2 = [IntPtr](-4)
if (-not [WinWidgetBoardLauncherEntryTestNative]::SetProcessDpiAwarenessContext(
        $perMonitorV2)) {
    $dpiError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    $currentDpiContext =
        [WinWidgetBoardLauncherEntryTestNative]::GetThreadDpiAwarenessContext()
    $alreadyPerMonitor =
        [WinWidgetBoardLauncherEntryTestNative]::AreDpiAwarenessContextsEqual(
            $currentDpiContext,
            $perMonitorV2) -or
        [WinWidgetBoardLauncherEntryTestNative]::AreDpiAwarenessContextsEqual(
            $currentDpiContext,
            [IntPtr](-3))
    if (-not $alreadyPerMonitor) {
        throw "Could not enable per-monitor DPI awareness (Win32 error $dpiError)."
    }
}

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
    -DisableNameChecking -Force
Add-Type -AssemblyName System.Windows.Forms

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherClassName = 'WinWidgetBoard.LauncherHost.EntryWindow'
$launcherPath = Join-Path $repoRoot (
    "artifacts\bin\WinWidgetBoard.LauncherHost\$Configuration\$Platform\" +
    'WinWidgetBoard.LauncherHost.exe')
$panelPath = Join-Path $repoRoot (
    "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
    'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')

$launcherProcess = $null
$fullscreenForm = $null
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
    $dpi = [int][WinWidgetBoardLauncherEntryTestNative]::GetDpiForWindow($entry.Handle)
    if ($dpi -le 0) { throw 'GetDpiForWindow returned an invalid DPI.' }
    $widthLogical = [int][math]::Round($width * 96.0 / $dpi)
    $heightLogical = [int][math]::Round($height * 96.0 / $dpi)
    # The window spans both capsules, so the ceiling is kEntryMaxWidthLogical plus
    # kCapsuleGapLogical plus kMonitorMaxWidthLogical from TaskbarGeometry.cpp; the floor is
    # kEntryMinWidthLogical. This run uses --no-broker, so in practice there are no readings
    # and only the main capsule is drawn.
    if ($widthLogical -lt 96 -or $widthLogical -gt 728) {
        throw "Entry width $widthLogical DIP is outside the supported 96..728 range."
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

    if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
        $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
        [void](New-Item -ItemType Directory -Path $EvidenceDirectory -Force)
        $beforeCapture = Join-Path $EvidenceDirectory 'launcher-entry-visible.png'
        [void](Save-ScreenRegionCapture `
            -Left $bounds.Left `
            -Top $stripTop `
            -Width $bounds.Width `
            -Height ($stripBottom - $stripTop) `
            -Path $beforeCapture)
        Write-Output "ENTRY-BEFORE-CAPTURE $beforeCapture"
    }

    # Use a real borderless foreground window that exactly covers the primary monitor. After
    # the launcher hides, WM_DISPLAYCHANGE forces the same placement-refresh path used by
    # display changes and content-width refits; the entry must remain hidden through it.
    $fullscreenForm = New-Object System.Windows.Forms.Form
    $fullscreenForm.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
    $fullscreenForm.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $fullscreenForm.Bounds = $bounds
    $fullscreenForm.ShowInTaskbar = $false
    $fullscreenForm.BackColor = [System.Drawing.Color]::FromArgb(28, 31, 36)
    $fullscreenForm.Show()
    $fullscreenForm.Activate()
    [void][WinWidgetBoardUiAutomationInput]::SetForegroundWindow($fullscreenForm.Handle)
    Invoke-LeftClickAtPoint `
        -X ([int]($bounds.Left + $bounds.Width / 2)) `
        -Y ([int]($bounds.Top + $bounds.Height / 2))

    $fullscreenDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        $foregroundMatches =
            [WinWidgetBoardLauncherEntryTestNative]::GetForegroundWindow() -eq
            $fullscreenForm.Handle
        $entryHidden =
            -not [WinWidgetBoardUiAutomationInput]::IsWindowVisible($entry.Handle)
        if ($foregroundMatches -and $entryHidden) { break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $fullscreenDeadline)
    if (-not $foregroundMatches) {
        throw 'The borderless monitor-covering test window did not become foreground.'
    }
    if (-not $entryHidden) {
        throw 'The launcher entry stayed visible over a full-screen foreground window.'
    }

    if (-not [WinWidgetBoardLauncherEntryTestNative]::PostMessage(
            $entry.Handle,
            0x007E,
            [UIntPtr]::Zero,
            [IntPtr]::Zero)) {
        throw 'PostMessage(WM_DISPLAYCHANGE) failed for the launcher entry.'
    }
    $refreshDeadline = [DateTime]::UtcNow.AddSeconds(1)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $refreshDeadline)
    if ([WinWidgetBoardUiAutomationInput]::IsWindowVisible($entry.Handle)) {
        throw 'A placement refresh exposed the launcher entry while full-screen remained active.'
    }

    if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
        $fullscreenCapture = Join-Path $EvidenceDirectory 'launcher-fullscreen-hidden.png'
        [void](Save-ScreenRegionCapture `
            -Left $bounds.Left `
            -Top $stripTop `
            -Width $bounds.Width `
            -Height ($stripBottom - $stripTop) `
            -Path $fullscreenCapture)
        Write-Output "ENTRY-FULLSCREEN-CAPTURE $fullscreenCapture"
    }
    Write-Output 'ENTRY-FULLSCREEN-HIDE-PASS activation+placement-refresh'

    $fullscreenForm.Close()
    $fullscreenForm.Dispose()
    $fullscreenForm = $null
    [System.Windows.Forms.Application]::DoEvents()

    $restoreDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $entryRestored =
            [WinWidgetBoardUiAutomationInput]::IsWindowVisible($entry.Handle)
        if ($entryRestored) { break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $restoreDeadline)
    if (-not $entryRestored) {
        throw 'The launcher entry did not return after the full-screen window closed.'
    }
    Write-Output 'ENTRY-FULLSCREEN-RESTORE-PASS'

    # Closing the full-screen window can give Explorer one more opportunity to re-stack the
    # taskbar. Wait again before sending the real click to the restored entry.
    $ownerDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $centerOwner = Get-WindowOwnerProcessAtPoint -X $entry.CenterX -Y $entry.CenterY
        if ($centerOwner -eq $launcherProcess.Id) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $ownerDeadline)
    if ($centerOwner -ne $launcherProcess.Id) {
        throw ("The restored entry never reached the top of the z-order at " +
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
    Write-Output 'REAL-LAUNCHER-ENTRY-PASS placement+passthrough+fullscreen+click'
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $fullscreenForm) {
        $fullscreenForm.Close()
        $fullscreenForm.Dispose()
    }

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
