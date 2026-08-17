[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') -DisableNameChecking -Force

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

    $brokerProcess = Start-TestBroker `
        -BrokerPath $brokerPath `
        -TestDataRoot $testDataRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $firstPanel = Start-TestPanel `
        -PanelPath $panelPath `
        -PortableDotnetRoot $PortableDotnetRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
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

    $brokerProcess = Start-TestBroker `
        -BrokerPath $brokerPath `
        -TestDataRoot $testDataRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
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

    $brokerProcess = Start-TestBroker `
        -BrokerPath $brokerPath `
        -TestDataRoot $testDataRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $secondPanel = Start-TestPanel `
        -PanelPath $panelPath `
        -PortableDotnetRoot $PortableDotnetRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
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
