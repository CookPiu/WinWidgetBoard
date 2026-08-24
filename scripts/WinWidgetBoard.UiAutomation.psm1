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

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(WindowPoint point);

    // Which process owns the window under a screen point. A layered or region-clipped window
    // that lets input through reports the window underneath instead, which is what callers
    // asserting pass-through need to see.
    public static uint ProcessIdAtPoint(int x, int y)
    {
        var point = new WindowPoint();
        point.X = x;
        point.Y = y;
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return 0;
        }

        uint owner;
        GetWindowThreadProcessId(window, out owner);
        return owner;
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string className, string windowName);

    // Wrapped because PowerShell binds $null to a string parameter as the empty string,
    // which asks FindWindow for a window whose title is empty rather than for any title.
    public static IntPtr FindWindowByClass(string className)
    {
        return FindWindowW(className, null);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private const uint GwHwndNext = 2;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    // Which of two windows is nearer the front. Returns 1 when `first` is above `second`,
    // -1 when `second` is above `first`, and 0 when neither is in the top-level z-order.
    // Walking the order is the only reliable comparison: both windows are topmost, so
    // neither style bits nor a hit test at a point can say which one wins.
    public static int CompareZOrder(IntPtr first, IntPtr second)
    {
        IntPtr current = GetTopWindow(IntPtr.Zero);
        while (current != IntPtr.Zero)
        {
            if (current == first)
            {
                return 1;
            }
            if (current == second)
            {
                return -1;
            }
            current = GetWindow(current, GwHwndNext);
        }
        return 0;
    }

    // Exactly what Explorer does to the taskbar while it handles an activation: re-inserts
    // it at the top of the topmost band without moving, resizing or activating it.
    public static bool RaiseToTopmost(IntPtr window)
    {
        return SetWindowPos(
            window,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    // PW_RENDERFULLCONTENT. Without it a DWM-composited window renders blank.
    private const uint PrintWindowFullContent = 0x00000002;

    // Copies a window's own content, so a capture does not depend on the window being
    // focused or unobscured. Screen-region capture cannot give that: whatever sits on top
    // is what lands in the bitmap, and a resident panel that hides on focus loss makes
    // "just bring it to the front first" unreliable.
    public static bool PrintWindowToDc(IntPtr window, IntPtr deviceContext)
    {
        return PrintWindow(window, deviceContext, PrintWindowFullContent);
    }

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

    // SendInput takes absolute coordinates normalized across the whole virtual desktop.
    private static bool TryNormalize(int x, int y, out int normalizedX, out int normalizedY)
    {
        normalizedX = 0;
        normalizedY = 0;

        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 1 || height <= 1)
        {
            return false;
        }

        normalizedX = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        normalizedY = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        return true;
    }

    public static bool SendMouseMove(int x, int y)
    {
        int normalizedX;
        int normalizedY;
        if (!TryNormalize(x, y, out normalizedX, out normalizedY))
        {
            return false;
        }

        uint absolute = MouseMove | MouseAbsolute | MouseVirtualDesk;
        var move = new Input[] { CreateMouseInput(absolute, normalizedX, normalizedY) };
        return SendInput(1, move, Marshal.SizeOf<Input>()) == 1;
    }

    public static bool SendLeftClick(int x, int y)
    {
        int normalizedX;
        int normalizedY;
        if (!TryNormalize(x, y, out normalizedX, out normalizedY))
        {
            return false;
        }

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

# A UI Automation query can fail transiently when any provider in the searched scope is
# torn down, restarted or busy: the client sees RPC_E_SERVERFAULT, a disconnected proxy or
# UIA_E_ELEMENTNOTAVAILABLE. Those are retryable; anything else is a real failure.
$script:TransientUiaHResults = @(
    0x80010105, # RPC_E_SERVERFAULT
    0x80010108, # RPC_E_DISCONNECTED
    0x800706BA, # RPC_S_SERVER_UNAVAILABLE
    0x800706BE, # RPC_S_CALL_FAILED
    0x80040201  # UIA_E_ELEMENTNOTAVAILABLE
)

function Test-TransientUiaFault {
    param(
        [Management.Automation.ErrorRecord]$ErrorRecord
    )

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if ($exception -is [System.Windows.Automation.ElementNotAvailableException]) {
            return $true
        }

        # Compare the HResult as-is. PowerShell parses an 8-digit hex literal such as
        # 0x80040201 as a signed Int32, which is exactly what Exception.HResult holds,
        # so the table above already matches. The previous [uint32] normalisation threw
        # InvalidArgument on every negative HResult and replaced the real UI Automation
        # failure with a type-conversion error; normalising the other way (0xFFFFFFFFL)
        # would instead silently stop matching and drop every retry.
        if ($script:TransientUiaHResults -contains $exception.HResult) {
            return $true
        }

        $exception = $exception.InnerException
    }

    return $false
}

function Invoke-UiaQuery {
    param(
        [scriptblock]$Query,
        [int]$MaxAttempts = 5
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            return & $Query
        }
        catch {
            if ($attempt -ge $MaxAttempts -or -not (Test-TransientUiaFault -ErrorRecord $_)) {
                throw
            }

            Start-Sleep -Milliseconds (50 * $attempt)
        }
    }
}

function Get-PanelWindow {
    param(
        [int]$ProcessId,
        [TimeSpan]$Timeout
    )

    # The panel process owns more than one top-level window, and only one of them holds the
    # XAML tree. Taking the first match by process ID picks whichever the UIA root happens to
    # list first, which is sometimes an empty host window - the caller then sees a window that
    # exists but has no descendants at all. Prefer a window that actually has children, and
    # fall back to the first one only after the timeout.
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $deadline = [DateTime]::UtcNow + $Timeout
    $firstSeen = $null
    do {
        $windows = Invoke-UiaQuery -Query {
            [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                $condition)
        }

        foreach ($window in $windows) {
            if ($null -eq $firstSeen) {
                $firstSeen = $window
            }

            $child = Invoke-UiaQuery -Query {
                $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Children,
                    [System.Windows.Automation.Condition]::TrueCondition)
            }
            if ($null -ne $child) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($null -ne $firstSeen) {
        return $firstSeen
    }

    throw "WorkspacePanel window did not appear within $($Timeout.TotalSeconds) seconds."
}

function Get-Descendants {
    param(
        [System.Windows.Automation.AutomationElement]$Root
    )

    return Invoke-UiaQuery -Query {
        $Root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
    }
}

function Get-ElementByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return Invoke-UiaQuery -Query {
        $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    }
}

function Get-ElementByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return Invoke-UiaQuery -Query {
        $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    }
}

function Get-ElementByNames {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string[]]$Names,
        [switch]$Optional
    )

    foreach ($element in Get-Descendants -Root $Root) {
        # An element can disappear between the enumeration and the property read.
        try {
            $name = $element.Current.Name
        }
        catch {
            if (Test-TransientUiaFault -ErrorRecord $_) {
                continue
            }

            throw
        }

        if ($Names -contains $name) {
            return $element
        }
    }

    if ($Optional) {
        return $null
    }

    throw "UI Automation element not found. Expected one of: $($Names -join ', ')."
}

# WinUI hosts a ContentDialog in a popup that can surface as its own top-level UIA window
# rather than a descendant of the panel HWND. Searching from the desktop root would walk
# every other application's tree - one faulting provider anywhere on the desktop then takes
# the whole run down. Enumerate only the top-level windows this process owns instead:
# Children scope on the desktop root never descends into another application.
function Get-ProcessWindows {
    param(
        [int]$ProcessId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return Invoke-UiaQuery -Query {
        [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    }
}

function Wait-ProcessWindowContaining {
    param(
        [int]$ProcessId,
        [string]$AutomationId,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        foreach ($window in Get-ProcessWindows -ProcessId $ProcessId) {
            $element = Get-ElementByAutomationId -Root $window -AutomationId $AutomationId
            if ($null -ne $element) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw ("No window of process $ProcessId exposed '$AutomationId' within " +
        "$($Timeout.TotalSeconds) seconds.")
}

function Wait-ProcessElementGone {
    param(
        [int]$ProcessId,
        [string]$AutomationId,
        [TimeSpan]$Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    do {
        $found = $false
        foreach ($window in Get-ProcessWindows -ProcessId $ProcessId) {
            if ($null -ne (Get-ElementByAutomationId -Root $window -AutomationId $AutomationId)) {
                $found = $true
                break
            }
        }

        if (-not $found) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw ("'$AutomationId' was still present in process $ProcessId after " +
        "$($Timeout.TotalSeconds) seconds.")
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

function Assert-ElementHiddenByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Because = 'must not be visible in this state'
    )

    $element = Get-ElementByAutomationId -Root $Root -AutomationId $AutomationId
    if ($null -ne $element -and -not $element.Current.IsOffscreen) {
        throw "UI Automation element '$AutomationId' $Because."
    }
}

# Flips a check box and returns the state it landed in. Toggling through the pattern rather
# than clicking keeps the caller off pixel coordinates for a control that a virtualizing list
# may have moved since it was found.
function Invoke-ElementToggle {
    param(
        [System.Windows.Automation.AutomationElement]$Element
    )

    if (-not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen) {
        throw "UI Automation element is not toggleable: $($Element.Current.Name)"
    }

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern,
            [ref]$pattern)) {
        throw "TogglePattern unavailable: $($Element.Current.Name)"
    }

    ([System.Windows.Automation.TogglePattern]$pattern).Toggle()
    return $Element.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
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

function Get-TaskbarWindowHandle {
    $handle = [WinWidgetBoardUiAutomationInput]::FindWindowByClass('Shell_TrayWnd')
    if ($handle -eq [IntPtr]::Zero) {
        throw 'The primary taskbar window (Shell_TrayWnd) was not found.'
    }

    return $handle
}

# Re-stacks the taskbar the way Explorer does on activation, then reports whether the given
# window is still in front of it.
function Test-WindowStaysAboveTaskbar {
    param(
        [Parameter(Mandatory)]
        [IntPtr]$Handle,

        [int]$Attempts = 5,

        [int]$SettleMilliseconds = 150
    )

    $taskbar = Get-TaskbarWindowHandle
    $covered = 0
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not [WinWidgetBoardUiAutomationInput]::RaiseToTopmost($taskbar)) {
            $raiseError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "SetWindowPos(HWND_TOPMOST) on the taskbar failed (Win32 error $raiseError)."
        }

        Start-Sleep -Milliseconds $SettleMilliseconds
        $relation = [WinWidgetBoardUiAutomationInput]::CompareZOrder($Handle, $taskbar)
        if ($relation -eq 0) {
            throw 'Neither the window nor the taskbar is in the top-level z-order.'
        }
        if ($relation -lt 0) {
            $covered++
        }
    }

    return [pscustomobject]@{
        Attempts = $Attempts
        Covered = $covered
        Taskbar = $taskbar
    }
}

function Get-WindowOwnerProcessAtPoint {
    param(
        [int]$X,
        [int]$Y
    )

    return [int][WinWidgetBoardUiAutomationInput]::ProcessIdAtPoint($X, $Y)
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

function Move-PointerToPoint {
    param(
        [int]$X,
        [int]$Y
    )

    if (-not [WinWidgetBoardUiAutomationInput]::SendMouseMove($X, $Y)) {
        throw "SendInput failed while moving the pointer to ($X, $Y)."
    }
}

# Captures one window's own content to a PNG, regardless of z-order or focus. Prefer this
# over Save-ScreenRegionCapture whenever the target is a window this session owns.
function Save-WindowCapture {
    param(
        [Parameter(Mandatory = $true)][IntPtr]$WindowHandle,
        [Parameter(Mandatory = $true)][string]$Path
    )

    Add-Type -AssemblyName System.Drawing
    $rect = New-Object 'WinWidgetBoardUiAutomationInput+WindowRect'
    if (-not [WinWidgetBoardUiAutomationInput]::GetWindowRect($WindowHandle, [ref]$rect)) {
        throw "GetWindowRect failed for window $WindowHandle."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "Window $WindowHandle has no drawable area."
    }

    $bitmap = New-Object 'System.Drawing.Bitmap' $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                if (-not [WinWidgetBoardUiAutomationInput]::PrintWindowToDc(
                        $WindowHandle, $deviceContext)) {
                    throw "PrintWindow failed for window $WindowHandle."
                }
            }
            finally {
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    return $Path
}

# Captures a screen rectangle to a PNG. Used to review how a surface actually renders when
# no automation property describes it - colour, antialiasing, elevation and motion states.
function Save-ScreenRegionCapture {
    param(
        [Parameter(Mandatory = $true)][int]$Left,
        [Parameter(Mandatory = $true)][int]$Top,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height,
        [Parameter(Mandatory = $true)][string]$Path
    )

    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object 'System.Drawing.Bitmap' $Width, $Height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($Left, $Top, 0, 0, (New-Object 'System.Drawing.Size' $Width, $Height))
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    return $Path
}

# A managed project writes to two different paths depending on whether Platform was passed
# explicitly, so the same executable can exist twice with different ages. Scripts that pick
# one path by hand end up verifying a stale build - which looks exactly like a product bug.
# Take the freshest of the two and say which one was chosen.
function Resolve-ManagedOutput {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName,
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [string]$Configuration = 'Release',
        [string]$Platform = 'x64',
        [string]$TargetFramework = 'net10.0-windows10.0.26100.0',
        [string]$RuntimeIdentifier = 'win-x64'
    )

    $candidates = @(
        (Join-Path $RepositoryRoot ("artifacts\bin\$ProjectName\$Platform\$Configuration\" +
            "$TargetFramework\$RuntimeIdentifier\$ExecutableName")),
        (Join-Path $RepositoryRoot ("artifacts\bin\$ProjectName\$Configuration\" +
            "$TargetFramework\$RuntimeIdentifier\$ExecutableName"))
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }

    $existing = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($existing.Count -eq 0) {
        throw ("Neither build output exists for ${ProjectName}:`n  " +
            ($candidates -join "`n  "))
    }

    $chosen = $existing |
        Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1

    if ($existing.Count -gt 1) {
        $age = (Get-Item -LiteralPath $chosen).LastWriteTime
        # Write-Host, not Write-Output: this function returns a path, and anything written
        # to the output stream would be concatenated into that return value.
        Write-Host "BUILD-OUTPUT $ProjectName -> $chosen (built $age)"
    }

    return $chosen
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
    'Get-ProcessWindows',
    'Wait-ProcessWindowContaining',
    'Wait-ProcessElementGone',
    'Get-Descendants',
    'Get-ElementByAutomationId',
    'Get-ElementByName',
    'Get-ElementByNames',
    'Wait-VisibleElementByAutomationId',
    'Assert-ElementHiddenByAutomationId',
    'Wait-VisibleElementByName',
    'Wait-ElementValue',
    'Get-ElementText',
    'Set-ElementValue',
    'Set-TextValue',
    'Invoke-Element',
    'Invoke-ElementToggle',
    'Set-VerticalScrollPercent',
    'Focus-PanelWindow',
    'Get-WindowRectByClass',
    'Get-WindowOwnerProcessAtPoint',
    'Get-TaskbarWindowHandle',
    'Test-WindowStaysAboveTaskbar',
    'Invoke-LeftClickAtPoint',
    'Move-PointerToPoint',
    'Save-ScreenRegionCapture',
    'Save-WindowCapture',
    'Resolve-ManagedOutput',
    'Stop-WinWidgetBoardProcess',
    'Stop-TestProcess',
    'Stop-OwnedProcess',
    'Start-TestBroker',
    'Start-TestPanel'
)
