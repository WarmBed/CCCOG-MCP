param(
    [string] $RouterPath,
    [string] $BridgeStatePath,
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RouterPath)) {
    $RouterPath = Join-Path $PSScriptRoot '..\artifacts\cccg-router\desktop-luna\cccg-router.exe'
}
if ([string]::IsNullOrWhiteSpace($BridgeStatePath)) {
    $BridgeStatePath = Join-Path $env:LOCALAPPDATA 'ClaudeDesktopBridgeShim\current.json'
}

function Write-Utf8NoBom([string] $Path, [string] $Content) {
    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-ClaudeStopped {
    $running = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -ieq 'Claude.exe' -or $_.Name -like 'claude.anthropic-*.exe'
    }

    if ($running) {
        $summary = ($running | Select-Object -ExpandProperty ProcessId | Sort-Object) -join ', '
        throw "Claude Desktop/engine is still running (PIDs: $summary). Exit Claude completely before installation."
    }
}

function Assert-SafeTarget([string] $Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $allowedRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code'))
    $prefix = $allowedRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing an unexpected Claude engine target: $resolved"
    }

    return $resolved
}

if (-not (Test-Path -LiteralPath $RouterPath -PathType Leaf)) {
    throw "Published CCCG router is missing: $RouterPath"
}
if (-not (Test-Path -LiteralPath $BridgeStatePath -PathType Leaf)) {
    throw "Existing bridge-shim state is missing: $BridgeStatePath"
}

$bridgeState = Get-Content -LiteralPath $BridgeStatePath -Raw | ConvertFrom-Json
$targetPath = Assert-SafeTarget ([string] $bridgeState.targetPath)
$targetDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
$sidecarPath = Assert-SafeTarget ([string] $bridgeState.sidecarOriginalPath)
$preservedBridgePath = Join-Path $targetDirectory 'claude.bridge-shim-2.1.227.exe'
$configPath = Join-Path $targetDirectory 'cccg.routes.json'
$activeManifestPath = Join-Path $targetDirectory 'cccg.desktop-luna-manifest.json'

if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
    throw "Active bridge shim is missing: $targetPath"
}
if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
    throw "Anthropic sidecar is missing: $sidecarPath"
}

$activeHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
$sidecarHash = (Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256).Hash
if ($activeHash -ne [string] $bridgeState.shimSha256) {
    throw "Active claude.exe is not the recorded bridge shim. Expected $($bridgeState.shimSha256), observed $activeHash."
}
if ($sidecarHash -ne [string] $bridgeState.originalSha256) {
    throw "Claude sidecar hash changed. Expected $($bridgeState.originalSha256), observed $sidecarHash."
}

$routerResolved = [System.IO.Path]::GetFullPath($RouterPath)
$routerHash = (Get-FileHash -LiteralPath $routerResolved -Algorithm SHA256).Hash

[pscustomobject]@{
    Ready = $true
    Target = $targetPath
    ExistingBridgeSha256 = $activeHash
    AnthropicSidecarSha256 = $sidecarHash
    CccgRouter = $routerResolved
    CccgRouterSha256 = $routerHash
    Mode = if ($Apply) { 'apply' } else { 'dry-run' }
} | Format-List

if (-not $Apply) {
    Write-Host 'Dry run only. Re-run with -Apply after Claude is completely stopped.'
    return
}

Assert-ClaudeStopped

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stateRoot = Join-Path $env:LOCALAPPDATA 'CCCG\desktop-luna'
$recoveryDirectory = Join-Path $stateRoot (Join-Path 'recovery' $stamp)
New-Item -ItemType Directory -Path $recoveryDirectory -Force | Out-Null

$recoveryClaudePath = Join-Path $recoveryDirectory 'previous-claude.exe'
Copy-Item -LiteralPath $targetPath -Destination $recoveryClaudePath
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    Copy-Item -LiteralPath $configPath -Destination (Join-Path $recoveryDirectory 'previous-cccg.routes.json')
}
if (Test-Path -LiteralPath $preservedBridgePath -PathType Leaf) {
    Copy-Item -LiteralPath $preservedBridgePath -Destination (Join-Path $recoveryDirectory 'previous-preserved-bridge.exe')
}

$configuration = [ordered]@{
    unmappedBehavior = 'passthrough'
    originalClaudePath = 'claude.bridge-shim-2.1.227.exe'
    routes = [ordered]@{
        'claude-haiku-4-5' = [ordered]@{
            provider = 'codex-app-server'
            model = 'gpt-5.6-luna'
            reasoningEffort = 'medium'
        }
        'claude-haiku-4-5-20251001' = [ordered]@{
            provider = 'codex-app-server'
            model = 'gpt-5.6-luna'
            reasoningEffort = 'medium'
        }
    }
}

$configTemp = Join-Path $targetDirectory ('.cccg-config-' + [guid]::NewGuid().ToString('N') + '.tmp')
Write-Utf8NoBom $configTemp ($configuration | ConvertTo-Json -Depth 8)
Move-Item -LiteralPath $configTemp -Destination $configPath -Force

$bridgeTemp = Join-Path $targetDirectory ('.cccg-bridge-' + [guid]::NewGuid().ToString('N') + '.tmp')
$routerTemp = Join-Path $targetDirectory ('.cccg-router-' + [guid]::NewGuid().ToString('N') + '.tmp')
Copy-Item -LiteralPath $targetPath -Destination $bridgeTemp
Move-Item -LiteralPath $bridgeTemp -Destination $preservedBridgePath -Force
Copy-Item -LiteralPath $routerResolved -Destination $routerTemp
Move-Item -LiteralPath $routerTemp -Destination $targetPath -Force

$installedRouterHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
$preservedBridgeHash = (Get-FileHash -LiteralPath $preservedBridgePath -Algorithm SHA256).Hash
if ($installedRouterHash -ne $routerHash) {
    throw "Installed CCCG router hash mismatch. Recovery is at $recoveryDirectory"
}
if ($preservedBridgeHash -ne $activeHash) {
    throw "Preserved bridge-shim hash mismatch. Recovery is at $recoveryDirectory"
}

$manifest = [ordered]@{
    schemaVersion = 1
    status = 'installed'
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    targetPath = $targetPath
    configPath = $configPath
    preservedBridgePath = $preservedBridgePath
    anthropicSidecarPath = $sidecarPath
    recoveryDirectory = $recoveryDirectory
    recoveryClaudePath = $recoveryClaudePath
    previousBridgeSha256 = $activeHash
    preservedBridgeSha256 = $preservedBridgeHash
    anthropicSidecarSha256 = $sidecarHash
    installedRouterSha256 = $installedRouterHash
    routes = @{
        'claude-haiku-4-5' = 'gpt-5.6-luna'
        'claude-haiku-4-5-20251001' = 'gpt-5.6-luna'
    }
}
Write-Utf8NoBom $activeManifestPath ($manifest | ConvertTo-Json -Depth 8)
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
Write-Utf8NoBom (Join-Path $stateRoot 'current.json') ($manifest | ConvertTo-Json -Depth 8)

Write-Host "CCCG Desktop Luna experiment installed. Recovery: $recoveryDirectory"
