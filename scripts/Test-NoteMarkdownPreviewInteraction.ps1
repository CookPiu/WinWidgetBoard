[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardMarkdownInput
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
}
'@

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

$running = Get-Process -Name @(
    'WinWidgetBoard.CoreBroker',
    'WinWidgetBoard.WorkspacePanel'
) -ErrorAction SilentlyContinue
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
        if ($null -eq $window) {
            Start-Sleep -Milliseconds 100
        }
    } while ($null -eq $window -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $window) {
        throw "WorkspacePanel window did not appear within $($Timeout.TotalSeconds) seconds."
    }

    return $window
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

function Wait-VisibleElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [TimeSpan]$Timeout,
        [switch]$Enabled
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element -and
            -not $element.Current.IsOffscreen -and
            (-not $Enabled -or $element.Current.IsEnabled)) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element not found: $AutomationId"
}

function Invoke-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    if (-not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen) {
        throw "UI Automation element is not invokable: $($Element.Current.Name)"
    }

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "InvokePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Set-TextValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        throw "ValuePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

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

    $brokerStartParameters = @{
        FilePath = $brokerPath
        ArgumentList = @('--session-token', $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN)
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $brokerProcess = Start-Process @brokerStartParameters
    Start-Sleep -Milliseconds 500
    if ($brokerProcess.HasExited) {
        throw "CoreBroker failed to start. Exit code: $($brokerProcess.ExitCode)."
    }

    $panelInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $panelInfo.UseShellExecute = $false
    $panelInfo.Environment['DOTNET_ROOT'] = $PortableDotnetRoot
    $panelInfo.Environment['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $panelInfo.Environment['LOCALAPPDATA'] = $testDataRoot
    $panelInfo.Environment['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
        $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $panelProcess = [Diagnostics.Process]::Start($panelInfo)
    $window = Get-PanelWindow -ProcessId $panelProcess.Id -Timeout ([TimeSpan]::FromSeconds(10))
    $windowHandle = [IntPtr]$window.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero -or
        -not [WinWidgetBoardMarkdownInput]::ShowWindow($windowHandle, 5)) {
        throw 'WorkspacePanel could not be brought to the foreground for UI Automation.'
    }

    [void][WinWidgetBoardMarkdownInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardMarkdownInput]::SetActiveWindow($windowHandle)
    $foregroundSet = [WinWidgetBoardMarkdownInput]::SetForegroundWindow($windowHandle)
    $focusSet = $false
    try {
        $window.SetFocus()
        $focusSet = $true
    }
    catch {
    }
    if (-not $foregroundSet -and -not $focusSet) {
        throw 'WorkspacePanel could not be focused for UI Automation.'
    }

    $bodyBox = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NoteBodyBox' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled
    $markdownBody = '# UI preview' + [Environment]::NewLine + [Environment]::NewLine + '- **item**'
    Set-TextValue -Element $bodyBox -Value $markdownBody

    $markdownCheckBox = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'MarkdownModeCheckBox' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled
    Ensure-CheckBoxOn -Element $markdownCheckBox

    $previewButton = Wait-VisibleElementByAutomationId -Root $window -AutomationId 'PreviewNoteButton' -Timeout ([TimeSpan]::FromSeconds(10)) -Enabled
    Invoke-Element -Element $previewButton

    [void](Wait-VisibleElementByAutomationId -Root $window -AutomationId 'NoteMarkdownPreviewPanel' -Timeout ([TimeSpan]::FromSeconds(5)))
    [void](Wait-VisibleElementByAutomationId -Root $window -AutomationId 'EditMarkdownButton' -Timeout ([TimeSpan]::FromSeconds(5)) -Enabled)

    Write-Output 'REAL-NOTE-MARKDOWN-PASS mode+preview'
}
finally {
    if ($null -ne $panelProcess -and -not $panelProcess.HasExited) {
        [void]$panelProcess.CloseMainWindow()
        if (-not $panelProcess.WaitForExit(3000)) {
            $panelProcess.Kill($true)
            [void]$panelProcess.WaitForExit(3000)
        }
    }

    if ($null -ne $brokerProcess -and -not $brokerProcess.HasExited) {
        $brokerProcess.Kill($true)
        [void]$brokerProcess.WaitForExit(3000)
    }

    $env:DOTNET_ROOT = $oldDotnetRoot
    $env:DOTNET_ROOT_X64 = $oldDotnetRootX64
    $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN = $oldSessionToken
    $env:LOCALAPPDATA = $oldLocalAppData
    if (Test-Path -LiteralPath $testDataRoot) {
        Remove-Item -LiteralPath $testDataRoot -Recurse -Force
    }
}
