[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "L2MonitorInstaller.Common.ps1")

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot ".."
}

$verificationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("l2monitor-installer-verify-" + [Guid]::NewGuid().ToString("N"))
$publishRoot = Join-Path $verificationRoot "publish"
$installRoot = Join-Path $verificationRoot "install-root"
$agentDataRoot = Join-Path $verificationRoot "user-data\agent"
$registryRoot = "HKCU:\Software\L2Monitor\InstallerTests\" + [Guid]::NewGuid().ToString("N")
$runRegistryPath = Join-Path $registryRoot "Run"
$unrelatedProcessRoot = Join-Path $verificationRoot "unrelated-processes"
$startedProcesses = @()

try {
    Publish-L2MonitorPayload -RepoRoot $RepoRoot -PublishRoot $publishRoot -Configuration $Configuration | Out-Null

    $layout = Get-L2MonitorInstallLayout -InstallRoot $installRoot -AgentDataRoot $agentDataRoot -RunRegistryPath $runRegistryPath
    $installed = Install-L2MonitorFromPublishedPayload -PublishRoot $publishRoot -Layout $layout -RegisterAutostart

    if (-not $installed.AgentInstalled -or -not $installed.TrayInstalled) {
        throw "Install verification failed: application payloads were not copied."
    }

    if ($installed.AgentAutostartEnabled -or -not $installed.TrayAutostartEnabled) {
        throw "Install verification failed: the single Aden+ autostart entry was not registered."
    }

    if (-not $installed.InstallMetadataPresent) {
        throw "Install verification failed: install metadata was not created."
    }

    $installMetadata = Get-Content -LiteralPath $layout.InstallMetadataPath -Raw | ConvertFrom-Json
    if ($installMetadata.agentDataRoot -ne $layout.AgentDataRoot) {
        throw "Install verification failed: install metadata did not point at the configured data root."
    }

    $settingsPath = Join-Path $layout.AgentDataRoot "settings.json"
    $secretsPath = Join-Path $layout.AgentDataRoot "secrets.dat"
    [System.IO.File]::WriteAllText($settingsPath, '{"loopback":{"port":45631}}')
    [System.IO.File]::WriteAllBytes($secretsPath, [byte[]](1, 2, 3, 4))

    $upgraded = Install-L2MonitorFromPublishedPayload -PublishRoot $publishRoot -Layout $layout -RegisterAutostart
    if (-not (Test-Path -LiteralPath $settingsPath) -or -not (Test-Path -LiteralPath $secretsPath)) {
        throw "Upgrade verification failed: agent data was not preserved."
    }

    if ($upgraded.AgentAutostartEnabled -or -not $upgraded.TrayAutostartEnabled) {
        throw "Upgrade verification failed: the single Aden+ autostart entry was not preserved."
    }

    if (-not $upgraded.InstallMetadataPresent) {
        throw "Upgrade verification failed: install metadata was not preserved."
    }

    New-L2MonitorDirectory -Path $unrelatedProcessRoot
    $powershellExe = (Get-Process -Id $PID).Path
    foreach ($processName in @("AdenPlus.Agent", "Aden+")) {
        $processExecutablePath = Join-Path $unrelatedProcessRoot ($processName + ".exe")
        Copy-Item -LiteralPath $powershellExe -Destination $processExecutablePath -Force
        $startedProcesses += Start-Process -FilePath $processExecutablePath -ArgumentList @(
            "-NoProfile",
            "-WindowStyle",
            "Hidden",
            "-Command",
            "Start-Sleep -Seconds 60"
        ) -PassThru -WindowStyle Hidden
    }

    Start-Sleep -Milliseconds 500
    Stop-L2MonitorProcesses -Layout $layout
    foreach ($process in $startedProcesses) {
        if ($process.HasExited) {
            throw "Process-stop verification failed: unrelated process '$($process.ProcessName)' was terminated."
        }
    }

    $uninstalled = Uninstall-L2Monitor -Layout $layout
    if ($uninstalled.AgentInstalled -or $uninstalled.TrayInstalled) {
        throw "Uninstall verification failed: application payloads remain after uninstall."
    }

    if ($uninstalled.AgentRunCommand -or $uninstalled.TrayRunCommand) {
        throw "Uninstall verification failed: autostart entries were not removed."
    }

    if (-not (Test-Path -LiteralPath $settingsPath) -or -not (Test-Path -LiteralPath $secretsPath)) {
        throw "Uninstall verification failed: user data should be preserved by default."
    }

    if (Test-Path -LiteralPath $layout.InstallMetadataPath) {
        throw "Uninstall verification failed: install metadata should be removed with the app payload."
    }

    Install-L2MonitorFromPublishedPayload -PublishRoot $publishRoot -Layout $layout -RegisterAutostart | Out-Null
    $uninstallLayoutWithoutAgentDataRoot = Get-L2MonitorInstallLayout -InstallRoot $installRoot -RunRegistryPath $runRegistryPath
    if ($uninstallLayoutWithoutAgentDataRoot.AgentDataRoot -ne $layout.AgentDataRoot) {
        throw "RemoveUserData verification failed: uninstall layout did not recover the installed agent data root from install metadata."
    }

    $removedWithData = Uninstall-L2Monitor -Layout $uninstallLayoutWithoutAgentDataRoot -RemoveUserData
    if ($removedWithData.AgentDataPresent) {
        throw "RemoveUserData verification failed: agent data still exists."
    }

    if ($removedWithData.AgentRunCommand -or $removedWithData.TrayRunCommand) {
        throw "RemoveUserData verification failed: autostart entries were not removed."
    }

    if (Test-Path -LiteralPath $layout.InstallMetadataPath) {
        throw "RemoveUserData verification failed: install metadata should not remain."
    }

    [pscustomobject]@{
        VerificationRoot = $verificationRoot
        InstallRoot      = $layout.InstallRoot
        AgentDataRoot    = $layout.AgentDataRoot
        RunRegistryPath  = $layout.RunRegistryPath
        Result           = "PASS"
    }
}
finally {
    foreach ($process in $startedProcesses) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
        }
    }

    if (Test-Path -LiteralPath $registryRoot) {
        Remove-Item -LiteralPath $registryRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath $verificationRoot) {
        Remove-Item -LiteralPath $verificationRoot -Recurse -Force
    }
}
