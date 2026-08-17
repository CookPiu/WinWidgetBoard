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

foreach ($requiredPath in @($panelPath, $brokerPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required executable was not found: $requiredPath"
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
    'WinWidgetBoard-MarkdownTest-' + [Guid]::NewGuid().ToString('N'))
$brokerProcess = $null
$panelProcess = $null







function Ensure-CheckBoxOn {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern,
            [ref]$pattern)) {
        throw "TogglePattern unavailable: $($Element.Current.Name)"
    }

    $toggle = [System.Windows.Automation.TogglePattern]$pattern
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $toggle.Toggle()
    }
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
        -SessionToken $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN `
        -WindowTimeout ([TimeSpan]::FromSeconds(10))
    $panelProcess = $panel.Process
    $window = $panel.Window

    $bodyBox = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NoteBodyBox' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled
    $markdownBody = '# UI preview' + [Environment]::NewLine + [Environment]::NewLine + '- **item**'
    Set-TextValue -Element $bodyBox -Value $markdownBody

    $noteScroll = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NotesCardScrollViewer' -Timeout ([TimeSpan]::FromSeconds(5))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    $markdownCheckBox = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'MarkdownModeCheckBox' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled -ScrollContainer $noteScroll
    Ensure-CheckBoxOn -Element $markdownCheckBox

    $previewButton = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'PreviewNoteButton' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled -ScrollContainer $noteScroll
    Invoke-Element -Element $previewButton

    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    [void](Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NoteMarkdownPreviewPanel' -Timeout ([TimeSpan]::FromSeconds(5)))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    $editButton = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'EditMarkdownButton' -Timeout ([TimeSpan]::FromSeconds(5)) -Enabled -ScrollContainer $noteScroll
    Invoke-Element -Element $editButton
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    $bodyBox = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NoteBodyBox' -Timeout ([TimeSpan]::FromSeconds(5)) -Enabled
    $valuePattern = $bodyBox.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $actualBody = $valuePattern.Current.Value
    $normalizedExpected = $markdownBody.Replace("`r`n", "`n").Replace("`r", "`n")
    $normalizedActual = $actualBody.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($normalizedActual -ne $normalizedExpected) {
        $expectedDisplay = $markdownBody.Replace("`r", '<CR>').Replace("`n", '<LF>')
        $actualDisplay = $actualBody.Replace("`r", '<CR>').Replace("`n", '<LF>')
        throw "Markdown source text changed after preview round trip. Expected '$expectedDisplay'; actual '$actualDisplay'."
    }

    Write-Output 'REAL-NOTE-MARKDOWN-PASS mode+preview+roundtrip'
}
finally {
    Stop-OwnedProcess -Process $panelProcess
    Stop-TestProcess -Process $brokerProcess

    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = $oldSessionToken
    $env:LOCALAPPDATA = $oldLocalAppData
    if (Test-Path -LiteralPath $testDataRoot) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force
    }
}
