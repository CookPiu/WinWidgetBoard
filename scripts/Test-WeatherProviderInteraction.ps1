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

function Wait-WeatherPayload {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastStatus = $null
    $lastHelp = $null
    $lastData = $null
    do {
        $statusAnchor = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId 'WeatherCardRuntimeStatus'
        if ($null -ne $statusAnchor) {
            $lastStatus = $statusAnchor.Current.Name
            $lastHelp = $statusAnchor.Current.HelpText
        }

        if ($lastHelp -match '°C') {
            return [pscustomobject]@{
                Status = $lastStatus
                Data = $lastHelp
            }
        }

        $temperatureElement = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId 'WeatherTemperatureText'
        if ($null -ne $temperatureElement -and
            -not $temperatureElement.Current.IsOffscreen) {
            $lastData = $temperatureElement
        }
        if ($null -ne $lastData -and
            ($lastData.Current.Name -match '°C' -or
             $lastData.Current.HelpText -match '°C')) {
            return [pscustomobject]@{
                Status = $lastStatus
                Data = if ($lastData.Current.Name) {
                    $lastData.Current.Name
                }
                else {
                    $lastData.Current.HelpText
                }
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    Get-Descendants -Root $Root |
        Where-Object {
            $_.Current.AutomationId -match 'Weather' -or
            $_.Current.Name -match 'Weather|天气|°C'
        } |
        Select-Object -First 20 |
        ForEach-Object {
            Write-Output (
                "WEATHER-UIA id='$($_.Current.AutomationId)' " +
                "name='$($_.Current.Name)' help='$($_.Current.HelpText)' " +
                "offscreen=$($_.Current.IsOffscreen)")
        }

    throw (
        "Weather payload did not reach the UI. Last status='$lastStatus', " +
        "last status help='$lastHelp', " +
        "last temperature name='$($lastData.Current.Name)', " +
        "help='$($lastData.Current.HelpText)'.")
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
        "WinWidgetBoard-Weather-$testInstanceId")
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

    $brokerStartInfo = [Diagnostics.ProcessStartInfo]::new($brokerPath)
    $brokerStartInfo.UseShellExecute = $false
    $brokerStartInfo.CreateNoWindow = $true
    $brokerStartInfo.Arguments = (
        "--session-token $sessionToken" +
        " --acceptance-test --test-instance-id $testInstanceId" +
        " --data-directory `"$testDataRoot`"")
    $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $brokerStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
    $brokerProcess = [Diagnostics.Process]::Start($brokerStartInfo)
    if ($null -eq $brokerProcess) {
        throw 'CoreBroker process could not be started.'
    }
    Start-Sleep -Milliseconds 500
    if ($brokerProcess.HasExited) {
        throw "CoreBroker exited before panel start with code $($brokerProcess.ExitCode)."
    }
    Write-Output "BROKER-START-PASS pid=$($brokerProcess.Id) dataIsolation=$testDataRoot"

    $panelStartInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $panelStartInfo.UseShellExecute = $false
    $panelStartInfo.CreateNoWindow = $true
    $panelStartInfo.Arguments =
        "--acceptance-test --test-instance-id $testInstanceId"
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $panelStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $panelStartInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
    $panelProcess = [Diagnostics.Process]::Start($panelStartInfo)
    if ($null -eq $panelProcess) {
        throw 'WorkspacePanel process could not be started.'
    }
    $window = Get-PanelWindow -ProcessId $panelProcess.Id -Timeout ([TimeSpan]::FromSeconds(15))
    Write-Output "PANEL-START-PASS pid=$($panelProcess.Id) brokerPid=$($brokerProcess.Id)"

    $weather = Wait-WeatherPayload `
        -Root $window `
        -Timeout ([TimeSpan]::FromSeconds(25))
    Write-Output (
        "REAL-WEATHER-PASS status=`"$($weather.Status)`" " +
        "temperature=`"$($weather.Data)`" provider=open-meteo " +
        'ipc=cards.snapshot scheduler=visible')

    $closeButton = Get-ElementByAutomationId -Root $window -AutomationId 'ClosePanelButton'
    if ($null -eq $closeButton -or -not $closeButton.Current.IsEnabled) {
        throw 'ClosePanelButton was not available for normal window exit.'
    }
    Invoke-Element -Element $closeButton
    if (-not $panelProcess.WaitForExit(10000)) {
        throw 'WorkspacePanel did not exit after ClosePanelButton invocation.'
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
