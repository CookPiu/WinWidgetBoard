<#
.SYNOPSIS
    Local development entry point: environment check, build, test, and an isolated
    CoreBroker + WorkspacePanel session for ad-hoc manual testing.

.DESCRIPTION
    Wraps the version-locked toolchain rules from docs/development/toolchain.md and
    the acceptance isolation rules from docs/09-security-privacy.md section 10, so a
    temporary test does not need the portable SDK, output layouts, and isolation
    switches re-derived each time.

    Developer tooling only: it is not shipped and does not change product behavior.

    The Run task always uses an isolated temporary data directory and a distinct
    instance identity. Production data is read-only for review and is never used as
    a test fixture. Use scripts/Run-WinWidgetBoard.ps1 for the full real flow that
    includes the taskbar entry.

.EXAMPLE
    .\scripts\Start-DevSandbox.ps1
    Environment check only: SDK version, MSBuild availability, and built artifacts.

.EXAMPLE
    .\scripts\Start-DevSandbox.ps1 -Task Test -Filter "FullyQualifiedName~ResponsiveGridLayoutTests"

.EXAMPLE
    .\scripts\Start-DevSandbox.ps1 -Task Run
    Start the broker and panel against sandbox data, hold until Enter, then clean up.
#>
[CmdletBinding()]
param(
    [ValidateSet('Env', 'Build', 'Test', 'Run')]
    [string]$Task = 'Env',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Platform = 'x64',

    [string]$PortableDotnetRoot = 'C:\tmp\winwidgetboard-dotnet-10.0.302',

    [string]$Filter,

    [switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = 'net10.0-windows10.0.26100.0'
$runtimeIdentifier = 'win-x64'

function Initialize-DotnetEnvironment {
    param([string]$Root)

    $dotnetExe = Join-Path $Root 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetExe)) {
        throw @"
Portable .NET SDK not found: $dotnetExe
Pass -PortableDotnetRoot with the correct location.
Do not delete global.json, enable roll-forward, or fall back to another SDK
(docs/development/toolchain.md section 4).
"@
    }

    $env:DOTNET_ROOT = $Root
    $env:DOTNET_ROOT_X64 = $Root
    if ($env:PATH -notlike "$Root;*") {
        $env:PATH = "$Root;$env:PATH"
    }

    $pinnedVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw |
        ConvertFrom-Json).sdk.version
    $versionOutput = & $dotnetExe --version
    if ($LASTEXITCODE -ne 0) {
        throw @"
'$dotnetExe --version' failed with exit code $LASTEXITCODE.
global.json pins SDK $pinnedVersion with rollForward disabled, so this is the
expected failure when that exact SDK is absent. Install it instead of relaxing
the lock (docs/development/toolchain.md section 4).
"@
    }

    $actualVersion = ($versionOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Last 1).ToString().Trim()
    if ($actualVersion -ne $pinnedVersion) {
        throw @"
global.json pins SDK $pinnedVersion but $dotnetExe reports $actualVersion.
Install the pinned SDK instead of relaxing the lock.
"@
    }

    return [pscustomobject]@{
        Exe = $dotnetExe
        Version = $actualVersion
    }
}

function Get-MsBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        return $null
    }

    try {
        $found = & $vswhere -latest -products * `
            -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null |
            Select-Object -First 1
    }
    catch {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($found)) {
        return $null
    }

    return $found
}

# Managed output gains an extra platform segment when Platform is passed explicitly
# to MSBuild and lacks it otherwise, so the same executable can live at two paths.
# Probe both instead of trusting one layout.
function Resolve-ProductExecutable {
    param(
        [ValidateSet('CoreBroker', 'WorkspacePanel', 'LauncherHost')]
        [string]$Component
    )

    $binRoot = Join-Path $repoRoot 'artifacts\bin'
    $projectName = "WinWidgetBoard.$Component"

    if ($Component -eq 'LauncherHost') {
        $candidates = @("$projectName\$Configuration\$Platform\$projectName.exe")
    }
    else {
        $candidates = @(
            "$projectName\$Platform\$Configuration\$targetFramework\$runtimeIdentifier\$projectName.exe",
            "$projectName\$Configuration\$targetFramework\$runtimeIdentifier\$projectName.exe")
    }

    foreach ($candidate in $candidates) {
        $fullPath = Join-Path $binRoot $candidate
        if (Test-Path -LiteralPath $fullPath) {
            return $fullPath
        }
    }

    return $null
}

function Assert-NoRunningInstance {
    # An acceptance broker still serves the production pipe name, so it cannot
    # coexist with an already running instance.
    $running = Get-Process -Name @(
        'WinWidgetBoard.CoreBroker',
        'WinWidgetBoard.WorkspacePanel',
        'WinWidgetBoard.LauncherHost') -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $text = ($running | ForEach-Object { '{0}#{1}' -f $_.ProcessName, $_.Id }) -join ', '
        throw "Existing WinWidgetBoard processes detected: $text. Close them normally first."
    }
}

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# docs/09-security-privacy.md section 10: verify the path is inside the system
# temporary directory before deleting anything.
function Remove-SandboxDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath -eq $temporaryRoot -or
        -not $fullPath.StartsWith("$temporaryRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Refusing to delete a path outside the system temporary directory: $fullPath"
        return
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction SilentlyContinue
}

function Show-Environment {
    param([pscustomobject]$Dotnet)

    Write-Host ''
    Write-Host "Repository      $repoRoot"
    Write-Host "Configuration   $Configuration / $Platform"
    Write-Host "dotnet          $($Dotnet.Version)  ($($Dotnet.Exe))"

    $msbuild = Get-MsBuildPath
    if ($null -eq $msbuild) {
        Write-Host 'MSBuild         not found - LauncherHost (C++) cannot be built here'
    }
    else {
        Write-Host "MSBuild         $msbuild"
    }

    Write-Host ''
    Write-Host 'Built executables:'
    foreach ($component in @('CoreBroker', 'WorkspacePanel', 'LauncherHost')) {
        $path = Resolve-ProductExecutable -Component $component
        if ($null -eq $path) {
            Write-Host ("  {0,-15} not built" -f $component)
        }
        else {
            Write-Host ("  {0,-15} {1}" -f $component, $path)
        }
    }

    Write-Host ''
}

function Invoke-Build {
    param([pscustomobject]$Dotnet)

    $managedProjects = @(
        'src\CoreBroker\WinWidgetBoard.CoreBroker.csproj',
        'src\WorkspacePanel\WinWidgetBoard.WorkspacePanel.csproj')

    foreach ($project in $managedProjects) {
        Write-Host "Building $project"
        & $Dotnet.Exe build (Join-Path $repoRoot $project) `
            --configuration $Configuration `
            "--property:Platform=$Platform"
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed: $project"
        }
    }

    # LauncherHost is C++/MSBuild and the portable SDK is not registered with the
    # Visual Studio SDK resolver, so it is built separately or not at all.
    $msbuild = Get-MsBuildPath
    if ($null -eq $msbuild) {
        Write-Warning @'
Skipped LauncherHost: MSBuild was not found. Build it from a Visual Studio
Developer PowerShell if you need the taskbar entry:
  msbuild .\src\LauncherHost\WinWidgetBoard.LauncherHost.vcxproj /p:Configuration=Release /p:Platform=x64
'@
        return
    }

    Write-Host 'Building src\LauncherHost\WinWidgetBoard.LauncherHost.vcxproj'
    & $msbuild (Join-Path $repoRoot 'src\LauncherHost\WinWidgetBoard.LauncherHost.vcxproj') `
        "/p:Configuration=$Configuration" `
        "/p:Platform=$Platform" `
        /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Build failed: LauncherHost'
    }
}

function Invoke-Test {
    param([pscustomobject]$Dotnet)

    $arguments = @(
        'test',
        (Join-Path $repoRoot 'tests\UnitTests\WinWidgetBoard.UnitTests.csproj'),
        '--configuration', $Configuration,
        "--property:Platform=$Platform")
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += @('--filter', $Filter)
        Write-Host "Filter: $Filter"
    }

    & $Dotnet.Exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed (exit code $LASTEXITCODE)."
    }
}

function Invoke-Run {
    Assert-NoRunningInstance

    $brokerPath = Resolve-ProductExecutable -Component 'CoreBroker'
    $panelPath = Resolve-ProductExecutable -Component 'WorkspacePanel'
    foreach ($required in @(
            @{ Name = 'CoreBroker'; Path = $brokerPath },
            @{ Name = 'WorkspacePanel'; Path = $panelPath })) {
        if ($null -eq $required.Path) {
            throw "$($required.Name) is not built for $Configuration/$Platform. Run -Task Build first."
        }
    }

    Import-Module -Name (Join-Path $PSScriptRoot 'WinWidgetBoard.UiAutomation.psm1') `
        -DisableNameChecking -Force

    $sandboxRoot = Join-Path ([IO.Path]::GetTempPath()) "winwidgetboard-sandbox-$([Guid]::NewGuid().ToString('N'))"
    $brokerDataRoot = Join-Path $sandboxRoot 'broker-data'
    $panelLocalAppData = Join-Path $sandboxRoot 'panel-localappdata'
    [void](New-Item -ItemType Directory -Path $brokerDataRoot -Force)
    [void](New-Item -ItemType Directory -Path $panelLocalAppData -Force)

    $originalLocalAppData = $env:LOCALAPPDATA
    $broker = $null
    $panel = $null

    try {
        $sessionToken = New-SessionToken
        $env:LOCALAPPDATA = $panelLocalAppData

        Write-Host "Sandbox data    $sandboxRoot"
        Write-Host 'Starting CoreBroker (isolated instance and data directory)'
        $broker = Start-TestBroker `
            -BrokerPath $brokerPath `
            -TestDataRoot $brokerDataRoot `
            -SessionToken $sessionToken

        Write-Host 'Starting WorkspacePanel'
        $panel = Start-TestPanel `
            -PanelPath $panelPath `
            -PortableDotnetRoot $PortableDotnetRoot `
            -SessionToken $sessionToken

        Write-Host ''
        Write-Host "Panel window is up (pid $($panel.Process.Id)). Notes, layout and weather"
        Write-Host 'settings all write to the sandbox directory above, not to your real data.'
        Write-Host 'Press Enter to stop both processes and clean up.'
        Write-Host ''

        Wait-ForSandboxExit -PanelProcess $panel.Process
    }
    finally {
        # Only processes this script started are stopped. Assert-NoRunningInstance
        # proved none existed beforehand, so a panel still alive here is ours even
        # when Start-TestPanel threw before returning its process object.
        if ($null -ne $panel) {
            Stop-OwnedProcess -Process $panel.Process
        }
        else {
            foreach ($stray in @(Get-Process -Name 'WinWidgetBoard.WorkspacePanel' -ErrorAction SilentlyContinue)) {
                Stop-OwnedProcess -Process $stray
            }
        }

        if ($null -ne $broker) {
            Stop-TestProcess -Process $broker
        }

        $env:LOCALAPPDATA = $originalLocalAppData

        if ($KeepTestData) {
            Write-Host "Retained sandbox data: $sandboxRoot"
        }
        else {
            Remove-SandboxDirectory -Path $sandboxRoot
        }
    }
}

function Wait-ForSandboxExit {
    param([Diagnostics.Process]$PanelProcess)

    while (-not $PanelProcess.HasExited) {
        try {
            if ([Console]::KeyAvailable) {
                if ([Console]::ReadKey($true).Key -eq 'Enter') {
                    return
                }
            }
        }
        catch [InvalidOperationException] {
            # No interactive console (redirected input): just wait for the panel.
            [void]$PanelProcess.WaitForExit()
            return
        }

        Start-Sleep -Milliseconds 200
    }
}

$savedEnvironment = @{
    DOTNET_ROOT = $env:DOTNET_ROOT
    DOTNET_ROOT_X64 = $env:DOTNET_ROOT_X64
    PATH = $env:PATH
}

try {
    $dotnet = Initialize-DotnetEnvironment -Root $PortableDotnetRoot

    switch ($Task) {
        'Env' { Show-Environment -Dotnet $dotnet }
        'Build' { Invoke-Build -Dotnet $dotnet; Show-Environment -Dotnet $dotnet }
        'Test' { Invoke-Test -Dotnet $dotnet }
        'Run' { Invoke-Run }
    }
}
finally {
    $env:DOTNET_ROOT = $savedEnvironment.DOTNET_ROOT
    $env:DOTNET_ROOT_X64 = $savedEnvironment.DOTNET_ROOT_X64
    $env:PATH = $savedEnvironment.PATH
}
