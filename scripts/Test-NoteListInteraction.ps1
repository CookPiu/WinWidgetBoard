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
    'WinWidgetBoard-NoteListTest-' + [Guid]::NewGuid().ToString('N'))
$brokerProcess = $null
$panelProcess = $null










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
    $panel = Start-TestPanel `
        -PanelPath $panelPath `
        -PortableDotnetRoot $PortableDotnetRoot `
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $panelProcess = $panel.Process
    $window = $panel.Window

    $titleBox = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NoteTitleBox' `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled
    $bodyBox = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NoteBodyBox' `
        -Timeout ([TimeSpan]::FromSeconds(15)) `
        -Enabled
    $seedTitle = 'List seed note'
    $seedBody = 'list-sentinel-' + [Guid]::NewGuid().ToString('N')

    Set-TextValue -Element $titleBox -Value $seedTitle
    Set-TextValue -Element $bodyBox -Value $seedBody
    $noteScroll = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NotesCardScrollViewer' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SavedStatus `
        -Root $window `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -ScrollContainer $noteScroll)

    $listButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'ListNotesButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $listButton

    $resultButton = Wait-VisibleElementByName `
        -Root $window `
        -Name $seedTitle `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $resultButton

    Write-Output 'REAL-NOTE-LIST-PASS list+saved-note'
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
