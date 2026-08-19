# Shared UI Automation and acceptance-process helpers for WinWidgetBoard scripts.
# Keep product-specific assertions and input injection in the calling script.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('WinWidgetBoardUiAutomationInput' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinWidgetBoardUiAutomationInput
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

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // x64 INPUT is DWORD type + 4 bytes of padding + the 32-byte MOUSEINPUT union member,
    // i.e. 40 bytes. Extra trailing fields would make Marshal.SizeOf disagree with the
    // cbSize SendInput expects, and SendInput would silently fail.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

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

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    // FindWindowW resolves a class name through the calling process's atom table, so it
    // cannot see a class another process registered without CS_GLOBALCLASS - which is how
    // the LauncherHost entry window is registered. Enumerate instead.
    public static IntPtr FindVisibleWindowByClass(string className, uint processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            if (processId != 0)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId)
                {
                    return true;
                }
            }

            var buffer = new StringBuilder(256);
            if (GetClassNameW(window, buffer, buffer.Capacity) == 0 ||
                !string.Equals(buffer.ToString(), className, StringComparison.Ordinal))
            {
                return true;
            }

            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static Input CreateMouseInput(uint flags, int normalizedX, int normalizedY)
    {
        var input = new Input();
        input.Type = InputMouse;
        input.Mouse.Dx = normalizedX;
        input.Mouse.Dy = normalizedY;
        input.Mouse.Flags = flags;
        return input;
    }

    public static bool SendLeftClick(int x, int y)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 1 || height <= 1)
        {
            return false;
        }

        int normalizedX = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        int normalizedY = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        uint absolute = MouseMove | MouseAbsolute | MouseVirtualDesk;
        var move = new Input[] { CreateMouseInput(absolute, normalizedX, normalizedY) };
        if (SendInput(1, move, Marshal.SizeOf<Input>()) != 1)
        {
            return false;
        }

        var down = new Input[]
        {
            CreateMouseInput(absolute | MouseLeftDown, normalizedX, normalizedY),
        };
        if (SendInput(1, down, Marshal.SizeOf<Input>()) != 1)
        {
            return false;
        }

        var up = new Input[]
        {
            CreateMouseInput(absolute | MouseLeftUp, normalizedX, normalizedY),
        };
        return SendInput(1, up, Marshal.SizeOf<Input>()) == 1;
    }
}
'@
}

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

function Get-ElementByNames {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Names,
        [switch]$Optional
    )

    foreach ($element in Get-Descendants -Root $Root) {
        if ($Names -contains $element.Current.Name) {
            return $element
        }
    }

    if ($Optional) {
        return $null
    }

    throw "UI Automation element not found. Expected one of: $($Names -join ', ')."
}

function Wait-VisibleElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [TimeSpan]$Timeout,
        [switch]$Enabled,
        [System.Windows.Automation.AutomationElement]$ScrollContainer
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastState = 'not found'
    do {
        $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            $lastState = "offscreen=$($element.Current.IsOffscreen); enabled=$($element.Current.IsEnabled)"
            if (-not $element.Current.IsOffscreen -and
                (-not $Enabled -or $element.Current.IsEnabled)) {
                return $element
            }

            if ($null -ne $ScrollContainer) {
                Set-VerticalScrollPercent -Element $ScrollContainer -Percent 100
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Visible UI Automation element not found: $AutomationId. Last state: $lastState"
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

function Wait-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Expected,
        [TimeSpan]$Timeout,
        [switch]$Enabled,
        [System.Windows.Automation.AutomationElement]$ScrollContainer
    )

    $element = Wait-VisibleElementByAutomationId `
        -Root $Root `
        -AutomationId $AutomationId `
        -Timeout $Timeout `
        -Enabled:$Enabled `
        -ScrollContainer $ScrollContainer
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

    throw ("UI Automation value did not become expected: {0}. " +
        "Expected='{1}', Actual='{2}'") -f
        $AutomationId,
        $Expected,
        $actual
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

function Set-ElementValue {
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

function Set-TextValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    Set-ElementValue -Element $Element -Value $Value
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

function Set-VerticalScrollPercent {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [double]$Percent,
        [int]$DelayMilliseconds = 300
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern,
            [ref]$pattern)) {
        throw "ScrollPattern unavailable: $($Element.Current.Name)"
    }

    $scroll = [System.Windows.Automation.ScrollPattern]$pattern
    if ($scroll.Current.VerticallyScrollable) {
        $scroll.SetScrollPercent(
            [System.Windows.Automation.ScrollPattern]::NoScroll,
            $Percent)
        if ($DelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Focus-PanelWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Window
    )

    $windowHandle = [IntPtr]$Window.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'WorkspacePanel returned an invalid native window handle.'
    }

    [void][WinWidgetBoardUiAutomationInput]::ShowWindow($windowHandle, 5)
    [void][WinWidgetBoardUiAutomationInput]::BringWindowToTop($windowHandle)
    [void][WinWidgetBoardUiAutomationInput]::SetActiveWindow($windowHandle)
    [void][WinWidgetBoardUiAutomationInput]::SetForegroundWindow($windowHandle)
    try {
        $Window.SetFocus()
    }
    catch {
    }
}

function Get-WindowRectByClass {
    param(
        [string]$ClassName,
        [int]$ProcessId = 0,
        [TimeSpan]$Timeout = ([TimeSpan]::FromSeconds(10))
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $handle = [WinWidgetBoardUiAutomationInput]::FindVisibleWindowByClass(
            $ClassName,
            [uint32]$ProcessId)
        if ($handle -ne [IntPtr]::Zero) {
            $rect = New-Object 'WinWidgetBoardUiAutomationInput+WindowRect'
            if ([WinWidgetBoardUiAutomationInput]::GetWindowRect($handle, [ref]$rect)) {
                return [pscustomobject]@{
                    Handle = $handle
                    Left = $rect.Left
                    Top = $rect.Top
                    Right = $rect.Right
                    Bottom = $rect.Bottom
                    CenterX = [int](($rect.Left + $rect.Right) / 2)
                    CenterY = [int](($rect.Top + $rect.Bottom) / 2)
                }
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Window class '$ClassName' did not become visible within $($Timeout.TotalSeconds) seconds."
}

function Invoke-LeftClickAtPoint {
    param(
        [int]$X,
        [int]$Y
    )

    if (-not [WinWidgetBoardUiAutomationInput]::SendLeftClick($X, $Y)) {
        throw "SendInput failed while clicking ($X, $Y)."
    }
}

function Stop-WinWidgetBoardProcess {
    param(
        [Diagnostics.Process]$Process,
        [switch]$Graceful
    )

    if ($null -eq $Process) {
        return
    }

    try {
        if (-not $Process.HasExited) {
            if ($Graceful) {
                [void]$Process.CloseMainWindow()
                if (-not $Process.WaitForExit(3000)) {
                    $Process.Kill()
                }
            }
            else {
                $Process.Kill()
            }

            [void]$Process.WaitForExit(5000)
        }
    }
    catch [InvalidOperationException] {
    }
}

function Stop-TestProcess {
    param(
        [Diagnostics.Process]$Process
    )

    Stop-WinWidgetBoardProcess -Process $Process
}

function Stop-OwnedProcess {
    param(
        [Diagnostics.Process]$Process
    )

    Stop-WinWidgetBoardProcess -Process $Process -Graceful
}

function Start-TestBroker {
    param(
        [string]$BrokerPath,
        [string]$TestDataRoot,
        [string]$SessionToken
    )

    $process = Start-Process `
        -FilePath $BrokerPath `
        -ArgumentList @(
            '--session-token',
            $SessionToken,
            '--acceptance-test',
            '--test-instance-id',
            [Guid]::NewGuid().ToString('N'),
            '--data-directory',
            $TestDataRoot) `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        throw "CoreBroker failed to start. Exit code: $($process.ExitCode)."
    }

    return $process
}

function Start-TestPanel {
    param(
        [string]$PanelPath,
        [string]$PortableDotnetRoot,
        [string]$SessionToken,
        [TimeSpan]$WindowTimeout = ([TimeSpan]::FromSeconds(15))
    )

    $panelInfo = [Diagnostics.ProcessStartInfo]::new($PanelPath)
    $panelInfo.UseShellExecute = $false
    $acceptanceInstanceId = [Guid]::NewGuid().ToString('N')
    $panelInfo.Arguments =
        "--acceptance-test --test-instance-id $acceptanceInstanceId"
    $panelInfo.EnvironmentVariables['DOTNET_ROOT'] = $PortableDotnetRoot
    $panelInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $PortableDotnetRoot
    if ($null -ne $env:LOCALAPPDATA) {
        $panelInfo.EnvironmentVariables['LOCALAPPDATA'] = $env:LOCALAPPDATA
    }
    $panelInfo.EnvironmentVariables['WINWIDGETBOARD_COREBROKER_SESSION_TOKEN'] =
        $SessionToken
    $process = [Diagnostics.Process]::Start($panelInfo)
    $window = Get-PanelWindow -ProcessId $process.Id -Timeout $WindowTimeout
    Focus-PanelWindow -Window $window
    return [pscustomobject]@{
        Process = $process
        Window = $window
    }
}

Export-ModuleMember -Function @(
    'Get-PanelWindow',
    'Get-Descendants',
    'Get-ElementByAutomationId',
    'Get-ElementByName',
    'Get-ElementByNames',
    'Wait-VisibleElementByAutomationId',
    'Wait-VisibleElementByName',
    'Wait-ElementValue',
    'Get-ElementText',
    'Set-ElementValue',
    'Set-TextValue',
    'Invoke-Element',
    'Set-VerticalScrollPercent',
    'Focus-PanelWindow',
    'Get-WindowRectByClass',
    'Invoke-LeftClickAtPoint',
    'Stop-WinWidgetBoardProcess',
    'Stop-TestProcess',
    'Stop-OwnedProcess',
    'Start-TestBroker',
    'Start-TestPanel'
)
