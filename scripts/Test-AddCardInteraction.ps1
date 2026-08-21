[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',
    [switch]$RetainTestData
)

<#
.SYNOPSIS
    Real-desktop regression for adding a card back to the board from layout edit mode.

.DESCRIPTION
    Starts an isolated broker and panel, removes the timer card, adds it again through the
    edit-mode picker, and confirms the addition survives finishing the edit and restarting the
    panel. Also asserts the picker button is invisible outside edit mode - it is a layout
    action, not permanent toolbar chrome.

    Everything runs on an acceptance instance ID with a data directory under %TEMP%; the
    production database is never touched (ADR-0017). Only processes this script started are
    stopped.

.EXAMPLE
    .\scripts\Test-AddCardInteraction.ps1
#>

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
    -DisableNameChecking -Force

$panelProcess = $null
$brokerProcess = $null
$testDataRoot = $null
$failure = $null

$timeout = [TimeSpan]::FromSeconds(15)

function Start-Stack {
    param(
        [string]$BrokerPath,
        [string]$PanelPath,
        [string]$InstanceId,
        [string]$DataRoot,
        [string]$SessionToken,
        [string]$DotnetRoot,
        [switch]$BrokerAlreadyRunning
    )

    $startedBroker = $null
    if (-not $BrokerAlreadyRunning) {
        $brokerStartInfo = [Diagnostics.ProcessStartInfo]::new($BrokerPath)
        $brokerStartInfo.UseShellExecute = $false
        $brokerStartInfo.CreateNoWindow = $true
        $brokerStartInfo.Arguments = (
            "--session-token $SessionToken" +
            " --acceptance-test --test-instance-id $InstanceId" +
            " --data-directory `"$DataRoot`"")
        $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $DotnetRoot
        $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $DotnetRoot
        $brokerStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
            $SessionToken
        $startedBroker = [Diagnostics.Process]::Start($brokerStartInfo)
        Start-Sleep -Milliseconds 700
        if ($startedBroker.HasExited) {
            throw "CoreBroker exited before panel start with code $($startedBroker.ExitCode)."
        }
    }

    $panelStartInfo = [Diagnostics.ProcessStartInfo]::new($PanelPath)
    $panelStartInfo.UseShellExecute = $false
    $panelStartInfo.CreateNoWindow = $true
    $panelStartInfo.Arguments = "--acceptance-test --test-instance-id $InstanceId"
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $DotnetRoot
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $DotnetRoot
    $panelStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
        $SessionToken
    $startedPanel = [Diagnostics.Process]::Start($panelStartInfo)
    if ($null -eq $startedPanel) {
        if ($null -ne $startedBroker) { Stop-OwnedProcess -Process $startedBroker }
        throw 'WorkspacePanel process could not be started.'
    }

    return [pscustomobject]@{
        Broker = $startedBroker
        Panel = $startedPanel
        Window = Get-PanelWindow -ProcessId $startedPanel.Id `
            -Timeout ([TimeSpan]::FromSeconds(20))
    }
}

# The edit-mode toolbar keeps its buttons in the tree while they are collapsed, so presence
# alone says nothing; only the offscreen flag distinguishes the two states.
function Enter-EditMode {
    param([System.Windows.Automation.AutomationElement]$Window)

    Assert-ElementHiddenByAutomationId `
        -Root $Window `
        -AutomationId 'AddCardButton' `
        -Because 'must stay hidden until layout edit mode begins'

    Invoke-Element -Element (Wait-VisibleElementByAutomationId `
        -Root $Window `
        -AutomationId 'EditLayoutButton' `
        -Timeout $timeout)
    Start-Sleep -Milliseconds 500
}

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $panelPath = Resolve-ManagedOutput `
        -RepositoryRoot $repoRoot `
        -ProjectName 'WinWidgetBoard.WorkspacePanel' `
        -ExecutableName 'WinWidgetBoard.WorkspacePanel.exe' `
        -Configuration $Configuration `
        -Platform $Platform
    $brokerPath = Resolve-ManagedOutput `
        -RepositoryRoot $repoRoot `
        -ProjectName 'WinWidgetBoard.CoreBroker' `
        -ExecutableName 'WinWidgetBoard.CoreBroker.exe' `
        -Configuration $Configuration `
        -Platform $Platform

    $running = Get-Process -Name @(
        'WinWidgetBoard.CoreBroker',
        'WinWidgetBoard.WorkspacePanel',
        'WinWidgetBoard.LauncherHost') -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $runningText = ($running | ForEach-Object {
                '{0}#{1}' -f $_.ProcessName, $_.Id
            }) -join ', '
        throw "Existing WinWidgetBoard processes detected: $runningText"
    }

    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $sessionToken = [BitConverter]::ToString($bytes).Replace('-', '')
    $instanceId = [Guid]::NewGuid().ToString('N')
    $testDataRoot = Join-Path ([IO.Path]::GetTempPath()) "wwb-addcard-$instanceId"
    New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null

    $stack = Start-Stack `
        -BrokerPath $brokerPath `
        -PanelPath $panelPath `
        -InstanceId $instanceId `
        -DataRoot $testDataRoot `
        -SessionToken $sessionToken `
        -DotnetRoot $PortableDotnetRoot
    $brokerProcess = $stack.Broker
    $panelProcess = $stack.Panel
    $window = $stack.Window
    Focus-PanelWindow -Window $window

    Enter-EditMode -Window $window
    [void](Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'AddCardButton' `
        -Timeout $timeout)
    Write-Output 'ADDCARD-BUTTON-PASS hidden-then-visible'

    # Every built-in card ships on the board, so the picker has nothing to offer yet and the
    # panel must say so instead of opening an empty dialog.
    Invoke-Element -Element (Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'AddCardButton' `
        -Timeout $timeout)
    Start-Sleep -Milliseconds 600
    Assert-ElementHiddenByAutomationId `
        -Root $window `
        -AutomationId 'AddCardList' `
        -Because 'must not open a picker when every card is already placed'
    Write-Output 'ADDCARD-EMPTY-PASS full-board-offers-nothing'

    Invoke-Element -Element (Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardRemoveButton' `
        -Timeout $timeout)
    Start-Sleep -Milliseconds 500
    Assert-ElementHiddenByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardDragHandle' `
        -Because 'must be gone once its card is removed'

    Invoke-Element -Element (Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'AddCardButton' `
        -Timeout $timeout)
    $timerOption = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'AddCardInclude_demo.timer' `
        -Timeout $timeout
    $state = Invoke-ElementToggle -Element $timerOption
    if ($state -ne [System.Windows.Automation.ToggleState]::On) {
        throw "The timer option did not select; it reports $state."
    }

    $confirm = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'AddCardConfirmButton' `
        -Timeout $timeout `
        -Enabled
    Invoke-Element -Element $confirm
    Start-Sleep -Milliseconds 800

    [void](Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardDragHandle' `
        -Timeout $timeout)
    Write-Output 'ADDCARD-ROUNDTRIP-PASS remove-then-add'

    # Finishing the edit is what writes the layout; the restart below is what proves it.
    Invoke-Element -Element (Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'EditLayoutButton' `
        -Timeout $timeout)
    Start-Sleep -Seconds 2

    Stop-OwnedProcess -Process $panelProcess
    $panelProcess = $null
    Start-Sleep -Seconds 2

    $restarted = Start-Stack `
        -BrokerPath $brokerPath `
        -PanelPath $panelPath `
        -InstanceId $instanceId `
        -DataRoot $testDataRoot `
        -SessionToken $sessionToken `
        -DotnetRoot $PortableDotnetRoot `
        -BrokerAlreadyRunning
    $panelProcess = $restarted.Panel
    $window = $restarted.Window
    Focus-PanelWindow -Window $window

    Enter-EditMode -Window $window
    [void](Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'TimerCardDragHandle' `
        -Timeout $timeout)
    Write-Output 'ADDCARD-RESTART-PASS added-card-persisted'
    Write-Output 'REAL-ADDCARD-PASS disclosure+picker+roundtrip+persistence'
}
catch {
    $failure = $_
}
finally {
    foreach ($owned in @($panelProcess, $brokerProcess)) {
        if ($null -ne $owned) {
            Stop-OwnedProcess -Process $owned
        }
    }

    if (-not $RetainTestData -and
        $null -ne $testDataRoot -and
        $testDataRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($null -ne $failure) {
    throw $failure
}
