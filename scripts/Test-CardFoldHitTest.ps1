<#
.SYNOPSIS
    Real-desktop regression for the card fold: after folding, does XAML hit testing still
    agree with where the cards are painted?

.DESCRIPTION
    The fold animates each card's own composition visual, which is not in the XAML hit-test
    chain. That is acceptable only because every property returns to identity when the motion
    settles. If a reset were ever missed, the panel would arrange and paint correctly while
    pointer input landed somewhere else - which is exactly the defect a redirected visual once
    caused permanently, and which looks like anything but a motion bug from the outside.

    Each cycle opens the panel through the taskbar entry, waits for the fold to settle, and
    asks the OS what element sits at the painted centre of several cards spread across the
    grid. The answer has to be part of that same card. Closing runs the fold in the other
    direction, so four cycles cover eight folds.

    Unlike Test-CardDragInteraction this needs no pointer drag and no foreground transfer to
    the console host, so it runs on a desktop that has other windows open.

    LauncherHost runs with --no-broker: the cards' content does not matter here, only their
    geometry, and the production database is never touched.

.EXAMPLE
    .\scripts\Test-CardFoldHitTest.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [int]$Cycles = 4
)

$ErrorActionPreference = 'Stop'

if (-not ('WinWidgetBoardFoldHitTestNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardFoldHitTestNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@
}

[void][WinWidgetBoardFoldHitTestNative]::SetProcessDpiAwarenessContext([IntPtr](-4))

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

# Spread across the grid, so a uniform displacement cannot hide inside one card.
# Chosen to be measurable with --no-broker as well: the runtime status blocks render in
# every state, so the set does not shrink to the deferred placeholder cards when the
# provider-backed ones have nothing to show.
$probes = @(
    'NotesCardRuntimeStatus',
    'WeatherCardRuntimeStatus',
    'NoteBodyBox',
    'NoteTitleBox',
    'WeatherLocationText',
    'SysMonSettingsButton',
    'TimerCardRuntimeStatus',
    'TodoCardRuntimeStatus',
    'CalendarCardRuntimeStatus')

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

    $env:WINWIDGETBOARD_WORKSPACE_PANEL = $panelPath
    $launcherProcess = Start-Process -FilePath $launcherPath `
        -ArgumentList '--no-broker' -PassThru -NoNewWindow
    $entry = Get-WindowRectByClass `
        -ClassName $launcherClassName `
        -ProcessId $launcherProcess.Id `
        -Timeout ([TimeSpan]::FromSeconds(20))

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        Invoke-LeftClickAtPoint -X $entry.CenterX -Y $entry.CenterY
        $panel = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        do {
            Start-Sleep -Milliseconds 400
            $panelProcess = Get-Process -Name 'WinWidgetBoard.WorkspacePanel' `
                -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $panelProcess) {
                try {
                    $panel = Get-PanelWindow `
                        -ProcessId $panelProcess.Id `
                        -Timeout ([TimeSpan]::FromMilliseconds(600))
                }
                catch {
                    $panel = $null
                }
            }
        } while ($null -eq $panel -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $panel) {
            throw "The panel did not open on cycle $cycle."
        }

        # Let the fold settle. A mid-fold reading proves nothing: the displacement is
        # expected while the motion runs, and only a residual one is a defect.
        Start-Sleep -Seconds 2
        $panelPid = $panelProcess.Id
        $checked = 0
        $mismatches = @()
        $seen = @()
        foreach ($id in $probes) {
            $element = Get-ElementByAutomationId -Root $panel -AutomationId $id
            if ($null -eq $element) { continue }
            $rect = $element.Current.BoundingRectangle
            if (-not [double]::IsFinite($rect.X) -or
                $rect.Width -lt 8 -or
                $rect.Height -lt 8) {
                continue
            }

            $checked++
            # Probes never overlap when the board is laid out: each sits in its own card, and
            # the two that share the notes card sit in different rows. Overlap therefore means
            # cards piled on top of each other.
            #
            # Hit testing alone cannot catch a pile-up: writing a visual's Offset instead of
            # its Translation replaced the layout position and stacked every card in the
            # parent's top-left corner, and the hit test still agreed because the picture and
            # the input had moved together.
            #
            # Honest limit: reintroducing that bug did not reproduce the pile-up under this
            # script's --no-broker conditions - the cards stayed where they belonged - so this
            # check is an invariant that should hold rather than one proven to catch that
            # particular failure.
            foreach ($prior in $seen) {
                if ($rect.X -lt ($prior.Rect.X + $prior.Rect.Width) -and
                    ($rect.X + $rect.Width) -gt $prior.Rect.X -and
                    $rect.Y -lt ($prior.Rect.Y + $prior.Rect.Height) -and
                    ($rect.Y + $rect.Height) -gt $prior.Rect.Y) {
                    $mismatches += "$id : overlaps $($prior.Id); cards are stacked"
                }
            }

            $seen += [pscustomobject]@{ Id = $id; Rect = $rect }

            $centreX = [int]($rect.X + $rect.Width / 2)
            $centreY = [int]($rect.Y + $rect.Height / 2)
            $hit = [System.Windows.Automation.AutomationElement]::FromPoint(
                [System.Windows.Point]::new($centreX, $centreY))
            if ($null -eq $hit) {
                $mismatches += "$id : nothing at its own painted centre"
                continue
            }

            # Compared by geometry rather than by walking the tree: what matters is that the
            # OS finds this same piece of the panel there, not that UIA returns one depth.
            $hitRect = $hit.Current.BoundingRectangle
            $overlaps = [double]::IsFinite($hitRect.X) -and
                $hitRect.X -lt ($rect.X + $rect.Width) -and
                ($hitRect.X + $hitRect.Width) -gt $rect.X -and
                $hitRect.Y -lt ($rect.Y + $rect.Height) -and
                ($hitRect.Y + $hitRect.Height) -gt $rect.Y
            if ($hit.Current.ProcessId -ne $panelPid -or -not $overlaps) {
                $mismatches += (
                    "$id : centre ($centreX,$centreY) hits pid " +
                    "$($hit.Current.ProcessId) $($hit.Current.ControlType.ProgrammaticName)")
            }
        }

        if ($checked -eq 0) {
            throw "No card probe was measurable on cycle $cycle."
        }

        if ($mismatches.Count -gt 0) {
            throw ("Hit testing disagrees with the painted position after fold cycle " +
                "${cycle}: " + ($mismatches -join '; '))
        }

        Write-Output "FOLD-HITTEST-CYCLE-PASS cycle=$cycle probes=$checked"

        $close = Get-ElementByAutomationId -Root $panel -AutomationId 'ClosePanelButton'
        if ($null -ne $close) {
            Invoke-Element -Element $close
        }

        Start-Sleep -Seconds 3
    }

    Write-Output "REAL-CARD-FOLD-HITTEST-PASS cycles=$Cycles"
}
catch {
    $failure = $_
}
finally {
    foreach ($stray in @(Get-Process -Name 'WinWidgetBoard.WorkspacePanel' `
                -ErrorAction SilentlyContinue)) {
        if ($stray.Path -eq $panelPath) {
            Stop-WinWidgetBoardProcess -Process $stray
        }
    }

    Stop-WinWidgetBoardProcess -Process $launcherProcess
}

if ($null -ne $failure) {
    throw $failure
}
