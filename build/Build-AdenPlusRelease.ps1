[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$BackendBaseUrl,
    [string]$OutputRoot,
    [switch]$SkipTests,
    [switch]$AllowDirtyWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot ".codex-out\release"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}

$packageName = "AdenPlus-v$Version-$RuntimeIdentifier"
$stagingRoot = Join-Path $resolvedOutputRoot $packageName
$agentRoot = Join-Path $stagingRoot "runtime\agent"
$archivePath = Join-Path $resolvedOutputRoot ($packageName + ".zip")
$checksumPath = Join-Path $resolvedOutputRoot ($packageName + ".sha256")

if (-not $AllowDirtyWorktree) {
    $gitStatus = & git -C $repoRoot status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to verify the Git worktree before release build."
    }
    if ($gitStatus) {
        throw "Release builds require a clean Git worktree. Commit or stash changes, or use -AllowDirtyWorktree for a non-release development build."
    }
}

function Test-PathUnderRoot {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $fullRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $fullRoot += [System.IO.Path]::DirectorySeparatorChar
    }

    return $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-PathUnderRoot -Path $stagingRoot -Root $resolvedOutputRoot)) {
    throw "Refusing to replace staging path outside the selected output root."
}

New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

if (-not $SkipTests) {
    & dotnet test (Join-Path $repoRoot "L2Monitor.slnx") -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed."
    }
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $agentRoot -Force | Out-Null

$publishProperties = @(
    "-p:Version=$Version",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

& dotnet publish (Join-Path $repoRoot "L2Monitor.Agent\L2Monitor.Agent.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $agentRoot `
    --nologo `
    @publishProperties
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the Aden+ background component failed."
}

& dotnet publish (Join-Path $repoRoot "L2Monitor.Tray\L2Monitor.Tray.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $stagingRoot `
    --nologo `
    @publishProperties
if ($LASTEXITCODE -ne 0) {
    throw "Publishing Aden+.exe failed."
}

if (-not [string]::IsNullOrWhiteSpace($BackendBaseUrl)) {
    $resolvedBackendUri = $null
    if (-not [Uri]::TryCreate($BackendBaseUrl.Trim(), [UriKind]::Absolute, [ref]$resolvedBackendUri) -or
        ($resolvedBackendUri.Scheme -ne "https" -and
            ($resolvedBackendUri.Scheme -ne "http" -or -not $resolvedBackendUri.IsLoopback)) -or
        -not [string]::IsNullOrEmpty($resolvedBackendUri.UserInfo)) {
        throw "BackendBaseUrl must use HTTPS; HTTP is allowed only for loopback development builds."
    }

    $defaults = [pscustomobject]@{ backendBaseUrl = $resolvedBackendUri.AbsoluteUri }
    [System.IO.File]::WriteAllText(
        (Join-Path $agentRoot "adenplus.defaults.json"),
        ($defaults | ConvertTo-Json -Depth 2),
        (New-Object System.Text.UTF8Encoding($false)))
}

Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $stagingRoot "LICENSE")
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination (Join-Path $stagingRoot "README.md")
Copy-Item -LiteralPath (Join-Path $repoRoot "PRIVACY.md") -Destination (Join-Path $stagingRoot "PRIVACY.md")
Copy-Item -LiteralPath (Join-Path $repoRoot "SECURITY.md") -Destination (Join-Path $stagingRoot "SECURITY.md")
Copy-Item -LiteralPath (Join-Path $repoRoot "CODE_SIGNING.md") -Destination (Join-Path $stagingRoot "CODE_SIGNING.md")
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\RELEASE_NOTES.md") -Destination (Join-Path $stagingRoot "RELEASE_NOTES.md")

$mediaSource = Join-Path $repoRoot "docs\media"
if (Test-Path -LiteralPath $mediaSource -PathType Container) {
    $mediaDestination = Join-Path $stagingRoot "docs\media"
    New-Item -ItemType Directory -Path $mediaDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $mediaSource "*") -Destination $mediaDestination -Recurse -Force
}

$dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
Copy-Item -LiteralPath (Join-Path $dotnetRoot "LICENSE.txt") -Destination (Join-Path $stagingRoot "DOTNET-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $dotnetRoot "ThirdPartyNotices.txt") -Destination (Join-Path $stagingRoot "DOTNET-THIRD-PARTY-NOTICES.txt")

$nugetPackagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".nuget\packages"
}
else {
    $env:NUGET_PACKAGES
}
$naudioLicensePath = Join-Path $nugetPackagesRoot "naudio\2.2.1\license.txt"
if (-not (Test-Path -LiteralPath $naudioLicensePath)) {
    throw "The NAudio license file was not found after restore: $naudioLicensePath"
}
Copy-Item -LiteralPath $naudioLicensePath -Destination (Join-Path $stagingRoot "NAUDIO-LICENSE.txt")

$unexpectedRootExecutables = @(Get-ChildItem -LiteralPath $stagingRoot -File -Filter "*.exe" | Where-Object Name -ne "Aden+.exe")
if ($unexpectedRootExecutables.Count -gt 0) {
    throw "The release root contains more than one executable entry point."
}

if (-not (Test-Path -LiteralPath (Join-Path $stagingRoot "Aden+.exe"))) {
    throw "The release entry point Aden+.exe was not produced."
}
if (-not (Test-Path -LiteralPath (Join-Path $agentRoot "AdenPlus.Agent.exe"))) {
    throw "The internal Aden+ agent was not produced."
}

Compress-Archive -LiteralPath $stagingRoot -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$hash  $([System.IO.Path]::GetFileName($archivePath))`n",
    (New-Object System.Text.UTF8Encoding($false)))

[pscustomobject]@{
    Version = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    BackendConfigured = -not [string]::IsNullOrWhiteSpace($BackendBaseUrl)
    StagingRoot = $stagingRoot
    ArchivePath = $archivePath
    ChecksumPath = $checksumPath
    Sha256 = $hash
}
