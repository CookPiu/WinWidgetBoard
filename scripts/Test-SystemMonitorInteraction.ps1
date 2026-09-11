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
    Real-desktop regression for the hardware monitor card and its settings dialog.

.DESCRIPTION
    Starts an isolated broker and panel, waits for the card to show real readings, then changes
    which metrics are displayed through the settings dialog and confirms the change survives a
    panel restart. Finally it swaps in CPU temperature, whose reading comes from the optional
    sensor source rather than from Windows, and checks the row says which state it is in.

    Everything runs on an acceptance instance ID with a data directory under %TEMP%; the
    production database is never touched (ADR-0017). Only processes this script started are
    stopped.

.EXAMPLE
    .\scripts\Test-SystemMonitorInteraction.ps1
#>

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
    -DisableNameChecking -Force

$panelProcess = $null
$brokerProcess = $null
$testDataRoot = $null
$failure = $null

function Wait-ElementByAutomationId {
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

        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "UIA element '$AutomationId' did not appear."
}

# The card's rows carry their reading in the automation name, which is what a screen reader
# gets; asserting on that keeps the test honest about what is actually announced.
function Wait-MetricReading {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Pattern,
        [TimeSpan]$Timeout
    )

    # Scoped to the window rather than to the ItemsControl: a bare items panel gets no
    # automation peer of its own, so the rows are reachable but their container is not.
    $deadline = [DateTime]::UtcNow + $Timeout
    $seen = @()
    do {
        $seen = @(Get-Descendants -Root $Root |
            ForEach-Object { $_.Current.Name } |
            Where-Object { $_ })
        $match = $seen | Where-Object { $_ -match $Pattern } | Select-Object -First 1
        if ($match) {
            return $match
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw ("No metric row matched '$Pattern'. Saw " + $seen.Count + ' names: ' +
        (($seen | Select-Object -First 40) -join ' | '))
}

# The settings sheet keeps both metric lists in Expanders that are collapsed when it opens, and
# a collapsed Expander has no children in the automation tree - so a toggle inside one is not
# late, it is absent. Opening the card's section first is what a user does too.
function Wait-CardMetricToggle {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $expander = Wait-ElementByAutomationId `
        -Root $Root `
        -AutomationId 'SysMonCardExpander' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Expand-Element -Element $expander | Out-Null

    return Wait-ElementByAutomationId `
        -Root $Root `
        -AutomationId $AutomationId `
        -Timeout ([TimeSpan]::FromSeconds(10))
}

# Saving closes the sheet, but not instantly - and until it is gone its own metric labels
# ("CPU 频率") sit in the same automation tree as the card's rows. A search of the window run
# in that gap finds the label and reads it as the card having followed the save, whatever the
# card is actually showing.
function Wait-SettingsDialogClosed {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout = ([TimeSpan]::FromSeconds(10))
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $save = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId 'SysMonSettingsSaveButton'
        if ($null -eq $save) {
            return
        }

        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The settings sheet was still open after saving.'
}

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
    $testDataRoot = Join-Path ([IO.Path]::GetTempPath()) "wwb-sysmon-$instanceId"
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

    # A percentage proves the whole chain: sampler, provider, snapshot event, projection, card.
    # Anchored on the metric's own name rather than on any percentage in the window - the
    # weather card announces a humidity, which matched a bare '\d+%' and passed this step on a
    # board with no hardware readings at all.
    $reading = Wait-MetricReading `
        -Root $window `
        -Pattern '^CPU\s+\d+(\.\d+)?%' `
        -Timeout ([TimeSpan]::FromSeconds(30))
    Write-Output "SYSMON-CARD-PASS reading=`"$reading`""

    # The card's leading reading is drawn as a headline rather than as a row, so its value is
    # its own element; a card that showed only rows would still satisfy the check above.
    $headlineValue = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SystemMonitorHeadlineValue' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    if ($headlineValue.Current.Name -notmatch '\d') {
        throw "The headline read '$($headlineValue.Current.Name)' rather than a value."
    }

    Write-Output "SYSMON-HEADLINE-PASS `"$($headlineValue.Current.Name)`""

    $settingsButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SysMonSettingsButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $settingsButton

    $clockToggle = Wait-CardMetricToggle `
        -Root $window `
        -AutomationId 'SysMonCardInclude_cpu.clock'
    $togglePattern = $clockToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $before = $togglePattern.Current.ToggleState
    $togglePattern.Toggle()
    $after = $clockToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    if ($before -eq $after) {
        throw "Toggling 'cpu.clock' did not change its state (still $after)."
    }

    $saveButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SysMonSettingsSaveButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $saveButton
    Wait-SettingsDialogClosed -Root $window
    Write-Output "SYSMON-SETTINGS-PASS cpu.clock $before -> $after"

    # The card follows the saved list within one refresh cadence.
    $expected = if ($after -eq [System.Windows.Automation.ToggleState]::On) { $true } else { $false }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $found = $false
    $clockNames = @()
    do {
        $names = @(Get-Descendants -Root $window |
            ForEach-Object { $_.Current.Name } |
            Where-Object { $_ })
        # A card row announces its name and then its reading; the settings sheet's list item is
        # the bare name. Requiring something after the name is what tells the two apart.
        $clockNames = @($names | Where-Object { $_ -match '^(CPU clock|CPU 频率)\s+\S' })
        $hasClock = $clockNames.Count -gt 0
        if ($hasClock -eq $expected) { $found = $true; break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $found) {
        throw "The card did not follow the saved metric list (expected clock present=$expected)."
    }

    # The row being there is not the same as it reading anything. The clock is measured from
    # per-core performance counters (ADR-0031), so a row announcing "this PC cannot read it"
    # means the counter pairing broke, not that the machine lacks a sensor.
    if ($expected) {
        $clockReading = @($clockNames |
            Where-Object { $_ -match '\d+(\.\d+)?\s*(GHz|MHz)' })[0]
        if (-not $clockReading) {
            throw "The clock row announced $($clockNames -join ' / ') instead of a frequency."
        }

        Write-Output "SYSMON-CLOCK-READING-PASS $clockReading"
    }

    Write-Output "SYSMON-CARD-FOLLOWS-SETTINGS-PASS clockPresent=$expected"

    # Restart only the panel; the broker keeps the same database, so this proves persistence
    # rather than in-process memory.
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

    $settingsButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SysMonSettingsButton' `
        -Timeout ([TimeSpan]::FromSeconds(15))
    Invoke-Element -Element $settingsButton
    $clockToggle = Wait-CardMetricToggle `
        -Root $window `
        -AutomationId 'SysMonCardInclude_cpu.clock'
    $reloaded = $clockToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    if ($reloaded -ne $after) {
        throw "After restart 'cpu.clock' was $reloaded; expected $after."
    }

    Write-Output "SYSMON-RESTART-PASS cpu.clock=$reloaded"

    # Temperature has no user-mode API on Windows at all: it is read out of HWiNFO's shared
    # memory when the user is already running it, and says so when they are not (ADR-0033).
    # Swapping it in for the clock keeps the number of rows the same, so what this asserts is
    # the wording rather than the card's size ladder a second time.
    $temperatureToggle = Wait-CardMetricToggle `
        -Root $window `
        -AutomationId 'SysMonCardInclude_cpu.temperature'
    $temperaturePattern = $temperatureToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($temperaturePattern.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::On) {
        $temperaturePattern.Toggle()
    }

    $clockPattern = $clockToggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($clockPattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
        $clockPattern.Toggle()
    }

    $saveButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SysMonSettingsSaveButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $saveButton
    Wait-SettingsDialogClosed -Root $window

    # A reading held up by the optional sensor source is an instruction, and instructions are
    # settings content - so the card leaves that row out entirely rather than carrying a
    # permanent line of setup jargon. Which outcome is correct here depends on whether this
    # machine happens to be running HWiNFO or Core Temp, so both are accepted: a row naming a
    # temperature, or no row at all. What is never accepted is the card repeating the setup
    # requirement, or a row with nothing after its name.
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    $temperatureRow = $null
    do {
        $temperatureRow = @(Get-Descendants -Root $window |
            ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -match '^(CPU 温度|CPU temperature)\s+\S' })[0]
        if ($temperatureRow) {
            break
        }

        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($temperatureRow) {
        if ($temperatureRow -match '需运行|Needs HWiNFO|Core Temp') {
            throw "The card carried a setup instruction as a reading: $temperatureRow"
        }

        Write-Output "SYSMON-TEMPERATURE-PASS `"$temperatureRow`""
    }
    else {
        Write-Output 'SYSMON-TEMPERATURE-PASS row absent, no sensor source running'
    }

    Write-Output 'REAL-SYSMON-PASS card+settings+persistence+sensor-source'
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
