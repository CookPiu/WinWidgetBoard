[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') -DisableNameChecking -Force

$panelProcess = $null
$failure = $null
# CoreBrokerSessionFactory reads this exact environment variable. The panel-only
# acceptance process must not inherit it from a developer or production shell.
$brokerSessionEnvironmentVariables = @(
    'WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'
)
$brokerLaunchArguments = @()
$brokerEnvironmentGuardPassed = $false
$unavailablePattern = (
    (-join [char[]](0x6682, 0x4e0d, 0x53ef, 0x7528)) + '|Unavailable')
$startTimerName = -join [char[]](0x542f, 0x52a8, 0x8ba1, 0x65f6, 0x5668)
$openTodoName = -join [char[]](0x6253, 0x5f00, 0x5f85, 0x529e)
$openCalendarName = -join [char[]](0x6253, 0x5f00, 0x65e5, 0x5386)

function Wait-ForStatusAnchor {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$CardLabel,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastName = $null
    do {
        $element = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId $AutomationId
        if ($null -ne $element) {
            $lastName = $element.Current.Name
            if (-not [string]::IsNullOrWhiteSpace($lastName) -and
                $lastName -match $unavailablePattern -and
                -not $element.Current.IsOffscreen) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Status anchor '$AutomationId' for $CardLabel was not readable as Unavailable. Last name: '$lastName'."
}

function Assert-PlaceholderActionGuard {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$CardLabel,
        [string[]]$Names
    )

    $element = Get-ElementByNames -Root $Root -Names $Names -Optional
    if ($null -eq $element) {
        Write-Output (
            "ACTION-GUARD-PASS card=$CardLabel present=false names=$($Names -join '|')")
        return
    }

    $isOffscreen = $element.Current.IsOffscreen
    $isEnabled = $element.Current.IsEnabled
    if (-not $isOffscreen -and $isEnabled) {
        throw "Placeholder action for $CardLabel is visible and enabled: '$($element.Current.Name)'."
    }

    Write-Output (
        "ACTION-GUARD-PASS card=$CardLabel present=true offscreen=$isOffscreen enabled=$isEnabled")
}

try {
    $panelPath = Join-Path $PSScriptRoot (
        "..\artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')
    $panelPath = [IO.Path]::GetFullPath($panelPath)
    if (-not (Test-Path -LiteralPath $panelPath -PathType Leaf)) {
        throw "WorkspacePanel executable was not found: $panelPath"
    }

    $acceptanceInstanceId = [Guid]::NewGuid().ToString('N')
    $startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments =
        "--acceptance-test --test-instance-id $acceptanceInstanceId"
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot

    foreach ($variableName in $brokerSessionEnvironmentVariables) {
        [void]$startInfo.EnvironmentVariables.Remove($variableName)
    }
    $remainingBrokerEnvironmentVariables = @(
        foreach ($variableName in $brokerSessionEnvironmentVariables) {
            if ($startInfo.EnvironmentVariables.ContainsKey($variableName)) {
                $variableName
            }
        }
    )
    if ($remainingBrokerEnvironmentVariables.Count -ne 0) {
        throw (
            'Panel-only launch retained a CoreBroker session environment variable: ' +
            ($remainingBrokerEnvironmentVariables -join ', '))
    }
    if ($brokerLaunchArguments.Count -ne 0) {
        throw 'Panel-only launch unexpectedly contains CoreBroker arguments.'
    }
    $brokerEnvironmentGuardPassed = $true
    Write-Output (
        'BROKER-ENV-GUARD-PASS cleared=' +
        ($brokerSessionEnvironmentVariables -join ',') +
        ' brokerArgs=none panelOnly=true')

    $panelProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $panelProcess) {
        throw 'WorkspacePanel process could not be started.'
    }

    Start-Sleep -Milliseconds 250
    if ($panelProcess.HasExited) {
        throw "WorkspacePanel exited before UI Automation with code $($panelProcess.ExitCode)."
    }

    if (-not $brokerEnvironmentGuardPassed -or $brokerLaunchArguments.Count -ne 0) {
        throw 'broker=false may only be reported after the environment and argument guards pass.'
    }

    $window = Get-PanelWindow `
        -ProcessId $panelProcess.Id `
        -Timeout ([TimeSpan]::FromSeconds(15))
    Write-Output (
        "PANEL-START-PASS configuration=$Configuration platform=$Platform " +
        "pid=$($panelProcess.Id) acceptanceInstanceId=$acceptanceInstanceId " +
        "broker=false dataIsolation=panel-only")

    $statusAnchors = [ordered]@{
        Notes = 'NotesCardRuntimeStatus'
        Weather = 'WeatherCardRuntimeStatus'
        Timer = 'TimerCardRuntimeStatus'
        Todo = 'TodoCardRuntimeStatus'
        Calendar = 'CalendarCardRuntimeStatus'
    }
    foreach ($entry in $statusAnchors.GetEnumerator()) {
        $anchor = Wait-ForStatusAnchor `
            -Root $window `
            -AutomationId $entry.Value `
            -CardLabel $entry.Key `
            -Timeout ([TimeSpan]::FromSeconds(10))
        Write-Output (
            "STATUS-ANCHOR-PASS card=$($entry.Key) automationId=$($entry.Value) " +
            "name=$($anchor.Current.Name) offscreen=$($anchor.Current.IsOffscreen)")
    }

    Assert-PlaceholderActionGuard `
        -Root $window `
        -CardLabel 'Timer' `
        -Names @($startTimerName, 'Start timer')
    Assert-PlaceholderActionGuard `
        -Root $window `
        -CardLabel 'Todo' `
        -Names @($openTodoName, 'Open to-do')
    Assert-PlaceholderActionGuard `
        -Root $window `
        -CardLabel 'Calendar' `
        -Names @($openCalendarName, 'Open calendar')

    $closeButton = Get-ElementByAutomationId `
        -Root $window `
        -AutomationId 'ClosePanelButton'
    if ($null -eq $closeButton -or -not $closeButton.Current.IsEnabled) {
        throw 'ClosePanelButton was not available for normal window exit.'
    }
    Invoke-Element -Element $closeButton
    $exitDeadline = [DateTime]::UtcNow + [TimeSpan]::FromSeconds(10)
    while (-not $panelProcess.HasExited -and
        [DateTime]::UtcNow -lt $exitDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not $panelProcess.HasExited) {
        throw 'WorkspacePanel did not exit after invoking ClosePanelButton.'
    }
    if ($panelProcess.ExitCode -ne 0) {
        throw "WorkspacePanel exited after ClosePanelButton with code $($panelProcess.ExitCode)."
    }

    Write-Output (
        "WINDOW-EXIT-PASS pid=$($panelProcess.Id) exitCode=$($panelProcess.ExitCode)")
    Write-Output (
        'REAL-CARD-STATUS-PASS unavailable-anchors=5 ' +
        'placeholder-actions=guarded panel-exit=normal broker=false')
}
catch {
    $failure = $_
}
finally {
    Stop-OwnedProcess -Process $panelProcess
}

if ($null -ne $failure) {
    Write-Error $failure
    exit 1
}

exit 0
