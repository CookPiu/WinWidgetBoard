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

function Assert-PanelHidden {
    param(
        [Diagnostics.Process]$Process,
        [TimeSpan]$Timeout = ([TimeSpan]::FromSeconds(10))
    )

    # The panel is resident: closing hides the window and keeps the process warm so the next
    # open skips process, runtime and XAML startup. See ADR-0025.
    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        if ($Process.HasExited) {
            throw "WorkspacePanel exited instead of staying resident (code $($Process.ExitCode))."
        }

        if (@(Get-ProcessWindows -ProcessId $Process.Id).Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'WorkspacePanel did not hide its window after the close request.'
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
    $searchBox = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsSearchBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))

    Set-ElementValue -Element $searchBox -Value 'Tokyo'

    # Invoke the search button rather than sending a keystroke: SendKeys goes to whatever
    # owns the foreground, which is not reliably this dialog during a test run.
    $searchButton = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsSearchButton' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $searchButton

    # Search leaves the machine, so allow for a slow answer before giving up. Poll for a
    # selectable row rather than for the list element: an empty ListView is not always
    # surfaced to UIA, so waiting on the list alone cannot tell "still loading" from
    # "search failed".
    $firstResult = $null
    $searchDeadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $resultsList = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'WeatherSettingsResultsList'
        if ($null -ne $resultsList) {
            $firstResult = @(Get-Descendants -Root $resultsList) |
                Where-Object {
                    $_.GetSupportedPatterns().Contains(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern)
                } |
                Select-Object -First 1
            if ($null -ne $firstResult) { break }
        }

        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $searchDeadline)

    if ($null -eq $firstResult) {
        $searchStatus = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'WeatherSettingsStatusText'
        $searchStatusText = if ($null -ne $searchStatus) {
            $searchStatus.Current.Name
        }
        else {
            '<no status element>'
        }
        throw ("The weather location search returned no selectable result for 'Tokyo'. " +
            "Status: '$searchStatusText'.")
    }

    $firstResult.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

    # The label stays editable: the searched name is not always what the user wants shown.
    Set-ElementValue -Element $labelBox -Value 'Tokyo'

    $coordinatesBox = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsCoordinatesBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $selectedCoordinates = Get-ElementText -Element $coordinatesBox
    if ([string]::IsNullOrWhiteSpace($selectedCoordinates) -or
        $selectedCoordinates -notmatch '\d') {
        throw "Selecting a search result did not fill the coordinates: '$selectedCoordinates'."
    }
    Write-Output "WEATHER-SEARCH-PASS selected coordinates=`"$selectedCoordinates`""

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
    Assert-PanelHidden -Process $panelProcess
    Stop-OwnedProcess -Process $panelProcess
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
    $reloadedCoordinates = Wait-ElementByAutomationId `
        -Root $automationRoot `
        -AutomationId 'WeatherSettingsCoordinatesBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    # The settings dialog closes rather than cancels: it holds every card's settings and each
    # section saves on its own, so dismissing it does not undo a save that already happened.
    $dialogCloseButton = Get-ElementByNames `
        -Root $automationRoot `
        -Names @('Close', '关闭')
    Invoke-Element -Element $dialogCloseButton
    # Closing the panel immediately after the dialog leaves the tree is the regression for
    # the deferred close request: the modal scope is still held at this point, so the click
    # below must be replayed once that scope is released rather than dropped.
    Wait-ProcessElementGone `
        -ProcessId $panelProcess.Id `
        -AutomationId 'WeatherSettingsLabelBox' `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Write-Output (
        "REAL-WEATHER-SETTINGS-RESTART-PASS settings.get " +
        "label=`"$(Get-ElementText -Element $reloadedLabel)`" " +
        "coordinates=`"$(Get-ElementText -Element $reloadedCoordinates)`" " +
        'card_payload=not-required')

    $closeButton = Wait-ElementByAutomationId -Root $window -AutomationId 'ClosePanelButton' -Timeout ([TimeSpan]::FromSeconds(10))
    Invoke-Element -Element $closeButton
    Assert-PanelHidden -Process $panelProcess
    Write-Output "WINDOW-HIDDEN-PASS pid=$($panelProcess.Id) resident=true"
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
