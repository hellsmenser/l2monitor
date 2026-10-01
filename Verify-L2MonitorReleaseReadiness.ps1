[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = $PSScriptRoot
}

$checks = @(
    [pscustomobject]@{
        Name = "Telegram direct mode"
        Command = @(
            "dotnet",
            "test",
            (Join-Path $RepoRoot "L2Monitor.Agent.Tests\L2Monitor.Agent.Tests.csproj"),
            "-c",
            $Configuration,
            "--nologo",
            "--filter",
            "FullyQualifiedName=L2Monitor.Agent.Tests.Api.LocalControlApiContractTests.Status_ReportsTelegramDirectModeConfiguredWhenLocalSettingsAndSecretArePresent"
        )
    },
    [pscustomobject]@{
        Name = "Cloud offline mode"
        Command = @(
            "dotnet",
            "test",
            (Join-Path $RepoRoot "L2Monitor.Agent.Tests\L2Monitor.Agent.Tests.csproj"),
            "-c",
            $Configuration,
            "--nologo",
            "--filter",
            "FullyQualifiedName=L2Monitor.Agent.Tests.Api.LocalControlApiContractTests.Status_ReportsCloudBackendMisconfiguredWhenBackendBaseUrlIsInvalid"
        )
    },
    [pscustomobject]@{
        Name = "Tray reconnect behavior"
        Command = @(
            "dotnet",
            "test",
            (Join-Path $RepoRoot "L2Monitor.Tray.Tests\L2Monitor.Tray.Tests.csproj"),
            "-c",
            $Configuration,
            "--nologo",
            "--filter",
            "FullyQualifiedName~L2Monitor.Tray.Tests.Api.LocalControlApiClientTests"
        )
    },
    [pscustomobject]@{
        Name = "Autostart and install persistence"
        Command = @(
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            (Join-Path $RepoRoot "installer\Verify-L2MonitorInstaller.ps1"),
            "-RepoRoot",
            $RepoRoot,
            "-Configuration",
            $Configuration
        )
    }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($check in $checks) {
    Write-Host ""
    Write-Host ("=== {0} ===" -f $check.Name)
    Write-Host ($check.Command -join " ")

    $output = & $check.Command[0] $check.Command[1..($check.Command.Count - 1)] 2>&1
    $exitCode = $LASTEXITCODE

    $results.Add([pscustomobject]@{
        Name = $check.Name
        ExitCode = $exitCode
        Output = [string]::Join([Environment]::NewLine, $output)
    })

    if ($output) {
        $output | Write-Host
    }

    $outputText = [string]::Join([Environment]::NewLine, $output)
    if ($exitCode -ne 0) {
        throw ("Release-readiness check failed: {0}" -f $check.Name)
    }
    if ($outputText -match "(?i)(no tests? match|no tests? were found|нет тестов)") {
        throw ("Release-readiness check selected no tests: {0}" -f $check.Name)
    }
}

[pscustomobject]@{
    RepoRoot = $RepoRoot
    Configuration = $Configuration
    Checks = $results
    Result = "PASS"
}
