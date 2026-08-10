[CmdletBinding()]
param(
    [switch] $KeepBroker
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
$launcherPath = Join-Path $repoRoot 'artifacts\bin\WinWidgetBoard.LauncherHost\Release\x64\WinWidgetBoard.LauncherHost.exe'
$panelPath = Join-Path $repoRoot 'artifacts\bin\WinWidgetBoard.WorkspacePanel\x64\Release\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe'
$brokerPath = Join-Path $repoRoot 'artifacts\bin\WinWidgetBoard.CoreBroker\Release\net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe'

foreach ($requiredPath in @($dotnetRoot, $launcherPath, $panelPath, $brokerPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing runtime file: $requiredPath. Build the Release configuration first."
    }
}

$running = Get-Process -Name @(
    'WinWidgetBoard.CoreBroker',
    'WinWidgetBoard.WorkspacePanel',
    'WinWidgetBoard.LauncherHost'
) -ErrorAction SilentlyContinue
if ($null -ne $running) {
    $runningText = ($running | ForEach-Object { '{0}#{1}' -f $_.ProcessName, $_.Id }) -join ', '
    throw "Existing WinWidgetBoard processes detected: $runningText. Close them normally before running this script."
}

$oldDotnetRoot = $env:DOTNET_ROOT
$oldDotnetRootX64 = $env:DOTNET_ROOT_X64
$oldPanelPath = $env:WINWIDGETBOARD_WORKSPACE_PANEL
$oldSessionToken = $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
$brokerProcess = $null

try {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:WINWIDGETBOARD_WORKSPACE_PANEL = $panelPath
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    $brokerProcess = Start-Process `
        -FilePath $brokerPath `
        -ArgumentList @('--session-token', $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN) `
        -WindowStyle Hidden `
        -PassThru

    Start-Sleep -Milliseconds 500
    if ($brokerProcess.HasExited) {
        throw "CoreBroker failed to start. Exit code: $($brokerProcess.ExitCode)."
    }

    $launcherProcess = Start-Process `
        -FilePath $launcherPath `
        -Wait `
        -PassThru
    $launcherExitCode = $launcherProcess.ExitCode
    if ($launcherExitCode -ne 0) {
        throw "LauncherHost exit code: $launcherExitCode."
    }
}
finally {
    if (-not $KeepBroker -and $null -ne $brokerProcess -and -not $brokerProcess.HasExited) {
        Stop-Process -Id $brokerProcess.Id -ErrorAction SilentlyContinue
    }

    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    $env:WINWIDGETBOARD_WORKSPACE_PANEL = $oldPanelPath
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = $oldSessionToken
}
