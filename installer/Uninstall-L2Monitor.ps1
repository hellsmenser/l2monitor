[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$AgentDataRoot,
    [string]$RunRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run",
    [switch]$RemoveUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "L2MonitorInstaller.Common.ps1")

$layout = Get-L2MonitorInstallLayout -InstallRoot $InstallRoot -AgentDataRoot $AgentDataRoot -RunRegistryPath $RunRegistryPath
$state = Uninstall-L2Monitor -Layout $layout -RemoveUserData:$RemoveUserData

$state
