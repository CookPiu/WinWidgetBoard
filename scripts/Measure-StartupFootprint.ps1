<#
.SYNOPSIS
    Measures taskbar-entry-to-panel latency and idle background footprint on one
    reference Windows 11 x64 machine.

.DESCRIPTION
    Produces the raw numbers docs/08-testing-strategy.md requires before any
    performance claim: device, OS build, configuration, debugger state, duration,
    tool and per-iteration values. It measures three things:

      A. WorkspacePanel process start -> first window -> layout-ready status text.
      B. LauncherHost entry click -> WorkspacePanel window visible (the real
         taskbar entry path, including the launcher's panel handoff).
      C. Idle working set and CPU time of CoreBroker and LauncherHost while no
         panel is open, sampled over a fixed duration.

    Everything runs against an isolated temporary data directory, a temporary
    LOCALAPPDATA and fresh acceptance instance identities; the production
    database is never opened. Only processes this script started are stopped.

    Developer tooling only: it is not shipped and does not change product behavior.

.EXAMPLE
    .\scripts\Measure-StartupFootprint.ps1
    Five iterations per phase and a 60 second idle sample.

.EXAMPLE
    .\scripts\Measure-StartupFootprint.ps1 -Iterations 3 -BackgroundSeconds 30
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Platform = 'x64',

    [ValidateRange(1, 20)]
    [int]$Iterations = 5,

    [ValidateRange(10, 600)]
    [int]$BackgroundSeconds = 60,

    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',

    [switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
    -DisableNameChecking -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherClassName = 'WinWidgetBoard.LauncherHost.EntryWindow'
$readyStatusNames = @(
    '布局已准备就绪，完成编辑时会保存修改。',
    'Layout is ready; changes are saved when you finish editing.'
)

$brokerProcess = $null
$launcherProcess = $null
$startedPanels = [Collections.Generic.List[Diagnostics.Process]]::new()
$testDataRoot = $null
$originalLocalAppData = $env:LOCALAPPDATA

function Resolve-RequiredPath {
    param(
        [string]$Description,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Missing runtime file for ${Description}. Tried: $($Candidates -join '; ')"
}

function Wait-ForReadyStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $element = Get-ElementByAutomationId -Root $Window -AutomationId 'StatusText'
        if ($null -ne $element -and $readyStatusNames -contains $element.Current.Name) {
            return $element.Current.Name
        }

        Start-Sleep -Milliseconds 10
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'WorkspacePanel did not reach the layout-ready status in time.'
}

function Get-Statistics {
    param(
        [double[]]$Values
    )

    $sorted = $Values | Sort-Object
    return [pscustomobject]@{
        Count = $sorted.Count
        Min = [math]::Round($sorted[0], 1)
        Median = [math]::Round($sorted[[int](($sorted.Count - 1) / 2)], 1)
        Max = [math]::Round($sorted[$sorted.Count - 1], 1)
        Mean = [math]::Round((($Values | Measure-Object -Average).Average), 1)
    }
}

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        $random.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

try {
    $launcherPath = Resolve-RequiredPath -Description 'LauncherHost' -Candidates @(
        (Join-Path $repoRoot "artifacts\bin\WinWidgetBoard.LauncherHost\$Configuration\$Platform\WinWidgetBoard.LauncherHost.exe"))
    $panelPath = Resolve-RequiredPath -Description 'WorkspacePanel' -Candidates @(
        (Join-Path $repoRoot "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe"),
        (Join-Path $repoRoot "artifacts\bin\WinWidgetBoard.WorkspacePanel\$Configuration\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe"))
    $brokerPath = Resolve-RequiredPath -Description 'CoreBroker' -Candidates @(
        (Join-Path $repoRoot "artifacts\bin\WinWidgetBoard.CoreBroker\$Configuration\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe"),
        (Join-Path $repoRoot "artifacts\bin\WinWidgetBoard.CoreBroker\$Platform\$Configuration\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe"))

    $running = Get-Process -Name @(
        'WinWidgetBoard.CoreBroker',
        'WinWidgetBoard.WorkspacePanel',
        'WinWidgetBoard.LauncherHost') -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $runningText = ($running | ForEach-Object { '{0}#{1}' -f $_.ProcessName, $_.Id }) -join ', '
        throw "Existing WinWidgetBoard processes detected: $runningText. Close them first."
    }

    $windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
    $memoryBytes = (Get-CimInstance -ClassName Win32_ComputerSystem).TotalPhysicalMemory
    $logicalCores = [Environment]::ProcessorCount
    $commit = (& git -C $repoRoot rev-parse --short HEAD)

    Write-Output '=== ENVIRONMENT ==='
    Write-Output ('OS              {0} {1} build {2}.{3}' -f `
            $windows.ProductName, $windows.DisplayVersion, `
            $windows.CurrentBuildNumber, $windows.UBR)
    Write-Output ('CPU             {0} ({1} logical cores)' -f $cpu.Name.Trim(), $logicalCores)
    Write-Output ('RAM             {0} GB' -f [math]::Round($memoryBytes / 1GB, 1))
    Write-Output ('Build           {0} {1}' -f $Configuration, $Platform)
    Write-Output ('Baseline commit {0}' -f $commit)
    Write-Output ('Debugger        script attached={0}; measured processes started without a debugger' -f `
        ([Diagnostics.Debugger]::IsAttached))
    Write-Output ('Tool            Measure-StartupFootprint.ps1, Stopwatch + UIA + Process counters')
    Write-Output ('Iterations      {0} per latency phase; idle sample {1}s' -f $Iterations, $BackgroundSeconds)
    Write-Output ''

    $testDataRoot = Join-Path $env:TEMP ('WinWidgetBoard-Footprint-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
    $localAppData = Join-Path $testDataRoot 'LocalAppData'
    New-Item -ItemType Directory -Path $localAppData -Force | Out-Null
    Write-Output ('DATA-ISOLATION  {0}' -f $testDataRoot)

    $sessionToken = New-SessionToken
    $brokerProcess = Start-TestBroker `
        -BrokerPath $brokerPath `
        -TestDataRoot $testDataRoot `
        -SessionToken $sessionToken

    Write-Output ''
    Write-Output '=== PHASE A: WorkspacePanel start -> window -> layout ready (ms) ==='
    $windowSamples = [Collections.Generic.List[double]]::new()
    $readySamples = [Collections.Generic.List[double]]::new()
    for ($index = 1; $index -le $Iterations; $index++) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
        $startInfo.UseShellExecute = $false
        $startInfo.Arguments =
            "--acceptance-test --test-instance-id $([Guid]::NewGuid().ToString('N'))"
        $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
        $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
        $startInfo.EnvironmentVariables['LOCALAPPDATA'] = $localAppData
        $startInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $panel = [Diagnostics.Process]::Start($startInfo)
        $startedPanels.Add($panel)

        $windowDeadline = [DateTime]::UtcNow.AddSeconds(30)
        $windowMs = $null
        do {
            $panel.Refresh()
            if ($panel.MainWindowHandle -ne [IntPtr]::Zero) {
                $windowMs = $stopwatch.Elapsed.TotalMilliseconds
                break
            }

            Start-Sleep -Milliseconds 5
        } while ([DateTime]::UtcNow -lt $windowDeadline)
        if ($null -eq $windowMs) {
            throw "WorkspacePanel window did not appear in iteration $index."
        }

        $window = Get-PanelWindow -ProcessId $panel.Id -Timeout ([TimeSpan]::FromSeconds(20))
        $statusName = Wait-ForReadyStatus -Window $window -Timeout ([TimeSpan]::FromSeconds(20))
        $readyMs = $stopwatch.Elapsed.TotalMilliseconds
        $stopwatch.Stop()

        $windowSamples.Add($windowMs)
        $readySamples.Add($readyMs)
        Write-Output ('  iteration {0,2}  window={1,7:N1}  ready={2,7:N1}  status="{3}"' -f `
                $index, $windowMs, $readyMs, $statusName)

        Stop-OwnedProcess -Process $panel
    }

    $windowStats = Get-Statistics -Values $windowSamples.ToArray()
    $readyStats = Get-Statistics -Values $readySamples.ToArray()
    Write-Output ('  window  min={0} median={1} max={2} mean={3}' -f `
            $windowStats.Min, $windowStats.Median, $windowStats.Max, $windowStats.Mean)
    Write-Output ('  ready   min={0} median={1} max={2} mean={3}' -f `
            $readyStats.Min, $readyStats.Median, $readyStats.Max, $readyStats.Mean)
    Write-Output ('  note    iteration 1 is the cold run; later iterations reuse OS file cache.')

    Write-Output ''
    Write-Output '=== PHASE B: LauncherHost entry click -> panel window (ms) ==='
    $env:LOCALAPPDATA = $localAppData
    $launcherInfo = [Diagnostics.ProcessStartInfo]::new($launcherPath)
    $launcherInfo.UseShellExecute = $false
    $launcherInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $launcherInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $launcherInfo.EnvironmentVariables['LOCALAPPDATA'] = $localAppData
    $launcherInfo.EnvironmentVariables['WINWIDGETBOARD_WORKSPACE_PANEL'] = $panelPath
    $launcherInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
    $launcherProcess = [Diagnostics.Process]::Start($launcherInfo)

    $entry = Get-WindowRectByClass `
        -ClassName $launcherClassName `
        -ProcessId $launcherProcess.Id `
        -Timeout ([TimeSpan]::FromSeconds(20))
    Write-Output ('  entry window rect=({0},{1})-({2},{3}) click=({4},{5})' -f `
            $entry.Left, $entry.Top, $entry.Right, $entry.Bottom, $entry.CenterX, $entry.CenterY)

    $entrySamples = [Collections.Generic.List[double]]::new()
    for ($index = 1; $index -le $Iterations; $index++) {
        $before = @(Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Id })

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Invoke-LeftClickAtPoint -X $entry.CenterX -Y $entry.CenterY

        $panel = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $candidate = Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue |
                Where-Object { $before -notcontains $_.Id } |
                Select-Object -First 1
            if ($null -ne $candidate) {
                $candidate.Refresh()
                if ($candidate.MainWindowHandle -ne [IntPtr]::Zero) {
                    $panel = $candidate
                    break
                }
            }

            Start-Sleep -Milliseconds 5
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $panel) {
            throw "LauncherHost did not show a panel window in iteration $index."
        }

        $entryMs = $stopwatch.Elapsed.TotalMilliseconds
        $stopwatch.Stop()
        $entrySamples.Add($entryMs)
        Write-Output ('  iteration {0,2}  entry-to-window={1,7:N1}  panelPid={2}' -f `
                $index, $entryMs, $panel.Id)

        Stop-OwnedProcess -Process $panel
        Start-Sleep -Milliseconds 750
    }

    $entryStats = Get-Statistics -Values $entrySamples.ToArray()
    Write-Output ('  entry   min={0} median={1} max={2} mean={3}' -f `
            $entryStats.Min, $entryStats.Median, $entryStats.Max, $entryStats.Mean)

    Write-Output ''
    Write-Output ('=== PHASE C: idle footprint with no panel open, {0}s ===' -f $BackgroundSeconds)
    $samples = @{
        'CoreBroker' = @{ Process = $brokerProcess; WorkingSet = [Collections.Generic.List[double]]::new() }
        'LauncherHost' = @{ Process = $launcherProcess; WorkingSet = [Collections.Generic.List[double]]::new() }
    }
    foreach ($entryName in $samples.Keys) {
        $samples[$entryName].Process.Refresh()
        $samples[$entryName].StartCpu = $samples[$entryName].Process.TotalProcessorTime
    }

    $idleStopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($idleStopwatch.Elapsed.TotalSeconds -lt $BackgroundSeconds) {
        foreach ($entryName in $samples.Keys) {
            $process = $samples[$entryName].Process
            $process.Refresh()
            $samples[$entryName].WorkingSet.Add($process.WorkingSet64 / 1MB)
        }

        Start-Sleep -Milliseconds 1000
    }

    $idleStopwatch.Stop()
    $elapsedSeconds = $idleStopwatch.Elapsed.TotalSeconds
    foreach ($entryName in @('CoreBroker', 'LauncherHost')) {
        $process = $samples[$entryName].Process
        $process.Refresh()
        $cpuMs = ($process.TotalProcessorTime - $samples[$entryName].StartCpu).TotalMilliseconds
        $cpuPercent = $cpuMs / ($elapsedSeconds * 1000 * $logicalCores) * 100
        $workingSet = Get-Statistics -Values $samples[$entryName].WorkingSet.ToArray()
        Write-Output ('  {0,-13} cpu={1:N3}% of all cores ({2:N0} ms over {3:N1} s)  workingSet min={4} median={5} max={6} MB' -f `
                $entryName, $cpuPercent, $cpuMs, $elapsedSeconds, `
                $workingSet.Min, $workingSet.Median, $workingSet.Max)
    }

    Write-Output ''
    Write-Output 'MEASURE-STARTUP-FOOTPRINT-PASS'
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    foreach ($panel in $startedPanels) {
        Stop-WinWidgetBoardProcess -Process $panel
    }

    foreach ($stray in @(Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue)) {
        if ($stray.Path -eq $panelPath) {
            Stop-WinWidgetBoardProcess -Process $stray
        }
    }

    Stop-WinWidgetBoardProcess -Process $launcherProcess
    Stop-WinWidgetBoardProcess -Process $brokerProcess

    if (-not $KeepTestData -and
        $null -ne $testDataRoot -and
        $testDataRoot.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $testDataRoot)) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
