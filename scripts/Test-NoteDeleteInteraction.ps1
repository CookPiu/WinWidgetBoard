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
    'WinWidgetBoard-NoteDeleteTest-' + [Guid]::NewGuid().ToString('N'))
$brokerProcess = $null
$panelProcess = $null






function Wait-VisibleElementByAnyName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Names,
        [TimeSpan]$Timeout,
        [switch]$Enabled
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        foreach ($name in $Names) {
            $element = Get-ElementByName -Root $Root -Name $name
            if ($null -ne $element -and
                -not $element.Current.IsOffscreen -and
                (-not $Enabled -or $element.Current.IsEnabled)) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element was not found for names: $($Names -join ', ')"
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
    $actual = $null
    do {
        $actual = Get-ElementText -Element $element
        if ([string]::Equals(
                $actual,
                $Expected,
                [StringComparison]::Ordinal)) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $statusElement = Get-ElementByAutomationId -Root $Root -AutomationId 'StatusText'
    $status = if ($null -eq $statusElement) {
        '<missing>'
    }
    else {
        Get-ElementText -Element $statusElement
    }
    throw (("UI Automation value did not become the expected value: {0}. " +
        "Expected='{1}', Actual='{2}', Status='{3}'") -f
        $AutomationId,
        $Expected,
        $actual,
        $status)
}



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

function Wait-DeletedStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [TimeSpan]$Timeout
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId 'StatusText' `
        -Timeout $Timeout
    $deadline = [DateTime]::UtcNow + $Timeout
    $deletedMarker = [string]::Concat(
        [char]0x4FBF,
        [char]0x7B7E,
        [char]0x5DF2,
        [char]0x5220,
        [char]0x9664)
    do {
        $text = Get-ElementText -Element $element
        if ($text -match ('Note deleted\.|' + [regex]::Escape($deletedMarker))) {
            return $element
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Note deletion did not complete. Last status: $text"
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
    $originalTitle = 'Original note'
    $originalBody = 'original-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-TextValue -Element $titleBox -Value $originalTitle
    Set-TextValue -Element $bodyBox -Value $originalBody
    $noteScroll = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NotesCardScrollViewer' `
        -Timeout ([TimeSpan]::FromSeconds(5))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SavedStatus `
        -Root $window `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -ScrollContainer $noteScroll)

    $newButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'NewNoteButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled `
        -ScrollContainer $noteScroll
    Invoke-Element -Element $newButton
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0
    [void](Wait-ElementValue `
        -Root $window `
        -AutomationId 'NoteTitleBox' `
        -Expected ([string]::Empty) `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)

    $createdTitle = 'Created note'
    $createdBody = 'created-sentinel-' + [Guid]::NewGuid().ToString('N')
    Set-TextValue -Element $titleBox -Value $createdTitle
    Set-TextValue -Element $bodyBox -Value $createdBody
    Set-VerticalScrollPercent -Element $noteScroll -Percent 100
    [void](Wait-SavedStatus `
        -Root $window `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -ScrollContainer $noteScroll)

    $deleteButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'DeleteCurrentNoteButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled `
        -ScrollContainer $noteScroll
    Invoke-Element -Element $deleteButton

    $confirmButton = Wait-VisibleElementByAnyName `
        -Root $window `
        -Names @('Delete', '删除') `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $confirmButton
    [void](Wait-DeletedStatus `
        -Root $window `
        -Timeout ([TimeSpan]::FromSeconds(10)))
    Set-VerticalScrollPercent -Element $noteScroll -Percent 0

    [void](Wait-ElementValue `
        -Root $window `
        -AutomationId 'NoteTitleBox' `
        -Expected $originalTitle `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)
    [void](Wait-ElementValue `
        -Root $window `
        -AutomationId 'NoteBodyBox' `
        -Expected $originalBody `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)

    $listButton = Wait-VisibleElementByAutomationId `
        -Root $window `
        -AutomationId 'ListNotesButton' `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled
    Invoke-Element -Element $listButton
    [void](Wait-VisibleElementByName `
        -Root $window `
        -Name $originalTitle `
        -Timeout ([TimeSpan]::FromSeconds(10)) `
        -Enabled)
    $deletedResult = Get-ElementByName -Root $window -Name $createdTitle
    if ($null -ne $deletedResult -and -not $deletedResult.Current.IsOffscreen) {
        throw "Deleted note still appears in the visible note list: $createdTitle"
    }

    Write-Output 'REAL-NOTE-DELETE-PASS current+list'
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
