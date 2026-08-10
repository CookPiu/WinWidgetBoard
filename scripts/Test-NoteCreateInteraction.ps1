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

public static class WinWidgetBoardNoteCreateInput
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

foreach ($requiredPath in @($panelPath, $brokerPath, $PortableDotnetRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required runtime file was not found: $requiredPath"
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
    'WinWidgetBoard-NoteCreateTest-' + [Guid]::NewGuid().ToString('N'))
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

function Get-ElementByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
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

function Wait-VisibleElementByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [TimeSpan]$Timeout,
        [switch]$Enabled
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $element = Get-ElementByName -Root $Root -Name $Name
        if ($null -ne $element -and
            -not $element.Current.IsOffscreen -and
            (-not $Enabled -or $element.Current.IsEnabled)) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element with name not found: $Name"
}

function Get-ElementText {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
    }

    return $Element.Current.Name
}

function Wait-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Expected,
        [TimeSpan]$Timeout,
        [switch]$Enabled
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId $AutomationId `
        -Timeout $Timeout `
        -Enabled:$Enabled
    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        if ([string]::Equals(
                (Get-ElementText -Element $element),
                $Expected,
                [StringComparison]::Ordinal)) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "UI Automation value did not become the expected value: $AutomationId"
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

function Wait-SavedStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId 'NoteEditorStatusText' `
        -Timeout $Timeout
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

function Focus-PanelWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Window
    )

    $windowHandle = [IntPtr]$Window.Current.NativeWindowHandle
    $shown = [WinWidgetBoardNoteCreateInput]::ShowWindow($windowHandle, 5)
    $zeroHandle = [IntPtr]::Zero
    if (($windowHandle -eq $zeroHandle) -or (-not $shown)) {
        throw 'WorkspacePanel could not be shown for UI Automation.'
    }

    [void][WinWidgetBoardNoteCreateInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardNoteCreateInput]::SetActiveWindow($windowHandle)
    $foregroundSet = [WinWidgetBoardNoteCreateInput]::SetForegroundWindow($windowHandle)
    $focusSet = $false
    try {
        $Window.SetFocus()
        $focusSet = $true
    }
    catch {
    }

    if (-not $foregroundSet -and -not $focusSet) {
        throw 'WorkspacePanel could not be focused for UI Automation.'
    }
}

function Start-TestBroker {
    $process = Start-Process `
        -FilePath $brokerPath `
        -ArgumentList @('--session-token', $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN) `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        throw "CoreBroker failed to start. Exit code: $($process.ExitCode)."
    }

    return $process
}

function Start-TestPanel {
    $panelInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $panelInfo.UseShellExecute = $false
    $panelInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $panelInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    $panelInfo.EnvironmentVariables['LOCALAPPDATA'] = $env:LOCALAPPDATA
    $panelInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
        $env:WINWIDGETBOARD_COREBROKER_SESSION_TOKEN
    $process = [Diagnostics.Process]::Start($panelInfo)
    $window = Get-PanelWindow -ProcessId $process.Id -Timeout ([TimeSpan]::FromSeconds(15))
    Focus-PanelWindow -Window $window
    return [pscustomobject]@{
        Process = $process
        Window = $window
    }
}

function Stop-TestProcess {
    param(
        [Diagnostics.Process]$Process
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
    }
    catch [InvalidOperationException] {
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

    $brokerProcess = Start-TestBroker
    $panel = Start-TestPanel
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
    $originalTitle = 'Original note'
    $originalBody = 'original-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-TextValue -Element $titleBox -Value $originalTitle
    Set-TextValue -Element $bodyBox -Value $originalBody
    [void](Wait-SavedStatus -Root $window -Timeout ([TimeSpan]::FromSeconds(10)))

    $newButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NewNoteButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $newButton
    [void](Wait-ElementValue `
        -Root $window `
        -AutomationId 'NoteTitleBox' `
        -Expected string.Empty `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)
    [void](Wait-ElementValue `
        -Root $window `
        -AutomationId 'NoteBodyBox' `
        -Expected string.Empty `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)

    $createdTitle = 'Created note'
    $createdBody = 'created-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-TextValue -Element $titleBox -Value $createdTitle
    Set-TextValue -Element $bodyBox -Value $createdBody
    [void](Wait-SavedStatus -Root $window -Timeout ([TimeSpan]::FromSeconds(10)))

    $listButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'ListNotesButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $listButton
    [void](Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NoteSearchResultsBorder' `
        -Timeout ([TimeSpan]::FromSeconds(10)))
    [void](Wait-VisibleElementByName `
        -Root $window `
        -Name $originalTitle `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)
    [void](Wait-VisibleElementByName `
        -Root $window `
        -Name $createdTitle `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)

    Write-Output 'REAL-NOTE-CREATE-PASS create+list'
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
