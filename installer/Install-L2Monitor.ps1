[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Configuration = "Release",
    [string]$PublishRoot,
    [string]$InstallRoot,
    [string]$AgentDataRoot,
    [string]$RunRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
    [switch]$SkipPublish,
    [switch]$NoAutostart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "L2MonitorInstaller.Common.ps1")

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot ".."
}

$resolvedPublishRoot = if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    Join-Path $PSScriptRoot "_published"
}
else {
    $PublishRoot
}

if (-not $SkipPublish) {
    Publish-L2MonitorPayload -RepoRoot $RepoRoot -PublishRoot $resolvedPublishRoot -Configuration $Configuration | Out-Null
}

$layout = Get-L2MonitorInstallLayout -InstallRoot $InstallRoot -AgentDataRoot $AgentDataRoot -RunRegistryPath $RunRegistryPath
$state = Install-L2MonitorFromPublishedPayload -PublishRoot $resolvedPublishRoot -Layout $layout -RegisterAutostart:(-not $NoAutostart)

$state
