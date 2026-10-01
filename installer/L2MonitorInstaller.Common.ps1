Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-L2MonitorInstallLayout {
    [CmdletBinding()]
    param(
        [string]$InstallRoot,
        [string]$AgentDataRoot,
        [string]$RunRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    )

    $resolvedInstallRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        Join-Path $env:LOCALAPPDATA "L2Monitor"
    }
    else {
        [System.IO.Path]::GetFullPath($InstallRoot)
    }

    $appRoot = Join-Path $resolvedInstallRoot "app"
    $installMetadataPath = Join-Path $appRoot "install.json"

    $resolvedAgentDataRoot = if ([string]::IsNullOrWhiteSpace($AgentDataRoot)) {
        $installedAgentDataRoot = Get-L2MonitorInstalledAgentDataRoot -InstallMetadataPath $installMetadataPath
        if ([string]::IsNullOrWhiteSpace($installedAgentDataRoot)) {
            Join-Path $resolvedInstallRoot "agent"
        }
        else {
            [System.IO.Path]::GetFullPath($installedAgentDataRoot)
        }
    }
    else {
        [System.IO.Path]::GetFullPath($AgentDataRoot)
    }

    $agentAppRoot = Join-Path $appRoot "agent"
    $trayAppRoot = Join-Path $appRoot "tray"

    [pscustomobject]@{
        InstallRoot          = $resolvedInstallRoot
        AppRoot              = $appRoot
        AgentAppRoot         = $agentAppRoot
        TrayAppRoot          = $trayAppRoot
        AgentDataRoot        = $resolvedAgentDataRoot
        InstallMetadataPath  = $installMetadataPath
        AgentExecutablePath  = Join-Path $agentAppRoot "AdenPlus.Agent.exe"
        TrayExecutablePath   = Join-Path $trayAppRoot "Aden+.exe"
        RunRegistryPath      = $RunRegistryPath
        AgentRunValueName    = "L2Monitor.Agent"
        TrayRunValueName     = "Aden+"
        LegacyTrayRunValueName = "L2Monitor.Tray"
    }
}

function Get-L2MonitorRepoRoot {
    [CmdletBinding()]
    param()

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Get-L2MonitorInstalledAgentDataRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$InstallMetadataPath
    )

    if (-not (Test-Path -LiteralPath $InstallMetadataPath)) {
        return $null
    }

    try {
        $metadataJson = Get-Content -LiteralPath $InstallMetadataPath -Raw
        if ([string]::IsNullOrWhiteSpace($metadataJson)) {
            return $null
        }

        $metadata = $metadataJson | ConvertFrom-Json
        $agentDataRoot = $metadata.agentDataRoot
        if ([string]::IsNullOrWhiteSpace($agentDataRoot)) {
            return $null
        }

        return $agentDataRoot.Trim()
    }
    catch {
        return $null
    }
}

function Write-L2MonitorInstallMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout
    )

    $metadata = [pscustomobject]@{
        agentDataRoot = $Layout.AgentDataRoot
    }

    $metadataJson = $metadata | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText($Layout.InstallMetadataPath, $metadataJson)
}

function New-L2MonitorDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        [void](New-Item -ItemType Directory -Path $Path -Force)
    }
}

function Test-L2MonitorPathUnderRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)

    if (-not $fullRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $fullRoot = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    }

    return $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

function Clear-L2MonitorDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$AllowedRoot
    )

    if (-not (Test-L2MonitorPathUnderRoot -Path $Path -Root $AllowedRoot)) {
        throw "Refusing to clear '$Path' because it is outside '$AllowedRoot'."
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Force | Remove-Item -Recurse -Force
}

function Copy-L2MonitorTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        throw "Source path '$SourcePath' does not exist."
    }

    New-L2MonitorDirectory -Path $DestinationPath
    Get-ChildItem -LiteralPath $SourcePath -Force | Copy-Item -Destination $DestinationPath -Recurse -Force
}

function Get-L2MonitorRunCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,
        [switch]$Background
    )

    $command = '"' + $ExecutablePath + '"'
    if ($Background) {
        $command += " --background"
    }

    return $command
}

function Publish-L2MonitorPayload {
    [CmdletBinding()]
    param(
        [string]$RepoRoot = (Get-L2MonitorRepoRoot),
        [Parameter(Mandatory)]
        [string]$PublishRoot,
        [string]$Configuration = "Release"
    )

    $resolvedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
    $resolvedPublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
    $agentPublishRoot = Join-Path $resolvedPublishRoot "agent"
    $trayPublishRoot = Join-Path $resolvedPublishRoot "tray"

    New-L2MonitorDirectory -Path $resolvedPublishRoot

    & dotnet publish (Join-Path $resolvedRepoRoot "L2Monitor.Agent\L2Monitor.Agent.csproj") `
        -c $Configuration `
        -o $agentPublishRoot `
        /nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for L2Monitor.Agent."
    }

    & dotnet publish (Join-Path $resolvedRepoRoot "L2Monitor.Tray\L2Monitor.Tray.csproj") `
        -c $Configuration `
        -o $trayPublishRoot `
        /nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for L2Monitor.Tray."
    }

    return [pscustomobject]@{
        PublishRoot      = $resolvedPublishRoot
        AgentPublishRoot = $agentPublishRoot
        TrayPublishRoot  = $trayPublishRoot
    }
}

function Register-L2MonitorAutostart {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout
    )

    if (-not (Test-Path -LiteralPath $Layout.AgentExecutablePath)) {
        throw "Agent executable '$($Layout.AgentExecutablePath)' was not found."
    }

    if (-not (Test-Path -LiteralPath $Layout.TrayExecutablePath)) {
        throw "Tray executable '$($Layout.TrayExecutablePath)' was not found."
    }

    New-L2MonitorDirectory -Path $Layout.RunRegistryPath

    Remove-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.AgentRunValueName -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.LegacyTrayRunValueName -ErrorAction SilentlyContinue
    Set-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.TrayRunValueName -Value (Get-L2MonitorRunCommand -ExecutablePath $Layout.TrayExecutablePath -Background)
}

function Stop-L2MonitorProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout
    )

    $processes = @(Get-Process -Name "AdenPlus.Agent", "Aden+", "L2Monitor.Agent", "L2Monitor.Tray" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }

    $targetPaths = @(
        [System.IO.Path]::GetFullPath($Layout.AgentExecutablePath),
        [System.IO.Path]::GetFullPath($Layout.TrayExecutablePath)
    )

    $ownedProcesses = @(
        $processes | Where-Object {
            $processPath = $null
            try {
                $processPath = $_.Path
            }
            catch {
                try {
                    $processPath = $_.MainModule.FileName
                }
                catch {
                    $processPath = $null
                }
            }

            if ([string]::IsNullOrWhiteSpace($processPath)) {
                return $false
            }

            $resolvedProcessPath = [System.IO.Path]::GetFullPath($processPath)
            return $targetPaths -contains $resolvedProcessPath
        }
    )

    if ($ownedProcesses.Count -eq 0) {
        return
    }

    $ownedProcesses | Stop-Process -Force
    foreach ($process in $ownedProcesses) {
        try {
            $null = $process.WaitForExit(5000)
        }
        catch {
        }
    }
}

function Unregister-L2MonitorAutostart {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout
    )

    if (-not (Test-Path -LiteralPath $Layout.RunRegistryPath)) {
        return
    }

    Remove-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.AgentRunValueName -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.TrayRunValueName -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath $Layout.RunRegistryPath -Name $Layout.LegacyTrayRunValueName -ErrorAction SilentlyContinue
}

function Install-L2MonitorFromPublishedPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PublishRoot,
        [Parameter(Mandatory)]
        [object]$Layout,
        [switch]$RegisterAutostart
    )

    $resolvedPublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
    $agentPublishRoot = Join-Path $resolvedPublishRoot "agent"
    $trayPublishRoot = Join-Path $resolvedPublishRoot "tray"

    if (-not (Test-Path -LiteralPath (Join-Path $agentPublishRoot "AdenPlus.Agent.exe"))) {
        throw "Published agent payload was not found in '$agentPublishRoot'."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $trayPublishRoot "Aden+.exe"))) {
        throw "Published tray payload was not found in '$trayPublishRoot'."
    }

    New-L2MonitorDirectory -Path $Layout.InstallRoot
    New-L2MonitorDirectory -Path $Layout.AppRoot
    New-L2MonitorDirectory -Path $Layout.AgentAppRoot
    New-L2MonitorDirectory -Path $Layout.TrayAppRoot
    New-L2MonitorDirectory -Path $Layout.AgentDataRoot

    Stop-L2MonitorProcesses -Layout $Layout
    Clear-L2MonitorDirectory -Path $Layout.AgentAppRoot -AllowedRoot $Layout.InstallRoot
    Clear-L2MonitorDirectory -Path $Layout.TrayAppRoot -AllowedRoot $Layout.InstallRoot

    Copy-L2MonitorTree -SourcePath $agentPublishRoot -DestinationPath $Layout.AgentAppRoot
    Copy-L2MonitorTree -SourcePath $trayPublishRoot -DestinationPath $Layout.TrayAppRoot
    Write-L2MonitorInstallMetadata -Layout $Layout

    if ($RegisterAutostart) {
        Register-L2MonitorAutostart -Layout $Layout
    }

    return Get-L2MonitorInstallationState -Layout $Layout
}

function Remove-L2MonitorDirectoryIfEmpty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$AllowedRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    if (-not (Test-L2MonitorPathUnderRoot -Path $Path -Root $AllowedRoot) -and ([System.IO.Path]::GetFullPath($Path) -ne [System.IO.Path]::GetFullPath($AllowedRoot))) {
        throw "Refusing to prune '$Path' because it is outside '$AllowedRoot'."
    }

    $children = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)
    if ($children.Count -eq 0) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Uninstall-L2Monitor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout,
        [switch]$RemoveUserData
    )

    $removeAgentData = $false
    if ($RemoveUserData -and (Test-Path -LiteralPath $Layout.AgentDataRoot)) {
        $resolvedAgentDataRoot = [System.IO.Path]::GetFullPath($Layout.AgentDataRoot)
        $agentDataRootPathRoot = [System.IO.Path]::GetPathRoot($resolvedAgentDataRoot)
        if ([string]::IsNullOrWhiteSpace($agentDataRootPathRoot) -or
            [string]::Equals($resolvedAgentDataRoot, $agentDataRootPathRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove user data root path '$($Layout.AgentDataRoot)'."
        }

        $removeAgentData = $true
    }

    Unregister-L2MonitorAutostart -Layout $Layout

    Stop-L2MonitorProcesses -Layout $Layout
    Clear-L2MonitorDirectory -Path $Layout.AgentAppRoot -AllowedRoot $Layout.InstallRoot
    Clear-L2MonitorDirectory -Path $Layout.TrayAppRoot -AllowedRoot $Layout.InstallRoot

    if (Test-Path -LiteralPath $Layout.AgentAppRoot) {
        Remove-Item -LiteralPath $Layout.AgentAppRoot -Force
    }

    if (Test-Path -LiteralPath $Layout.TrayAppRoot) {
        Remove-Item -LiteralPath $Layout.TrayAppRoot -Force
    }

    if (Test-Path -LiteralPath $Layout.InstallMetadataPath) {
        Remove-Item -LiteralPath $Layout.InstallMetadataPath -Force
    }

    if ($removeAgentData) {
        Remove-Item -LiteralPath $Layout.AgentDataRoot -Recurse -Force
    }

    Remove-L2MonitorDirectoryIfEmpty -Path $Layout.AppRoot -AllowedRoot $Layout.InstallRoot
    Remove-L2MonitorDirectoryIfEmpty -Path $Layout.InstallRoot -AllowedRoot $Layout.InstallRoot

    return Get-L2MonitorInstallationState -Layout $Layout
}

function Get-L2MonitorInstallationState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Layout
    )

    $agentRunCommand = $null
    $trayRunCommand = $null
    if (Test-Path -LiteralPath $Layout.RunRegistryPath) {
        $runValues = Get-ItemProperty -LiteralPath $Layout.RunRegistryPath
        $agentProperty = $runValues.PSObject.Properties[$Layout.AgentRunValueName]
        $trayProperty = $runValues.PSObject.Properties[$Layout.TrayRunValueName]

        if ($null -ne $agentProperty) {
            $agentRunCommand = $agentProperty.Value
        }

        if ($null -ne $trayProperty) {
            $trayRunCommand = $trayProperty.Value
        }
    }

    [pscustomobject]@{
        InstallRoot            = $Layout.InstallRoot
        AppRoot                = $Layout.AppRoot
        AgentAppRoot           = $Layout.AgentAppRoot
        TrayAppRoot            = $Layout.TrayAppRoot
        AgentDataRoot          = $Layout.AgentDataRoot
        InstallMetadataPath    = $Layout.InstallMetadataPath
        AgentInstalled         = Test-Path -LiteralPath $Layout.AgentExecutablePath
        TrayInstalled          = Test-Path -LiteralPath $Layout.TrayExecutablePath
        AgentDataPresent       = Test-Path -LiteralPath $Layout.AgentDataRoot
        InstallMetadataPresent = Test-Path -LiteralPath $Layout.InstallMetadataPath
        RunRegistryPath        = $Layout.RunRegistryPath
        AgentRunCommand        = $agentRunCommand
        TrayRunCommand         = $trayRunCommand
        AgentAutostartEnabled  = $agentRunCommand -eq (Get-L2MonitorRunCommand -ExecutablePath $Layout.AgentExecutablePath)
        TrayAutostartEnabled   = $trayRunCommand -eq (Get-L2MonitorRunCommand -ExecutablePath $Layout.TrayExecutablePath -Background)
    }
}
