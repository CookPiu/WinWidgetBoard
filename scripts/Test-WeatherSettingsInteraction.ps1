[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',
    [switch]$RetainTestData
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$panelProcess = $null
$brokerProcess = $null
$testDataRoot = $null
$failure = $null

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
        if ($null -ne $window) {
            return $window
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "WorkspacePanel window did not appear within $($Timeout.TotalSeconds) seconds."
}

function Get-Descendants {
    param(
        [System.Windows.Automation.AutomationElement]$Root
    )

    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
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

function Invoke-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "Element '$($Element.Current.Name)' does not support InvokePattern."
    }

    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Set-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        throw "Element '$($Element.Current.Name)' does not support ValuePattern."
    }

    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

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

function Wait-ElementName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Pattern,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastName = ''
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            $lastName = $element.Current.Name
            if ($lastName -match $Pattern) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "UIA element '$AutomationId' did not reach '$Pattern'; last name='$lastName'."
}

function Stop-OwnedProcess {
    param(
        [Diagnostics.Process]$Process
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            [void]$Process.CloseMainWindow()
            if (-not $Process.WaitForExit(3000)) {
                $Process.Kill()
                [void]$Process.WaitForExit(5000)
            }
        }
    }
    catch [InvalidOperationException] {
    }
}

function Start-Stack {
    param(
        [string]$BrokerPath,
        [string]$PanelPath,
        [string]$InstanceId,
        [string]$DataRoot,
        [string]$SessionToken,
        [string]$DotnetRoot
    )

    $brokerStartInfo = [Diagnostics.ProcessStartInfo]::new($BrokerPath)
    $brokerStartInfo.UseShellExecute = $false
    $brokerStartInfo.CreateNoWindow = $true
    $brokerStartInfo.Arguments = (
        "--session-token $SessionToken" +
        " --acceptance-test --test-instance-id $InstanceId" +
        " --data-directory `"$DataRoot`"")
    $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $DotnetRoot
    $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $DotnetRoot
    $brokerStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $SessionToken
    $startedBroker = [Diagnostics.Process]::Start($brokerStartInfo)
    if ($null -eq $startedBroker) {
        throw 'CoreBroker process could not be started.'
    }
    Start-Sleep -Milliseconds 500
    if ($startedBroker.HasExited) {
        throw "CoreBroker exited before panel start with code $($startedBroker.ExitCode)."
    }

    $panelStartInfo = [Diagnostics.ProcessStartInfo]::new($PanelPath)
    $panelStartInfo.UseShellExecute = $false
    $panelStartInfo.CreateNoWindow = $true
    $panelStartInfo.Arguments =
        "--acceptance-test --test-instance-id $InstanceId"
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $DotnetRoot
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $DotnetRoot
    $panelStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $SessionToken
    $startedPanel = [Diagnostics.Process]::Start($panelStartInfo)
    if ($null -eq $startedPanel) {
        Stop-OwnedProcess -Process $startedBroker
        throw 'WorkspacePanel process could not be started.'
    }

    return [pscustomobject]@{
        Broker = $startedBroker
        Panel = $startedPanel
        Window = Get-PanelWindow -ProcessId $startedPanel.Id -Timeout ([TimeSpan]::FromSeconds(15))
    }
}

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $panelPath = Join-Path $repoRoot (
        "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')
    $brokerPath = Join-Path $repoRoot (
        "artifacts\bin\WinWidgetBoard.CoreBroker\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe')
    $panelPath = [IO.Path]::GetFullPath($panelPath)
    $brokerPath = [IO.Path]::GetFullPath($brokerPath)
    foreach ($requiredPath in @($panelPath, $brokerPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required executable was not found: $requiredPath"
        }
    }

    $running = Get-Process -Name @(
        'WinWidgetBoard.CoreBroker',
        'WinWidgetBoard.WorkspacePanel') -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $runningText = ($running | ForEach-Object {
                '{0}#{1}' -f $_.ProcessName, $_.Id
            }) -join ', '
        throw "Existing WinWidgetBoard processes detected: $runningText"
    }

    $testInstanceId = [Guid]::NewGuid().ToString('N')
    $testDataRoot = Join-Path ([IO.Path]::GetTempPath()) (
        "WinWidgetBoard-WeatherSettings-$testInstanceId")
    [void](New-Item -ItemType Directory -Path $testDataRoot -Force)

    $sessionRandomBytes = New-Object byte[] 32
    $sessionRandom = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $sessionRandom.GetBytes($sessionRandomBytes)
    }
    finally {
        $sessionRandom.Dispose()
    }
    $sessionToken = [Convert]::ToBase64String($sessionRandomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    $stack = Start-Stack `
        -BrokerPath $brokerPath `
        -PanelPath $panelPath `
        -InstanceId $testInstanceId `
        -DataRoot $testDataRoot `
        -SessionToken $sessionToken `
        -DotnetRoot $PortableDotnetRoot
    $brokerProcess = $stack.Broker
    $panelProcess = $stack.Panel
    $window = $stack.Window
    Write-Output "STACK-START-PASS panelPid=$($panelProcess.Id) brokerPid=$($brokerProcess.Id) dataIsolation=$testDataRoot"

    $settingsButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SettingsButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $settingsButton

    # ContentDialog is hosted in a WinUI Popup and may appear as a separate
    # top-level UIA window rather than as a descendant of the panel HWND.
    $automationRoot = [System.Windows.Automation.AutomationElement]::RootElement

    $labelBox = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $latitudeBox = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLatitudeBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $longitudeBox = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLongitudeBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Set-ElementValue -Element $labelBox -Value 'Tokyo'
    Set-ElementValue -Element $latitudeBox -Value '35.6762'
    Set-ElementValue -Element $longitudeBox -Value '139.6503'

    $saveButton = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsSaveButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    if (-not $saveButton.Current.IsEnabled) {
        throw 'Weather settings Save button was disabled after settings load.'
    }
    Invoke-Element -Element $saveButton
    $status = Wait-ElementName `
        -Root $window `
        -AutomationId 'StatusText' `
        -Pattern 'Weather location saved|天气位置已保存' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $location = Wait-ElementName `
        -Root $window `
        -AutomationId 'WeatherLocationText' `
        -Pattern 'Tokyo' `
        -Timeout ([TimeSpan]::FromSeconds(30))
    Write-Output (
        "REAL-WEATHER-SETTINGS-PASS status=`"$($status.Current.Name)`" " +
        "location=`"$($location.Current.Name)`" ipc=weather.settings.save " +
        'sqlite=weather_settings provider=runtime-reload')

    $closeButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'ClosePanelButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $closeButton
    if (-not $panelProcess.WaitForExit(10000)) {
        throw 'WorkspacePanel did not exit after the first settings pass.'
    }
    Stop-OwnedProcess -Process $brokerProcess
    $panelProcess = $null
    $brokerProcess = $null

    $sessionRandomBytes = New-Object byte[] 32
    $sessionRandom = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $sessionRandom.GetBytes($sessionRandomBytes)
    }
    finally {
        $sessionRandom.Dispose()
    }
    $sessionToken = [Convert]::ToBase64String($sessionRandomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $stack = Start-Stack `
        -BrokerPath $brokerPath `
        -PanelPath $panelPath `
        -InstanceId $testInstanceId `
        -DataRoot $testDataRoot `
        -SessionToken $sessionToken `
        -DotnetRoot $PortableDotnetRoot
    $brokerProcess = $stack.Broker
    $panelProcess = $stack.Panel
    $window = $stack.Window
    $reloadedLocation = Wait-ElementName `
        -Root $window `
        -AutomationId 'WeatherLocationText' `
        -Pattern 'Tokyo' `
        -Timeout ([TimeSpan]::FromSeconds(30))
    Write-Output "WEATHER-SETTINGS-RESTART-PASS location=\"$($reloadedLocation.Current.Name)\" persisted=sqlite"

    $closeButton = Wait-ElementByAutomationId -Root $window -AutomationId 'ClosePanelButton' -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $closeButton
    if (-not $panelProcess.WaitForExit(10000)) {
        throw 'WorkspacePanel did not exit after the restart settings pass.'
    }
    if ($panelProcess.ExitCode -ne 0) {
        throw "WorkspacePanel exited with code $($panelProcess.ExitCode)."
    }
    Write-Output "WINDOW-EXIT-PASS exitCode=$($panelProcess.ExitCode)"
}
catch {
    $failure = $_
}
finally {
    Stop-OwnedProcess -Process $panelProcess
    Stop-OwnedProcess -Process $brokerProcess
    if (-not $RetainTestData -and $null -ne $testDataRoot -and
        (Test-Path -LiteralPath $testDataRoot -PathType Container)) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force
    }
}

if ($null -ne $failure) {
    throw $failure
}
