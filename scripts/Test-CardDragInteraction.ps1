[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',
    [switch]$WithBroker
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
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
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

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
            Math.Clamp(x - left, 0, width - 1) * 65535.0 / (width - 1));
        int normalizedY = (int)Math.Round(
            Math.Clamp(y - top, 0, height - 1) * 65535.0 / (height - 1));
        return SendMouse(
            MouseMove | MouseAbsolute | MouseVirtualDesk,
            normalizedX,
            normalizedY);
    }

    public static bool SendLeftButton(bool pressed)
    {
        return SendMouse(pressed ? MouseLeftDown : MouseLeftUp, 0, 0);
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
$originalCursor = [WinWidgetBoardPointerInput+Point]::new()
[void][WinWidgetBoardPointerInput]::GetCursorPos([ref]$originalCursor)

function Get-PanelWindow {
    param(
        [int]$ProcessId,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
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

function Get-ElementByNames {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Names
    )

    foreach ($element in Get-Descendants -Root $Root) {
        if ($Names -contains $element.Current.Name) {
            return $element
        }
    }

    throw "UI Automation element not found. Expected one of: $($Names -join ', ')."
}

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

function Get-DragHandleForTitle {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.AutomationElement]$Title
    )

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
        Sort-Object {
            $center = Get-ElementCenter -Element $_
            [Math]::Abs($center.X - $titleCenter.X) +
                [Math]::Abs($center.Y - $titleCenter.Y)
        } |
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

try {
    $processNames = @('WinWidgetBoard.WorkspacePanel')
    if ($WithBroker) {
        $processNames += @(
            'WinWidgetBoard.CoreBroker',
            'WinWidgetBoard.LauncherHost')
    }

    $existing = Get-Process -Name $processNames -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        throw 'A WinWidgetBoard process is already running. Close it before the drag interaction test.'
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
            '..\artifacts\bin\WinWidgetBoard.CoreBroker\Release\' +
            'net10.0-windows10.0.26100.0\win-x64\WinWidgetBoard.CoreBroker.exe')
        $brokerPath = [IO.Path]::GetFullPath($brokerPath)
        if (-not (Test-Path -LiteralPath $brokerPath -PathType Leaf)) {
            throw "CoreBroker executable was not found: $brokerPath"
        }

        $sessionToken = [Convert]::ToBase64String(
            [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        ).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $brokerStartInfo = [Diagnostics.ProcessStartInfo]::new($brokerPath)
        $brokerStartInfo.UseShellExecute = $false
        $brokerStartInfo.CreateNoWindow = $true
        [void]$brokerStartInfo.ArgumentList.Add('--session-token')
        [void]$brokerStartInfo.ArgumentList.Add($sessionToken)
        $brokerStartInfo.Environment['DOTNET_ROOT'] = $PortableDotnetRoot
        $brokerStartInfo.Environment['DOTNET_ROOT_X64'] = $PortableDotnetRoot
        $brokerStartInfo.Environment[
            'WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] = $sessionToken
        $brokerProcess = [Diagnostics.Process]::Start($brokerStartInfo)
        Start-Sleep -Milliseconds 500
        if ($brokerProcess.HasExited) {
            throw "CoreBroker exited with code $($brokerProcess.ExitCode)."
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new($panelPath)
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['DOTNET_ROOT'] = $PortableDotnetRoot
    $startInfo.Environment['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    if ($WithBroker) {
        $startInfo.Environment[
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
    [void][WinWidgetBoardPointerInput]::SetForegroundWindow($panelWindowHandle)
    Start-Sleep -Milliseconds 250

    $expectedLayoutStatuses = if ($WithBroker) {
        @(
                '已恢复异常的布局空行；点击“完成”可保存修复后的位置。',
                'Recovered an excessive saved layout gap. Choose Done to save the repaired positions.'
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

    $timerTitle = Get-ElementByNames -Root $window -Names @('计时器', 'Timer')
    $calendarTitle = Get-ElementByNames `
        -Root $window `
        -Names @('本地日历', 'Local calendar')
    $timerStart = Get-ElementCenter -Element $timerTitle
    $calendarStart = Get-ElementCenter -Element $calendarTitle
    $timerHandle = Get-DragHandleForTitle -Root $window -Title $timerTitle
    $calendarHandle = Get-DragHandleForTitle -Root $window -Title $calendarTitle
    $timerHandleStart = Get-ElementCenter -Element $timerHandle
    $calendarHandleStart = Get-ElementCenter -Element $calendarHandle
    Write-Output (
        "BEFORE timerTitle=$(Format-Point $timerStart) " +
        "calendarTitle=$(Format-Point $calendarStart) " +
        "timerHandle=$(Format-Point $timerHandleStart) " +
        "calendarHandle=$(Format-Point $calendarHandleStart)")
    $statusElement = Get-ElementByAutomationId `
        -Root $window `
        -AutomationId 'StatusText'
    if ($null -ne $statusElement) {
        Write-Output "STATUS before drag: $($statusElement.Current.Name)"
    }
    [void][WinWidgetBoardPointerInput]::SetForegroundWindow($panelWindowHandle)
    Start-Sleep -Milliseconds 100
    Assert-PanelOwnsPoint `
        -Point $timerHandleStart `
        -ProcessId $panelProcess.Id `
        -Description 'Timer drag handle'
    Assert-PanelOwnsPoint `
        -Point $calendarHandleStart `
        -ProcessId $panelProcess.Id `
        -Description 'Calendar drag target'

    Invoke-MouseDrag `
        -Start $timerHandleStart `
        -End $calendarHandleStart
    Start-Sleep -Milliseconds 750

    $timerTitle = Get-ElementByNames -Root $window -Names @('计时器', 'Timer')
    $calendarTitle = Get-ElementByNames `
        -Root $window `
        -Names @('本地日历', 'Local calendar')
    $timerAfterFirstDrag = Get-ElementCenter -Element $timerTitle
    $calendarAfterFirstDrag = Get-ElementCenter -Element $calendarTitle
    Write-Output (
        "AFTER timerTitle=$(Format-Point $timerAfterFirstDrag) " +
        "calendarTitle=$(Format-Point $calendarAfterFirstDrag)")
    if ($null -ne $statusElement) {
        Write-Output "STATUS after drag: $($statusElement.Current.Name)"
    }
    Assert-Near `
        -Actual $timerAfterFirstDrag `
        -Expected $calendarStart `
        -Tolerance 48 `
        -Description 'Timer'
    Assert-Near `
        -Actual $calendarAfterFirstDrag `
        -Expected $timerStart `
        -Tolerance 48 `
        -Description 'Calendar'

    $timerHandle = Get-DragHandleForTitle -Root $window -Title $timerTitle
    $calendarHandle = Get-DragHandleForTitle -Root $window -Title $calendarTitle
    Invoke-MouseDrag `
        -Start (Get-ElementCenter -Element $calendarHandle) `
        -End (Get-ElementCenter -Element $timerHandle)
    Start-Sleep -Milliseconds 750

    $timerTitle = Get-ElementByNames -Root $window -Names @('计时器', 'Timer')
    $calendarTitle = Get-ElementByNames `
        -Root $window `
        -Names @('本地日历', 'Local calendar')
    Assert-Near `
        -Actual (Get-ElementCenter -Element $timerTitle) `
        -Expected $timerStart `
        -Tolerance 48 `
        -Description 'Timer after reverse drag'
    Assert-Near `
        -Actual (Get-ElementCenter -Element $calendarTitle) `
        -Expected $calendarStart `
        -Tolerance 48 `
        -Description 'Calendar after reverse drag'

    $timerSurfaceStart = Get-ElementCenter -Element $timerTitle
    $calendarSurfaceStart = Get-ElementCenter -Element $calendarTitle
    Assert-PanelOwnsPoint `
        -Point $timerSurfaceStart `
        -ProcessId $panelProcess.Id `
        -Description 'Timer card surface'
    Assert-PanelOwnsPoint `
        -Point $calendarSurfaceStart `
        -ProcessId $panelProcess.Id `
        -Description 'Calendar card target'
    Invoke-MouseDrag `
        -Start $timerSurfaceStart `
        -End $calendarSurfaceStart
    Start-Sleep -Milliseconds 750

    $timerTitle = Get-ElementByNames -Root $window -Names @('计时器', 'Timer')
    $calendarTitle = Get-ElementByNames `
        -Root $window `
        -Names @('本地日历', 'Local calendar')
    Assert-Near `
        -Actual (Get-ElementCenter -Element $timerTitle) `
        -Expected $calendarStart `
        -Tolerance 48 `
        -Description 'Timer after surface drag'
    Assert-Near `
        -Actual (Get-ElementCenter -Element $calendarTitle) `
        -Expected $timerStart `
        -Tolerance 48 `
        -Description 'Calendar after timer surface drag'

    Invoke-MouseDrag `
        -Start (Get-ElementCenter -Element $calendarTitle) `
        -End (Get-ElementCenter -Element $timerTitle)
    Start-Sleep -Milliseconds 750

    $timerTitle = Get-ElementByNames -Root $window -Names @('计时器', 'Timer')
    $calendarTitle = Get-ElementByNames `
        -Root $window `
        -Names @('本地日历', 'Local calendar')
    Assert-Near `
        -Actual (Get-ElementCenter -Element $timerTitle) `
        -Expected $timerStart `
        -Tolerance 48 `
        -Description 'Timer after reverse surface drag'
    Assert-Near `
        -Actual (Get-ElementCenter -Element $calendarTitle) `
        -Expected $calendarStart `
        -Tolerance 48 `
        -Description 'Calendar after reverse surface drag'

    Write-Output 'REAL-DRAG-PASS timer<->calendar handles+surfaces'
}
finally {
    [void][WinWidgetBoardPointerInput]::SetCursorPos(
        $originalCursor.X,
        $originalCursor.Y)
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
}
