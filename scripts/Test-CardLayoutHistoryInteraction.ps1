[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardHistoryInput
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
}
'@

$repoRoot = Split-Path -Parent $PSScriptRoot
$panelPath = Join-Path $repoRoot (
    "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
    'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')
$panelPath = [IO.Path]::GetFullPath($panelPath)
if (-not (Test-Path -LiteralPath $panelPath -PathType Leaf)) {
    throw "WorkspacePanel executable was not found: $panelPath"
}

if ($null -ne (Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue)) {
    throw 'A WinWidgetBoard.WorkspacePanel process is already running.'
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
$startInfo.UseShellExecute = $false
$startInfo.Environment['DOTNET_ROOT'] = $PortableDotnetRoot
$startInfo.Environment['DOTNET_ROOT_X64'] = $PortableDotnetRoot
$panelProcess = $null

function Get-PanelWindow {
    param(
        [int]$ProcessId,
        [TimeSpan]$Timeout
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
        if ($null -eq $window) {
            Start-Sleep -Milliseconds 100
        }
    } while ($null -eq $window -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $window) {
        throw "WorkspacePanel window did not appear within $($Timeout.TotalSeconds) seconds."
    }

    return $window
}

function Get-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-VisibleElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element -and -not $element.Current.IsOffscreen) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element not found: $AutomationId"
}

function Invoke-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    if (-not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen) {
        throw "UI Automation element is not invokable: $($Element.Current.Name)"
    }

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "InvokePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Get-EnabledResizeButton {
    param(
        [System.Windows.Automation.AutomationElement]$Root
    )

    $names = @('放大卡片', 'Make card larger')
    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if ($names -contains $element.Current.Name -and
            $element.Current.IsEnabled -and
            -not $element.Current.IsOffscreen) {
            return $element
        }
    }

    throw 'No enabled card resize button was found in layout edit mode.'
}

try {
    $panelProcess = [Diagnostics.Process]::Start($startInfo)
    $window = Get-PanelWindow -ProcessId $panelProcess.Id -Timeout ([TimeSpan]::FromSeconds(10))
    $windowHandle = [IntPtr]$window.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero -or
        -not [WinWidgetBoardHistoryInput]::ShowWindow($windowHandle, 5)) {
        throw 'WorkspacePanel could not be brought to the foreground for UI Automation.'
    }

    [void][WinWidgetBoardHistoryInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardHistoryInput]::SetActiveWindow($windowHandle)
    $foregroundSet = [WinWidgetBoardHistoryInput]::SetForegroundWindow($windowHandle)
    $focusSet = $false
    try {
        $window.SetFocus()
        $focusSet = $true
    }
    catch {
        # UIA focus can be unavailable in a non-interactive desktop session.
    }
    if (-not $foregroundSet -and -not $focusSet) {
        throw 'WorkspacePanel could not be focused for UI Automation.'
    }

    Start-Sleep -Milliseconds 300
    $editButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'EditLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    Invoke-Element -Element $editButton
    Start-Sleep -Milliseconds 500

    $undoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'UndoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    $redoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'RedoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    if ($undoButton.Current.IsEnabled -or $redoButton.Current.IsEnabled) {
        throw 'Undo and redo must start disabled.'
    }

    Invoke-Element -Element (Get-EnabledResizeButton -Root $window)
    Start-Sleep -Milliseconds 500
    $undoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'UndoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    if (-not $undoButton.Current.IsEnabled) {
        throw 'Undo did not become enabled after a real resize.'
    }

    Invoke-Element -Element $undoButton
    Start-Sleep -Milliseconds 500
    $redoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'RedoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    if (-not $redoButton.Current.IsEnabled) {
        throw 'Redo did not become enabled after undo.'
    }

    Invoke-Element -Element $redoButton
    Start-Sleep -Milliseconds 500
    $undoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'UndoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    $redoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'RedoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    if (-not $undoButton.Current.IsEnabled -or $redoButton.Current.IsEnabled) {
        throw 'Undo/redo state is incorrect after redo.'
    }

    Write-Output 'REAL-HISTORY-PASS resize+undo+redo UIA'
}
finally {
    if ($null -ne $panelProcess -and -not $panelProcess.HasExited) {
        [void]$panelProcess.CloseMainWindow()
        if (-not $panelProcess.WaitForExit(3000)) {
            $panelProcess.Kill($true)
            [void]$panelProcess.WaitForExit(3000)
        }
    }
}
