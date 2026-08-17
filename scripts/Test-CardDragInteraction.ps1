[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',
    [switch]$WithBroker,
    [switch]$PersistenceRecovery,
    [switch]$RetainTestData,
    [ValidateSet(0, 2, 4, 6)]
    [int]$ExpectedColumnCount = 0,
    [string[]]$PanelContextArguments = @()
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') -DisableNameChecking -Force
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinWidgetBoardPointerInput
{
    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VirtualKeyEscape = 0x1B;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        Input[] inputs,
        int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static bool SendAbsoluteMove(int x, int y)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 1 || height <= 1)
        {
            return false;
        }

        int normalizedX = (int)Math.Round(
            Math.Min(Math.Max(x - left, 0), width - 1) * 65535.0 / (width - 1));
        int normalizedY = (int)Math.Round(
            Math.Min(Math.Max(y - top, 0), height - 1) * 65535.0 / (height - 1));
        return SendMouse(
            MouseMove | MouseAbsolute | MouseVirtualDesk,
            normalizedX,
            normalizedY);
    }

    public static bool SendLeftButton(bool pressed)
    {
        return SendMouse(pressed ? MouseLeftDown : MouseLeftUp, 0, 0);
    }

    public static bool MakeTopmost(IntPtr window)
    {
        return SetWindowPos(
            window,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    public static void SendEscape()
    {
        keybd_event(VirtualKeyEscape, 0, 0, UIntPtr.Zero);
        keybd_event(VirtualKeyEscape, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static bool SendMouse(uint flags, int x, int y)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Dx = x,
                        Dy = y,
                        Flags = flags,
                    },
                },
            },
        };
        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }
}
'@

$panelProcess = $null
$brokerProcess = $null
$testDataRoot = $null
$originalCursor = [WinWidgetBoardPointerInput+Point]::new()
[void][WinWidgetBoardPointerInput]::GetCursorPos([ref]$originalCursor)





function Wait-ForElementName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string[]]$ExpectedNames,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastName = $null
    do {
        $element = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId $AutomationId
        if ($null -ne $element) {
            $lastName = $element.Current.Name
            if ($ExpectedNames -contains $lastName) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Element '$AutomationId' did not reach the expected state. Last name: '$lastName'."
}


function Get-ElementCenter {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    $rectangle = $Element.Current.BoundingRectangle
    if ($rectangle.IsEmpty -or $rectangle.Width -le 0 -or $rectangle.Height -le 0) {
        throw "Element '$($Element.Current.Name)' has no visible bounding rectangle."
    }

    return [pscustomobject]@{
        X = $rectangle.Left + ($rectangle.Width / 2)
        Y = $rectangle.Top + ($rectangle.Height / 2)
    }
}

function Format-Point {
    param(
        [pscustomobject]$Point
    )

    $x = [Math]::Round($Point.X, 1)
    $y = [Math]::Round($Point.Y, 1)
    return "($x,$y)"
}

function Get-PointRelativeToElement {
    param(
        [pscustomobject]$Point,
        [System.Windows.Automation.AutomationElement]$Element
    )

    $rectangle = $Element.Current.BoundingRectangle
    if ($rectangle.IsEmpty -or
        $rectangle.Width -le 0 -or
        $rectangle.Height -le 0) {
        throw 'Reference UI Automation element has an empty bounding rectangle.'
    }

    return [pscustomobject]@{
        X = $Point.X - $rectangle.Left
        Y = $Point.Y - $rectangle.Top
    }
}

function Wait-StableRelativeCenter {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Names,
        [System.Windows.Automation.AutomationElement]$Reference,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $previous = $null
    $stableSamples = 0
    do {
        $element = Get-ElementByNames `
            -Root $Root `
            -Names $Names `
            -Optional
        if ($null -eq $element -or $element.Current.IsOffscreen) {
            $stableSamples = 0
            $previous = $null
            Start-Sleep -Milliseconds 100
            continue
        }

        $screenPoint = Get-ElementCenter -Element $element
        $relativePoint = Get-PointRelativeToElement `
            -Point $screenPoint `
            -Element $Reference
        if ($null -ne $previous) {
            $distance = [Math]::Sqrt(
                [Math]::Pow($relativePoint.X - $previous.X, 2) +
                [Math]::Pow($relativePoint.Y - $previous.Y, 2))
            $stableSamples = if ($distance -le 2) {
                $stableSamples + 1
            }
            else {
                0
            }
        }
        $previous = $relativePoint
        if ($stableSamples -ge 5) {
            return [pscustomobject]@{
                Element = $element
                ScreenPoint = $screenPoint
                RelativePoint = $relativePoint
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "UI Automation element did not settle: $($Names -join '/')"
}

function Assert-PanelOwnsPoint {
    param(
        [pscustomobject]$Point,
        [int]$ProcessId,
        [string]$Description
    )

    $nativePoint = [WinWidgetBoardPointerInput+Point]::new()
    $nativePoint.X = [int][Math]::Round($Point.X)
    $nativePoint.Y = [int][Math]::Round($Point.Y)
    $windowAtPoint = [WinWidgetBoardPointerInput]::WindowFromPoint($nativePoint)
    [uint32]$ownerProcessId = 0
    [void][WinWidgetBoardPointerInput]::GetWindowThreadProcessId(
        $windowAtPoint,
        [ref]$ownerProcessId)
    if ($ownerProcessId -ne $ProcessId) {
        throw "$Description is covered by process $ownerProcessId instead of WorkspacePanel $ProcessId."
    }
}

function Get-CommonAncestorScore {
    param(
        [System.Windows.Automation.AutomationElement]$First,
        [System.Windows.Automation.AutomationElement]$Second
    )

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $firstAncestors = @{}
    $current = $First
    $distance = 0
    while ($null -ne $current -and $distance -lt 64) {
        $key = [string]::Join(',', @($current.GetRuntimeId()))
        if (-not $firstAncestors.ContainsKey($key)) {
            $firstAncestors[$key] = $distance
        }
        $current = $walker.GetParent($current)
        $distance++
    }

    $current = $Second
    $distance = 0
    while ($null -ne $current -and $distance -lt 64) {
        $key = [string]::Join(',', @($current.GetRuntimeId()))
        if ($firstAncestors.ContainsKey($key)) {
            return $distance + [int]$firstAncestors[$key]
        }
        $current = $walker.GetParent($current)
        $distance++
    }

    return [int]::MaxValue
}

function Get-DragHandleForTitle {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.AutomationElement]$Title
    )

    $handleAutomationId = switch ($Title.Current.Name) {
        { $_ -in @('便签', 'Notes') } { 'NotesCardDragHandle'; break }
        { $_ -in @('计时器', 'Timer') } { 'TimerCardDragHandle'; break }
        { $_ -in @('待办', 'To-do') } { 'TodoCardDragHandle'; break }
        { $_ -in @('本地日历', 'Local calendar') } {
            'CalendarCardDragHandle'
            break
        }
        default { $null }
    }
    if ($null -ne $handleAutomationId) {
        $explicitHandle = Get-ElementByAutomationId `
            -Root $Root `
            -AutomationId $handleAutomationId
        if ($null -eq $explicitHandle) {
            throw "Stable drag handle was not found: $handleAutomationId"
        }
        if ($explicitHandle.Current.IsOffscreen) {
            throw "Stable drag handle is offscreen: $handleAutomationId"
        }
        return $explicitHandle
    }

    $titleCenter = Get-ElementCenter -Element $Title
    $handles = @(
        Get-Descendants -Root $Root |
            Where-Object {
                if ($_.Current.Name -notin @('拖动卡片', 'Drag card') -or
                    $_.Current.IsOffscreen) {
                    return $false
                }

                $rectangle = $_.Current.BoundingRectangle
                return -not $rectangle.IsEmpty -and
                    $rectangle.Width -gt 0 -and
                    $rectangle.Height -gt 0
            }
    )
    if ($handles.Count -lt 2) {
        throw "Expected at least two visible drag handles, found $($handles.Count)."
    }

    return $handles |
        Sort-Object `
            @{ Expression = {
                Get-CommonAncestorScore -First $Title -Second $_
            } }, `
            @{ Expression = {
                $center = Get-ElementCenter -Element $_
                [Math]::Abs($center.X - $titleCenter.X) +
                    [Math]::Abs($center.Y - $titleCenter.Y)
            } } |
        Select-Object -First 1
}

function Invoke-MouseDrag {
    param(
        [pscustomobject]$Start,
        [pscustomobject]$End
    )

    if (-not [WinWidgetBoardPointerInput]::SendAbsoluteMove(
            [int][Math]::Round($Start.X),
            [int][Math]::Round($Start.Y))) {
        throw 'SendInput failed while positioning the drag start.'
    }
    Start-Sleep -Milliseconds 100
    if (-not [WinWidgetBoardPointerInput]::SendLeftButton($true)) {
        throw 'SendInput failed while pressing the left mouse button.'
    }
    try {
        foreach ($step in 1..20) {
            $progress = $step / 20.0
            $x = $Start.X + (($End.X - $Start.X) * $progress)
            $y = $Start.Y + (($End.Y - $Start.Y) * $progress)
            if (-not [WinWidgetBoardPointerInput]::SendAbsoluteMove(
                    [int][Math]::Round($x),
                    [int][Math]::Round($y))) {
                throw 'SendInput failed while moving the pointer.'
            }
            Start-Sleep -Milliseconds 20
        }
    }
    finally {
        [void][WinWidgetBoardPointerInput]::SendLeftButton($false)
    }
}

function Invoke-MouseDragAndCancel {
    param(
        [pscustomobject]$Start,
        [pscustomobject]$End
    )

    if (-not [WinWidgetBoardPointerInput]::SendAbsoluteMove(
            [int][Math]::Round($Start.X),
            [int][Math]::Round($Start.Y))) {
        throw 'SendInput failed while positioning the cancel drag start.'
    }
    Start-Sleep -Milliseconds 100
    if (-not [WinWidgetBoardPointerInput]::SendLeftButton($true)) {
        throw 'SendInput failed while pressing the cancel drag button.'
    }
    try {
        foreach ($step in 1..20) {
            $progress = $step / 20.0
            $x = $Start.X + (($End.X - $Start.X) * $progress)
            $y = $Start.Y + (($End.Y - $Start.Y) * $progress)
            if (-not [WinWidgetBoardPointerInput]::SendAbsoluteMove(
                    [int][Math]::Round($x),
                    [int][Math]::Round($y))) {
                throw 'SendInput failed while moving the cancel drag pointer.'
            }
            Start-Sleep -Milliseconds 20
        }

        [WinWidgetBoardPointerInput]::SendEscape()
        Start-Sleep -Milliseconds 250
    }
    finally {
        [void][WinWidgetBoardPointerInput]::SendLeftButton($false)
    }
}

function Assert-Near {
    param(
        [pscustomobject]$Actual,
        [pscustomobject]$Expected,
        [double]$Tolerance,
        [string]$Description
    )

    $distance = [Math]::Sqrt(
        [Math]::Pow($Actual.X - $Expected.X, 2) +
        [Math]::Pow($Actual.Y - $Expected.Y, 2))
    if ($distance -gt $Tolerance) {
        throw "$Description moved to an unexpected position (distance $([Math]::Round($distance, 1)) px)."
    }
}

function Assert-Far {
    param(
        [pscustomobject]$Actual,
        [pscustomobject]$Expected,
        [double]$MinimumDistance,
        [string]$Description
    )

    $distance = [Math]::Sqrt(
        [Math]::Pow($Actual.X - $Expected.X, 2) +
        [Math]::Pow($Actual.Y - $Expected.Y, 2))
    if ($distance -lt $MinimumDistance) {
        throw "$Description did not move far enough (distance $([Math]::Round($distance, 1)) px)."
    }
}


function Test-ResponsiveColumnCount {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [int]$ColumnCount
    )

    $contentScroll = Get-ElementByAutomationId `
        -Root $Root `
        -AutomationId 'ContentScrollViewer'
    if ($null -eq $contentScroll) {
        throw 'The card surface scroll viewer was not exposed to UI Automation.'
    }

    $cardNames = [ordered]@{
        Notes = @('便签', 'Notes')
        Timer = @('计时器', 'Timer')
        Todo = @('待办', 'To-do')
        Calendar = @('本地日历', 'Local calendar')
    }
    $xByCard = @{}
    foreach ($percent in @(0, 25, 50, 75, 100)) {
        Set-VerticalScrollPercent -Element $contentScroll -Percent $percent -DelayMilliseconds 500
        foreach ($entry in $cardNames.GetEnumerator()) {
            $element = Get-ElementByNames `
                -Root $Root `
                -Names $entry.Value `
                -Optional
            if ($null -eq $element -or $element.Current.IsOffscreen) {
                continue
            }

            $rectangle = $element.Current.BoundingRectangle
            if (-not $rectangle.IsEmpty -and
                $rectangle.Width -gt 0 -and
                $rectangle.Height -gt 0) {
                $xByCard[$entry.Key] =
                    $rectangle.Left + ($rectangle.Width / 2)
            }
        }
    }

    if ($xByCard.Count -ne $cardNames.Count) {
        throw ("Responsive grid scan found {0}/{1} cards: {2}" -f
            $xByCard.Count,
            $cardNames.Count,
            ($xByCard.Keys -join ','))
    }

    $distinctX = New-Object 'System.Collections.Generic.List[double]'
    foreach ($x in @($xByCard.Values | Sort-Object)) {
        if ($distinctX.Count -eq 0 -or
            [Math]::Abs($x - $distinctX[$distinctX.Count - 1]) -gt 40) {
            $distinctX.Add($x)
        }
    }
    $expectedDistinctColumns = switch ($ColumnCount) {
        2 { 1 }
        4 { 2 }
        6 { 3 }
    }
    if ($distinctX.Count -ne $expectedDistinctColumns) {
        throw ("Expected {0} visual card columns for a {1}-column logical grid, " +
            "but found {2}. Centers: {3}" -f
            $expectedDistinctColumns,
            $ColumnCount,
            $distinctX.Count,
            (($distinctX | ForEach-Object { [Math]::Round($_, 1) }) -join ','))
    }

    Write-Output (
        "REAL-GRID-PASS columns=$ColumnCount " +
        "visual-columns=$expectedDistinctColumns cards=4")
}

function Test-CardDragAndUndo {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$SourceNames,
        [string[]]$TargetNames,
        [ValidateSet('Handle', 'Surface')]
        [string]$Mode,
        [int]$ProcessId
    )

    $sourceTitle = Get-ElementByNames -Root $Root -Names $SourceNames
    $targetTitle = Get-ElementByNames -Root $Root -Names $TargetNames
    if ($null -eq $sourceTitle -or $sourceTitle.Current.IsOffscreen) {
        throw "$($SourceNames -join '/') title is not visible for $Mode drag."
    }
    if ($null -eq $targetTitle -or $targetTitle.Current.IsOffscreen) {
        throw "$($TargetNames -join '/') target is not visible for $Mode drag."
    }

    $sourceStart = Get-ElementCenter -Element $sourceTitle
    $dragStart = if ($Mode -eq 'Handle') {
        Get-ElementCenter -Element (
            Get-DragHandleForTitle -Root $Root -Title $sourceTitle)
    }
    else {
        $sourceStart
    }
    $dragEnd = Get-ElementCenter -Element $targetTitle
    Write-Output (
        "DRAG source=$($SourceNames[0]) mode=$Mode " +
        "start=$(Format-Point $dragStart) target=$(Format-Point $dragEnd)")
    Assert-PanelOwnsPoint `
        -Point $dragStart `
        -ProcessId $ProcessId `
        -Description "$($SourceNames[0]) $Mode drag source"
    Assert-PanelOwnsPoint `
        -Point $dragEnd `
        -ProcessId $ProcessId `
        -Description "$($TargetNames[0]) $Mode drag target"

    Invoke-MouseDrag -Start $dragStart -End $dragEnd
    Start-Sleep -Milliseconds 750
    $sourceTitle = Get-ElementByNames -Root $Root -Names $SourceNames
    Assert-Far `
        -Actual (Get-ElementCenter -Element $sourceTitle) `
        -Expected $sourceStart `
        -MinimumDistance 80 `
        -Description "$($SourceNames[0]) after $Mode drag"

    $undoButton = Get-ElementByAutomationId `
        -Root $Root `
        -AutomationId 'UndoLayoutButton'
    if ($null -eq $undoButton -or -not $undoButton.Current.IsEnabled) {
        throw "Layout undo was unavailable after $($SourceNames[0]) $Mode drag."
    }
    Invoke-Element -Element $undoButton
    Start-Sleep -Milliseconds 750
    $sourceTitle = Get-ElementByNames -Root $Root -Names $SourceNames
    Assert-Near `
        -Actual (Get-ElementCenter -Element $sourceTitle) `
        -Expected $sourceStart `
        -Tolerance 48 `
        -Description "$($SourceNames[0]) after undoing $Mode drag"
}


try {
    if ($PersistenceRecovery -and -not $WithBroker) {
        throw 'PersistenceRecovery requires WithBroker.'
    }
    if ($WithBroker) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $testDataRoot = [IO.Path]::GetFullPath(
            (Join-Path $temporaryRoot (
                'WinWidgetBoard.LayoutAcceptance.' +
                [Guid]::NewGuid().ToString('N'))))
        if (-not $testDataRoot.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Temporary layout root escaped the system temp directory: $testDataRoot"
        }

        New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
    }

    $existing = Get-Process -Name 'WinWidgetBoard.LauncherHost' `
        -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        throw 'A conflicting LauncherHost process is already running. Close it before the drag interaction test.'
    }

    $panelPath = Join-Path $PSScriptRoot (
        "..\artifacts\bin\WinWidgetBoard.WorkspacePanel\$Platform\$Configuration\" +
        'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.WorkspacePanel.exe')
    $panelPath = [IO.Path]::GetFullPath($panelPath)
    if (-not (Test-Path -LiteralPath $panelPath -PathType Leaf)) {
        throw "WorkspacePanel executable was not found: $panelPath"
    }

    $sessionToken = $null
    if ($WithBroker) {
        $brokerPath = Join-Path $PSScriptRoot (
            "..\artifacts\bin\WinWidgetBoard.CoreBroker\$Platform\$Configuration\" +
            'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe')
        $brokerPath = [IO.Path]::GetFullPath($brokerPath)
        if (-not (Test-Path -LiteralPath $brokerPath -PathType Leaf)) {
            throw "CoreBroker executable was not found: $brokerPath"
        }

        $sessionRandomBytes = New-Object byte[] 32
        $sessionRandom = [Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $sessionRandom.GetBytes($sessionRandomBytes)
        }
        finally {
            $sessionRandom.Dispose()
        }
        $sessionToken = [Convert]::ToBase64String(
            $sessionRandomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $brokerAcceptanceInstanceId = [Guid]::NewGuid().ToString('N')
        $brokerStartInfo = [Diagnostics.ProcessStartInfo]::new($brokerPath)
        $brokerStartInfo.UseShellExecute = $false
        $brokerStartInfo.CreateNoWindow = $true
        $brokerStartInfo.Arguments =
            "--session-token $sessionToken" +
            " --acceptance-test --test-instance-id $brokerAcceptanceInstanceId" +
            " --data-directory `"$testDataRoot`""
        $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
        $brokerStartInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
        $brokerStartInfo.EnvironmentVariables[
            'WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
        $brokerProcess = [Diagnostics.Process]::Start($brokerStartInfo)
        Start-Sleep -Milliseconds 500
        if ($brokerProcess.HasExited) {
            throw "CoreBroker exited with code $($brokerProcess.ExitCode)."
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $startInfo.UseShellExecute = $false
    $acceptanceInstanceId = [Guid]::NewGuid().ToString('N')
    $startArguments =
        "--acceptance-test --test-instance-id $acceptanceInstanceId"
    if ($PanelContextArguments.Count -gt 0) {
        $startArguments += ' ' + ($PanelContextArguments -join ' ')
    }
    $startInfo.Arguments = $startArguments
    $startInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    if ($WithBroker) {
        $startInfo.EnvironmentVariables[
            'WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
    }
    $panelProcess = [Diagnostics.Process]::Start($startInfo)
    $window = Get-PanelWindow `
        -ProcessId $panelProcess.Id `
        -Timeout ([TimeSpan]::FromSeconds(10))
    $panelProcess.Refresh()
    $panelWindowHandle = $panelProcess.MainWindowHandle
    if ($panelWindowHandle -eq [IntPtr]::Zero) {
        $panelWindowHandle = [IntPtr]$window.Current.NativeWindowHandle
    }
    [void][WinWidgetBoardPointerInput]::ShowWindow($panelWindowHandle, 5)
    if (-not [WinWidgetBoardPointerInput]::MakeTopmost($panelWindowHandle)) {
        throw 'WorkspacePanel could not be made temporarily topmost for pointer acceptance.'
    }
    [void][WinWidgetBoardPointerInput]::BringWindowToTop($panelWindowHandle)
    [void][WinWidgetBoardPointerInput]::SetForegroundWindow($panelWindowHandle)
    Start-Sleep -Milliseconds 250

    $expectedLayoutStatuses = if ($WithBroker) {
        @(
            '已恢复异常的布局空行；点击“完成”可保存修复后的位置。',
            'Recovered an excessive saved layout gap. Choose Done to save the repaired positions.',
            '已加载保存的卡片布局。',
            'Saved card layout loaded.',
            '布局已准备就绪，完成编辑时会保存修改。',
            'Layout is ready; changes will be saved when editing is finished.'
        )
    }
    else {
        @(
            'CoreBroker 不可用，卡片布局修改无法保存。',
            'CoreBroker is unavailable; card layout changes cannot be saved.'
        )
    }
    $statusElement = Wait-ForElementName `
        -Root $window `
        -AutomationId 'StatusText' `
        -ExpectedNames $expectedLayoutStatuses `
        -Timeout ([TimeSpan]::FromSeconds(10))
    Write-Output "STATUS after layout initialization: $($statusElement.Current.Name)"

    if ($ExpectedColumnCount -ne 0) {
        Test-ResponsiveColumnCount `
            -Root $window `
            -ColumnCount $ExpectedColumnCount
        return
    }

    $editButton = Get-ElementByAutomationId `
        -Root $window `
        -AutomationId 'EditLayoutButton'
    if ($null -eq $editButton) {
        $editButton = Get-ElementByNames `
            -Root $window `
            -Names @('编辑布局', 'Edit layout')
    }
    Invoke-Element -Element $editButton
    Start-Sleep -Milliseconds 500

    if ($PersistenceRecovery) {
        $contentScroll = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'ContentScrollViewer'
    Set-VerticalScrollPercent -Element $contentScroll -Percent 0 -DelayMilliseconds 500
        $timerTitle = Get-ElementByNames `
            -Root $window `
            -Names @('计时器', 'Timer')
        $notesTitle = Get-ElementByNames `
            -Root $window `
            -Names @('便签', 'Notes')
        $timerStart = Get-ElementCenter -Element $timerTitle
        Invoke-MouseDrag `
            -Start $timerStart `
            -End (Get-ElementCenter -Element $notesTitle)
        Start-Sleep -Milliseconds 750
        $timerTitle = Get-ElementByNames `
            -Root $window `
            -Names @('计时器', 'Timer')
        $persistedTimerCenter = Get-ElementCenter -Element $timerTitle
        $cardGridHost = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'CardGridHost'
        if ($null -eq $cardGridHost) {
            throw 'CardGridHost was not exposed for relative layout verification.'
        }
        $persistedTimerRelative = Get-PointRelativeToElement `
            -Point $persistedTimerCenter `
            -Element $cardGridHost
        Write-Output (
            "PERSIST timer-start=$(Format-Point $timerStart) " +
            "committed=$(Format-Point $persistedTimerCenter) " +
            "relative=$(Format-Point $persistedTimerRelative)")
        Assert-Far `
            -Actual $persistedTimerCenter `
            -Expected $timerStart `
            -MinimumDistance 80 `
            -Description 'Timer before persisted layout commit'

        $doneButton = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'EditLayoutButton'
        Invoke-Element -Element $doneButton
        [void](Wait-ForElementName `
            -Root $window `
            -AutomationId 'StatusText' `
            -ExpectedNames @('布局编辑已完成。', 'Layout editing completed.') `
            -Timeout ([TimeSpan]::FromSeconds(15)))

        Stop-OwnedProcess -Process $panelProcess
        $panelProcess = $null
        Stop-OwnedProcess -Process $brokerProcess
        $brokerProcess = $null
        Start-Sleep -Milliseconds 250

        $brokerProcess = [Diagnostics.Process]::Start($brokerStartInfo)
        Start-Sleep -Milliseconds 500
        if ($brokerProcess.HasExited) {
            throw "CoreBroker restart exited with code $($brokerProcess.ExitCode)."
        }
        $panelProcess = [Diagnostics.Process]::Start($startInfo)
        $window = Get-PanelWindow `
            -ProcessId $panelProcess.Id `
            -Timeout ([TimeSpan]::FromSeconds(10))
        $panelWindowHandle = [IntPtr]$window.Current.NativeWindowHandle
        [void][WinWidgetBoardPointerInput]::ShowWindow($panelWindowHandle, 5)
        if (-not [WinWidgetBoardPointerInput]::MakeTopmost($panelWindowHandle)) {
            throw 'Restarted WorkspacePanel could not be made temporarily topmost.'
        }
        [void][WinWidgetBoardPointerInput]::BringWindowToTop($panelWindowHandle)
        [void][WinWidgetBoardPointerInput]::SetForegroundWindow($panelWindowHandle)
        [void](Wait-ForElementName `
            -Root $window `
            -AutomationId 'StatusText' `
            -ExpectedNames @('已加载保存的卡片布局。', 'Saved card layout loaded.') `
            -Timeout ([TimeSpan]::FromSeconds(15)))

        $contentScroll = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'ContentScrollViewer'
    Set-VerticalScrollPercent -Element $contentScroll -Percent 0 -DelayMilliseconds 500
        $cardGridHost = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'CardGridHost'
        if ($null -eq $cardGridHost) {
            throw 'Restarted CardGridHost was not exposed for verification.'
        }
        $stableTimer = Wait-StableRelativeCenter `
            -Root $window `
            -Names @('计时器', 'Timer') `
            -Reference $cardGridHost `
            -Timeout ([TimeSpan]::FromSeconds(10))
        $timerTitle = $stableTimer.Element
        $reloadedTimerCenter = $stableTimer.ScreenPoint
        $reloadedTimerRelative = $stableTimer.RelativePoint
        Write-Output (
            "PERSIST timer-reloaded=$(Format-Point $reloadedTimerCenter) " +
            "relative=$(Format-Point $reloadedTimerRelative)")
        Assert-Near `
            -Actual $reloadedTimerRelative `
            -Expected $persistedTimerRelative `
            -Tolerance 48 `
            -Description 'Timer after persisted layout restart'

        $editButton = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'EditLayoutButton'
        Invoke-Element -Element $editButton
        Start-Sleep -Milliseconds 500
        $resizeButton = Get-Descendants -Root $window |
            Where-Object {
                $_.Current.Name -in @(
                    '缩小卡片',
                    'Make card smaller',
                    '放大卡片',
                    'Make card larger') -and
                $_.Current.IsEnabled -and
                -not $_.Current.IsOffscreen
            } |
            Select-Object -First 1
        if ($null -eq $resizeButton) {
            throw 'No enabled resize button was available after persisted restart.'
        }
        Invoke-Element -Element $resizeButton
        Start-Sleep -Milliseconds 500
        $undoButton = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'UndoLayoutButton'
        if ($null -eq $undoButton -or -not $undoButton.Current.IsEnabled) {
            throw 'Second-round layout edit did not create an undo entry.'
        }
        Invoke-Element -Element $undoButton
        Start-Sleep -Milliseconds 500
        $redoButton = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'RedoLayoutButton'
        if ($null -eq $redoButton -or -not $redoButton.Current.IsEnabled) {
            throw 'Second-round layout undo did not create a redo entry.'
        }

        Write-Output (
            'REAL-LAYOUT-PERSISTENCE-PASS ' +
            'commit+broker-panel-restart+second-edit')
        return
    }

    $contentScroll = Get-ElementByAutomationId `
        -Root $window `
        -AutomationId 'ContentScrollViewer'
    if ($null -eq $contentScroll) {
        throw 'The card surface scroll viewer was not exposed to UI Automation.'
    }

    Set-VerticalScrollPercent -Element $contentScroll -Percent 25 -DelayMilliseconds 500
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('便签', 'Notes') `
        -TargetNames @('计时器', 'Timer') `
        -Mode Handle `
        -ProcessId $panelProcess.Id
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('便签', 'Notes') `
        -TargetNames @('计时器', 'Timer') `
        -Mode Surface `
        -ProcessId $panelProcess.Id

    Set-VerticalScrollPercent -Element $contentScroll -Percent 0 -DelayMilliseconds 500
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('计时器', 'Timer') `
        -TargetNames @('便签', 'Notes') `
        -Mode Handle `
        -ProcessId $panelProcess.Id
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('计时器', 'Timer') `
        -TargetNames @('便签', 'Notes') `
        -Mode Surface `
        -ProcessId $panelProcess.Id

    $placeholderActions = [ordered]@{
        Timer = @('启动计时器', 'Start timer')
        Todo = @('打开待办', 'Open to-do')
        Calendar = @('打开日历', 'Open calendar')
    }
    foreach ($entry in $placeholderActions.GetEnumerator()) {
        $placeholderAction = Get-ElementByNames `
            -Root $window `
            -Names $entry.Value `
            -Optional
        if ($null -ne $placeholderAction) {
            throw "Removed placeholder action for $($entry.Key) was still exposed: '$($placeholderAction.Current.Name)'."
        }
    }
    Write-Output 'STATUS-ACTION-GUARD-PASS timer+todo+calendar actions absent'

    if ($WithBroker) {
        $notesTitle = Get-ElementByNames `
            -Root $window `
            -Names @('便签', 'Notes')
        $notesStart = Get-ElementCenter -Element $notesTitle
        $noteBodyBox = Get-ElementByAutomationId `
            -Root $window `
            -AutomationId 'NoteBodyBox'
        if ($null -eq $noteBodyBox -or
            $noteBodyBox.Current.IsOffscreen -or
            -not $noteBodyBox.Current.IsEnabled) {
            throw 'The visible enabled note editor was unavailable for drag isolation.'
        }

        $noteBodyCenter = Get-ElementCenter -Element $noteBodyBox
        $noteBodyDragEnd = [pscustomobject]@{
            X = $noteBodyCenter.X + 100
            Y = $noteBodyCenter.Y
        }
        Invoke-MouseDrag -Start $noteBodyCenter -End $noteBodyDragEnd
        Start-Sleep -Milliseconds 500
        $notesTitle = Get-ElementByNames `
            -Root $window `
            -Names @('便签', 'Notes')
        Assert-Near `
            -Actual (Get-ElementCenter -Element $notesTitle) `
            -Expected $notesStart `
            -Tolerance 16 `
            -Description 'Notes after text editor drag'
    }

    Set-VerticalScrollPercent -Element $contentScroll -Percent 100 -DelayMilliseconds 500
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('待办', 'To-do') `
        -TargetNames @('本地日历', 'Local calendar') `
        -Mode Handle `
        -ProcessId $panelProcess.Id
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('待办', 'To-do') `
        -TargetNames @('本地日历', 'Local calendar') `
        -Mode Surface `
        -ProcessId $panelProcess.Id
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('本地日历', 'Local calendar') `
        -TargetNames @('待办', 'To-do') `
        -Mode Handle `
        -ProcessId $panelProcess.Id
    Test-CardDragAndUndo `
        -Root $window `
        -SourceNames @('本地日历', 'Local calendar') `
        -TargetNames @('待办', 'To-do') `
        -Mode Surface `
        -ProcessId $panelProcess.Id

    Set-VerticalScrollPercent -Element $contentScroll -Percent 0 -DelayMilliseconds 500
    $timerTitle = Get-ElementByNames `
        -Root $window `
        -Names @('计时器', 'Timer')
    $notesTitle = Get-ElementByNames `
        -Root $window `
        -Names @('便签', 'Notes')
    $timerStart = Get-ElementCenter -Element $timerTitle
    $timerHandle = Get-DragHandleForTitle `
        -Root $window `
        -Title $timerTitle
    Invoke-MouseDragAndCancel `
        -Start (Get-ElementCenter -Element $timerHandle) `
        -End (Get-ElementCenter -Element $notesTitle)
    Start-Sleep -Milliseconds 750
    $timerTitle = Get-ElementByNames `
        -Root $window `
        -Names @('计时器', 'Timer')
    Assert-Near `
        -Actual (Get-ElementCenter -Element $timerTitle) `
        -Expected $timerStart `
        -Tolerance 16 `
        -Description 'Timer after Esc-canceled captured drag'
    $undoButton = Get-ElementByAutomationId `
        -Root $window `
        -AutomationId 'UndoLayoutButton'
    if ($undoButton.Current.IsEnabled) {
        throw 'Esc-canceled drag unexpectedly created a layout history entry.'
    }

    Write-Output (
        'REAL-DRAG-PASS four-cards handles+surfaces+' +
        'interactive-control-isolation+esc-cancel')
    return
}
finally {
    [void][WinWidgetBoardPointerInput]::SetCursorPos(
        $originalCursor.X,
        $originalCursor.Y)
        Stop-OwnedProcess -Process $panelProcess
        Stop-OwnedProcess -Process $brokerProcess
    if ($null -ne $testDataRoot -and
        (Test-Path -LiteralPath $testDataRoot)) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedTestDataRoot = [IO.Path]::GetFullPath($testDataRoot)
        if (-not $resolvedTestDataRoot.StartsWith(
                $temporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to delete test data outside temp: $resolvedTestDataRoot"
        }
        if ($RetainTestData) {
            Write-Output "TEST-DATA-RETAINED $resolvedTestDataRoot"
        }
        else {
            Remove-Item -LiteralPath $resolvedTestDataRoot -Recurse -Force
        }
    }
}
