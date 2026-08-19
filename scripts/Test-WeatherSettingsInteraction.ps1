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

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') -DisableNameChecking -Force

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
    # top-level UIA window rather than as a descendant of the panel HWND. Resolve the
    # owning window inside this process instead of searching the desktop root, whose
    # descendant walk covers every other running application.
    $automationRoot = Wait-ProcessWindowContaining `
        -ProcessId $panelProcess.Id `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))

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
    $settingsButton = Wait-ElementByAutomationId `
        -Root $window `
        -AutomationId 'SettingsButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $settingsButton

    $automationRoot = Wait-ProcessWindowContaining `
        -ProcessId $panelProcess.Id `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $reloadedLabel = Wait-ElementValue `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Expected 'Tokyo' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $reloadedLatitude = Wait-ElementValue `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLatitudeBox' `
        -Expected '35.6762' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $reloadedLongitude = Wait-ElementValue `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsLongitudeBox' `
        -Expected '139.6503' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $dialogCloseButton = Get-ElementByNames `
        -Root $automationRoot `
        -Names @('Cancel', '取消')
    Invoke-Element -Element $dialogCloseButton
    Wait-ProcessElementGone `
        -ProcessId $panelProcess.Id `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    # The panel drops a close request while its modal scope is still held, and that scope
    # outlives the dialog element: it is released only after the dialog handler writes its
    # closing status. Wait for that status, otherwise the close click below is swallowed.
    [void](Wait-ElementName `
        -Root $window `
        -AutomationId 'StatusText' `
        -Pattern 'Current weather location loaded|已加载当前天气位置' `
        -Timeout ([TimeSpan]::FromSeconds(10)))
    Write-Output (
        "REAL-WEATHER-SETTINGS-RESTART-PASS settings.get " +
        "label=`"$(Get-ElementText -Element $reloadedLabel)`" " +
        "latitude=`"$(Get-ElementText -Element $reloadedLatitude)`" " +
        "longitude=`"$(Get-ElementText -Element $reloadedLongitude)`" " +
        'card_payload=not-required')

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
