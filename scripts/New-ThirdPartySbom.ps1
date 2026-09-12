<#
.SYNOPSIS
    Writes a CycloneDX software bill of materials for an assembled release layout.

.DESCRIPTION
    Describes what a release archive actually contains, not what the repository references. The
    component list is read from the artifact itself wherever possible - the runtime pack from the
    broker's deps.json, the Visual C++ runtime from the staged DLLs' own file versions - so an SBOM
    can never drift from the build it claims to describe.

    Build- and test-only packages are excluded on purpose: they never enter the product output, and
    listing them would misrepresent the attack surface a consumer inherits.

    ADR-0006 lists the SBOM as a gate for distributing binaries; this script is what satisfies it.

.PARAMETER StagingDirectory
    The assembled install layout: LauncherHost at the root, WorkspacePanel\ and CoreBroker\ beside it.

.PARAMETER Version
    The release version to stamp on the SBOM's own metadata, e.g. 0.1.0.

.PARAMETER OutputPath
    Where to write the JSON document.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath $StagingDirectory -PathType Container)) {
    throw "Staging directory not found: $StagingDirectory"
}

function New-Component {
    param(
        [string]$Name,
        [string]$ComponentVersion,
        [string]$Purl,
        [string]$LicenseExpression,
        [string]$LicenseName,
        [string]$Publisher,
        [string]$Description
    )

    $component = [ordered]@{
        type = 'library'
        name = $Name
        version = $ComponentVersion
    }
    if ($Publisher) { $component['publisher'] = $Publisher }
    if ($Purl) { $component['purl'] = $Purl }
    if ($Description) { $component['description'] = $Description }
    if ($LicenseExpression) {
        $component['licenses'] = @(@{ expression = $LicenseExpression })
    }
    elseif ($LicenseName) {
        $component['licenses'] = @(@{ license = @{ name = $LicenseName } })
    }

    return $component
}

$components = [System.Collections.Generic.List[object]]::new()

# --- 1. Our own binaries.
foreach ($own in @('WinWidgetBoard.LauncherHost', 'WinWidgetBoard.WorkspacePanel', 'WinWidgetBoard.CoreBroker')) {
    $components.Add((New-Component -Name $own -ComponentVersion $Version `
        -LicenseExpression 'MIT' -Publisher 'CookPiu' `
        -Description 'WinWidgetBoard component'))
}

# --- 2. Product NuGet packages, from the lock files. Build-only packages are filtered out rather
#        than trusted to be absent: PrivateAssets keeps them out of the output, not out of the lock.
$buildOnly = @(
    'Microsoft.Windows.SDK.BuildTools',
    'Microsoft.Windows.SDK.BuildTools.MSIX')
$microsoftTerms = 'Microsoft Software License Terms'

foreach ($project in @('WorkspacePanel', 'CoreBroker', 'CoreBroker.Client', 'Contracts')) {
    $lockFile = Join-Path $repositoryRoot "src\$project\packages.lock.json"
    if (-not (Test-Path -LiteralPath $lockFile)) { continue }

    $lock = Get-Content -LiteralPath $lockFile -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            $name = $package.Name
            $entry = $package.Value
            if ($entry.type -eq 'Project') { continue }
            if ($buildOnly -contains $name) { continue }
            if ($components | Where-Object { $_.name -eq $name }) { continue }

            $resolved = if ($entry.resolved) { $entry.resolved } else { $entry.requested }
            $components.Add((New-Component -Name $name -ComponentVersion $resolved `
                -Purl "pkg:nuget/$name@$resolved" -LicenseName $microsoftTerms `
                -Publisher 'Microsoft Corporation' `
                -Description 'Redistributed by the self-contained Windows App SDK deployment'))
        }
    }
}

# --- 3. The Windows SDK .NET projection, whose version only exists in the built deps.json.
$depsFile = Join-Path $StagingDirectory 'CoreBroker\WinWidgetBoard.CoreBroker.deps.json'
if (Test-Path -LiteralPath $depsFile) {
    $deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'runtimepack') { continue }

        $parts = $library.Name -split '/'
        $name = $parts[0] -replace '^runtimepack\.', ''
        $components.Add((New-Component -Name $name -ComponentVersion $parts[1] `
            -Purl "pkg:nuget/$name@$($parts[1])" -LicenseName $microsoftTerms `
            -Publisher 'Microsoft Corporation' `
            -Description 'Supplies Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll'))
    }
}

# --- 4. The Visual C++ runtime, versioned from the staged files themselves.
foreach ($runtimeDll in @('VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll')) {
    $path = Join-Path $StagingDirectory $runtimeDll
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

    $fileVersion = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    $components.Add((New-Component -Name $runtimeDll -ComponentVersion $fileVersion `
        -LicenseName 'Microsoft Visual Studio - Distributable Code' `
        -Publisher 'Microsoft Corporation' `
        -Description 'Application-local Visual C++ runtime required by LauncherHost'))
}

$document = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.5'
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        component = [ordered]@{
            type = 'application'
            name = 'WinWidgetBoard'
            version = $Version
            licenses = @(@{ expression = 'MIT' })
        }
        tools = @(@{ name = 'New-ThirdPartySbom.ps1'; vendor = 'WinWidgetBoard' })
    }
    components = $components.ToArray()
}

$json = $document | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8
Write-Output "wrote $($components.Count) components to $OutputPath"
