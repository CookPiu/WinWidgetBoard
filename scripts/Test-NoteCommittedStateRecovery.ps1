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

public static class WinWidgetBoardNoteRecoveryInput
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
$panelPath = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot (
        "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')))
$brokerPath = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot (
        "artifacts\bin\WinWidgetBoard.CoreBroker\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe')))

foreach ($requiredPath in @($panelPath, $brokerPath, $PortableDotnetRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required runtime file was not found: $requiredPath"
    }
}

$running = Get-Process -Name 'WinWidgetBoard.LauncherHost' `
    -ErrorAction SilentlyContinue
if ($null -ne $running) {
    $runningText = ($running | ForEach-Object { '{0}#{1}' -f $_.ProcessName, $_.Id }) -join ', '
    throw "Existing WinWidgetBoard processes detected: $runningText"
}

$oldDotnetRoot = $env:DOTNET_ROOT
$oldDotnetRootX64 = $env:DOTNET_ROOT_X64
$oldSessionToken = $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
$oldLocalAppData = $env:LOCALAPPDATA
$testDataRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'WinWidgetBoard-NoteRecoveryTest-' + [Guid]::NewGuid().ToString('N'))
$brokerProcess = $null
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
        [TimeSpan]$Timeout,
        [switch]$Enabled,
        [System.Windows.Automation.AutomationElement]$ScrollContainer,
        [double]$ScrollPercent = 100
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element -and
            -not $element.Current.IsOffscreen -and
            (-not $Enabled -or $element.Current.IsEnabled)) {
            return $element
        }

        if ($null -ne $element -and $null -ne $ScrollContainer) {
            Set-VerticalScrollPercent `
                -Element $ScrollContainer `
                -Percent $ScrollPercent
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element not found: $AutomationId"
}

function Get-ElementText {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
    }

    return $Element.Current.Name
}

function Wait-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Expected,
        [TimeSpan]$Timeout,
        [switch]$Enabled
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId $AutomationId `
        -Timeout $Timeout `
        -Enabled:$Enabled
    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        if ([string]::Equals(
                (Get-ElementText -Element $element),
                $Expected,
                [StringComparison]::Ordinal)) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "UI Automation value did not become the expected committed value: $AutomationId"
}

function Wait-SavedStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout,
        [System.Windows.Automation.AutomationElement]$ScrollContainer
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId 'NoteEditorStatusText' `
        -Timeout $Timeout `
        -ScrollContainer $ScrollContainer
    $deadline = [DateTime]::UtcNow + $Timeout
    $savedMarker = [string]::Concat(
        [char]0x5DF2,
        [char]0x4FDD,
        [char]0x5B58)
    do {
        $text = Get-ElementText -Element $element
        if ($text -match ('Saved|' + [regex]::Escape($savedMarker))) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Note editor did not report a completed save. Last status: $text"
}

function Wait-SaveFailedStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout,
        [System.Windows.Automation.AutomationElement]$ScrollContainer
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId 'NoteEditorStatusText' `
        -Timeout $Timeout `
        -ScrollContainer $ScrollContainer
    $deadline = [DateTime]::UtcNow + $Timeout
    $failedMarker = -join @(
        [char]0x4FDD,
        [char]0x5B58,
        [char]0x5931,
        [char]0x8D25)
    do {
        $text = Get-ElementText -Element $element
        if ($text -match ('Save failed|' + [regex]::Escape($failedMarker))) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Note editor did not report a failed save. Last status: $text"
}

function Set-TextValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        throw "ValuePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

function Invoke-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "InvokePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Set-VerticalScrollPercent {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [double]$Percent
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern,
            [ref]$pattern)) {
        throw "ScrollPattern unavailable: $($Element.Current.Name)"
    }

    $scroll = [System.Windows.Automation.ScrollPattern]$pattern
    if ($scroll.Current.VerticallyScrollable) {
        $scroll.SetScrollPercent(
            [System.Windows.Automation.ScrollPattern]::NoScroll,
            $Percent)
        Start-Sleep -Milliseconds 300
    }
}

function Focus-PanelWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Window
    )

    $windowHandle = [IntPtr]$Window.Current.NativeWindowHandle
    $zeroHandle = [IntPtr]::Zero
    if ($windowHandle -eq $zeroHandle) {
        throw 'WorkspacePanel returned an invalid native window handle.'
    }

    [void][WinWidgetBoardNoteRecoveryInput]::ShowWindow($windowHandle, 5)
    [void][WinWidgetBoardNoteRecoveryInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardNoteRecoveryInput]::SetActiveWindow($windowHandle)
    $foregroundSet = [WinWidgetBoardNoteRecoveryInput]::SetForegroundWindow($windowHandle)
    $focusSet = $false
    try {
        $Window.SetFocus()
        $focusSet = $true
    }
    catch {
    }

    # ValuePattern remains usable for a visible UIA target even when the
    # foreground lock rejects cross-process keyboard focus.
}

function Start-TestBroker {
    $process = Start-Process `
        -FilePath $brokerPath `
        -ArgumentList @(
            '--session-token',
            $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN,
            '--acceptance-test',
            '--test-instance-id',
            [Guid]::NewGuid().ToString('N'),
            '--data-directory',
            $testDataRoot) `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        throw "CoreBroker failed to start. Exit code: $($process.ExitCode)."
    }

    return $process
}

function Start-TestPanel {
    $panelInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $panelInfo.UseShellExecute = $false
    $acceptanceInstanceId = [Guid]::NewGuid().ToString('N')
    $panelInfo.Arguments =
        "--acceptance-test --test-instance-id $acceptanceInstanceId"
    $panelInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $panelInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $panelInfo.EnvironmentVariables['LOCALAPPDATA'] = $env:LOCALAPPDATA
    $panelInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
        $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $process = [Diagnostics.Process]::Start($panelInfo)
    $window = Get-PanelWindow -ProcessId $process.Id -Timeout ([TimeSpan]::FromSeconds(15))
    Focus-PanelWindow -Window $window
    return [pscustomobject]@{
        Process = $process
        Window = $window
    }
}

function Stop-TestProcess {
    param(
        [Diagnostics.Process]$Process
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
    }
    catch [InvalidOperationException] {
    }
}

try {
    New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
    $env:DOTNET_ROOT = $PortableDotnetRoot
    $env:DOTNET_ROOT_X64 = $PortableDotnetRoot
    $env:LOCALAPPDATA = $testDataRoot

    $sessionRandomBytes = New-Object byte[] 32
    $sessionRandom = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $sessionRandom.GetBytes($sessionRandomBytes)
    }
    finally {
        $sessionRandom.Dispose()
    }
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = [Convert]::ToBase64String(
        $sessionRandomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    $brokerProcess = Start-TestBroker
    $firstPanel = Start-TestPanel
    $panelProcess = $firstPanel.Process
    $firstWindow = $firstPanel.Window

    $titleBox = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'NoteTitleBox' `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled
    $bodyBox = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'NoteBodyBox' `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled
    $committedTitle = 'Committed after restart'
    $committedBody = 'recovery-sentinel-' + [Guid]::NewGuid().ToString('N')

    Set-TextValue -Element $titleBox -Value $committedTitle
    Set-TextValue -Element $bodyBox -Value $committedBody
    [void](Wait-ElementValue `
        -Root $firstWindow `
        -AutomationId 'NoteTitleBox' `
        -Expected $committedTitle `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled)
    [void](Wait-ElementValue `
        -Root $firstWindow `
        -AutomationId 'NoteBodyBox' `
        -Expected $committedBody `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled)
    $noteScroll = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'NotesCardScrollViewer' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SavedStatus `
        -Root $firstWindow `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -ScrollContainer $noteScroll)

    $redoBody = 'redo-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    Set-TextValue -Element $bodyBox -Value $redoBody
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    $undoButton = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'UndoNoteButton' `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled `
        -ScrollContainer $noteScroll
    Invoke-Element -Element $undoButton
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    [void](Wait-ElementValue `
        -Root $firstWindow `
        -AutomationId 'NoteBodyBox' `
        -Expected $committedBody `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled)

    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    $redoButton = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'RedoNoteButton' `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled `
        -ScrollContainer $noteScroll
    Invoke-Element -Element $redoButton
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    [void](Wait-ElementValue `
        -Root $firstWindow `
        -AutomationId 'NoteBodyBox' `
        -Expected $redoBody `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled)
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SavedStatus `
        -Root $firstWindow `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -ScrollContainer $noteScroll)

    Stop-TestProcess -Process $brokerProcess
    $brokerProcess = $null
    $retriedBody = 'retry-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    Set-TextValue -Element $bodyBox -Value $retriedBody
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SaveFailedStatus `
        -Root $firstWindow `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -ScrollContainer $noteScroll)
    $retryButton = Wait-VisibleElementByAutomationId `
        -Root $firstWindow `
        -AutomationId 'RetryNoteSaveButton' `
        -Timeout ([TimeSpan]::FromSeconds(5)) `
        -Enabled `
        -ScrollContainer $noteScroll

    $brokerProcess = Start-TestBroker
    Invoke-Element -Element $retryButton
    [void](Wait-SavedStatus `
        -Root $firstWindow `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -ScrollContainer $noteScroll)

    Stop-TestProcess -Process $panelProcess
    $panelProcess = $null
    Stop-TestProcess -Process $brokerProcess
    $brokerProcess = $null
    Start-Sleep -Milliseconds 250

    $brokerProcess = Start-TestBroker
    $secondPanel = Start-TestPanel
    $panelProcess = $secondPanel.Process
    $secondWindow = $secondPanel.Window

    [void](Wait-ElementValue `
        -Root $secondWindow `
        -AutomationId 'NoteTitleBox' `
        -Expected $committedTitle `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled)
    [void](Wait-ElementValue `
        -Root $secondWindow `
        -AutomationId 'NoteBodyBox' `
        -Expected $retriedBody `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled)

    Write-Output 'REAL-NOTE-RECOVERY-PASS undo+redo+retry+committed-state-reloaded'
}
finally {
    Stop-TestProcess -Process $panelProcess
    Stop-TestProcess -Process $brokerProcess

    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = $oldSessionToken
    $env:LOCALAPPDATA = $oldLocalAppData
    if (Test-Path -LiteralPath $testDataRoot) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force
    }
}
