[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',
    [switch]$SkipKeyboardShortcuts
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') -DisableNameChecking -Force
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardHistoryInput
{
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VirtualKeyControl = 0x11;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

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

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public static bool MakeTopmost(IntPtr window)
    {
        return SetWindowPos(
            window,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    public static void SendControlShortcut(byte virtualKey)
    {
        keybd_event(VirtualKeyControl, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VirtualKeyControl, 0, KeyEventKeyUp, UIntPtr.Zero);
    }
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

if ($null -ne (Get-Process -Name 'WinWidgetBoard.LauncherHost' -ErrorAction SilentlyContinue)) {
    throw 'A WinWidgetBoard.LauncherHost process is already running.'
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
$startInfo.UseShellExecute = $false
$acceptanceInstanceId = [Guid]::NewGuid().ToString('N')
$startInfo.Arguments =
    "--acceptance-test --test-instance-id $acceptanceInstanceId"
$startInfo.Environment['DOTNET_ROOT'] = $PortableDotnetRoot
$startInfo.Environment['DOTNET_ROOT_X64'] = $PortableDotnetRoot
$panelProcess = $null

function Wait-VisibleElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastState = 'not found'
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            $rectangle = $element.Current.BoundingRectangle
            $lastState = 'name={0}; offscreen={1}; enabled={2}; bounds={3},{4},{5},{6}' -f
                $element.Current.Name,
                $element.Current.IsOffscreen,
                $element.Current.IsEnabled,
                [Math]::Round($rectangle.X),
                [Math]::Round($rectangle.Y),
                [Math]::Round($rectangle.Width),
                [Math]::Round($rectangle.Height)
            if (-not $element.Current.IsOffscreen) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $processState = if ($null -eq $script:panelProcess) {
        'not-started'
    }
    elseif ($script:panelProcess.HasExited) {
        "exited:$($script:panelProcess.ExitCode)"
    }
    else {
        'running'
    }
    $rootState = try {
        "offscreen=$($Root.Current.IsOffscreen); enabled=$($Root.Current.IsEnabled)"
    }
    catch {
        "unavailable:$($_.Exception.GetType().Name)"
    }
    throw "Visible UI Automation element not found: $AutomationId. Last state: $lastState. Process: $processState. Root: $rootState"
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
    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'WorkspacePanel returned an invalid native window handle.'
    }

    [void][WinWidgetBoardHistoryInput]::ShowWindow($windowHandle, 5)
    if (-not [WinWidgetBoardHistoryInput]::MakeTopmost($windowHandle)) {
        throw 'WorkspacePanel could not be made temporarily topmost for keyboard acceptance.'
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
    # InvokePattern does not require the tested window to own the keyboard
    # foreground. Windows can reject SetForegroundWindow for a valid,
    # visible cross-process UIA target, so focus is best effort here.

    Start-Sleep -Milliseconds 300
    $editButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'EditLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    Assert-ElementHiddenByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardResizeFrame'
    Invoke-Element -Element $editButton
    Start-Sleep -Milliseconds 500

    [void](Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardResizeFrame' `
        -Timeout ([TimeSpan]::FromSeconds(5)))
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

    if ($SkipKeyboardShortcuts) {
        Write-Output 'REAL-HISTORY-PASS mode-visibility+resize+buttons keyboard-skipped'
        return
    }

    [void][WinWidgetBoardHistoryInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardHistoryInput]::SetForegroundWindow($windowHandle)
    try {
        $window.SetFocus()
    }
    catch {
    }
    Start-Sleep -Milliseconds 200
    if ([WinWidgetBoardHistoryInput]::GetForegroundWindow() -ne $windowHandle) {
        throw 'WorkspacePanel did not own keyboard foreground for shortcut acceptance.'
    }

    [WinWidgetBoardHistoryInput]::SendControlShortcut(0x5A)
    Start-Sleep -Milliseconds 500
    $undoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'UndoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    $redoButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'RedoLayoutButton' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    if ($undoButton.Current.IsEnabled -or -not $redoButton.Current.IsEnabled) {
        throw 'Ctrl+Z did not undo the real layout resize.'
    }

    [WinWidgetBoardHistoryInput]::SendControlShortcut(0x59)
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
        throw 'Ctrl+Y did not redo the real layout resize.'
    }

    Write-Output 'REAL-HISTORY-PASS mode-visibility+resize+buttons+ctrl-z+ctrl-y'
}
finally {
    Stop-OwnedProcess -Process $panelProcess
}
