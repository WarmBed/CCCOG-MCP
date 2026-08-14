param(
    [string] $StatePath,
    [switch] $Apply,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path $env:LOCALAPPDATA 'CCCG\desktop-luna\current.json'
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
        throw "Claude Desktop/engine is still running (PIDs: $summary). Exit Claude completely before restore."
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

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
    throw "CCCG Desktop Luna state is missing: $StatePath"
}

$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
if ([string] $state.status -ne 'installed') {
    throw "CCCG Desktop Luna state is not installed (status=$($state.status))."
}

$targetPath = Assert-SafeTarget ([string] $state.targetPath)
$configPath = Assert-SafeTarget ([string] $state.configPath)
$preservedBridgePath = Assert-SafeTarget ([string] $state.preservedBridgePath)
$targetDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
$activeManifestPath = Join-Path $targetDirectory 'cccg.desktop-luna-manifest.json'
$recoveryClaudePath = [System.IO.Path]::GetFullPath([string] $state.recoveryClaudePath)
$recoveryDirectory = [System.IO.Path]::GetFullPath([string] $state.recoveryDirectory)

if (-not (Test-Path -LiteralPath $recoveryClaudePath -PathType Leaf)) {
    throw "Recovery bridge shim is missing: $recoveryClaudePath"
}

$observedRouterHash = if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
    (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
} else {
    $null
}
if (-not $Force -and $observedRouterHash -ne [string] $state.installedRouterSha256) {
    throw "Active claude.exe no longer matches the installed CCCG router. Use -Force only after manual inspection."
}

[pscustomobject]@{
    Ready = $true
    Target = $targetPath
    ObservedRouterSha256 = $observedRouterHash
    RestoreSha256 = (Get-FileHash -LiteralPath $recoveryClaudePath -Algorithm SHA256).Hash
    RecoveryDirectory = $recoveryDirectory
    Mode = if ($Apply) { 'apply' } else { 'dry-run' }
} | Format-List

if (-not $Apply) {
    Write-Host 'Dry run only. Re-run with -Apply after Claude is completely stopped.'
    return
}

Assert-ClaudeStopped

if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
    Move-Item -LiteralPath $targetPath -Destination (Join-Path $recoveryDirectory 'removed-cccg-router.exe') -Force
}
foreach ($ownedPath in @($configPath, $preservedBridgePath, $activeManifestPath)) {
    if (Test-Path -LiteralPath $ownedPath -PathType Leaf) {
        $name = 'removed-' + [System.IO.Path]::GetFileName($ownedPath)
        Move-Item -LiteralPath $ownedPath -Destination (Join-Path $recoveryDirectory $name) -Force
    }
}

$restoreTemp = Join-Path $targetDirectory ('.cccg-restore-' + [guid]::NewGuid().ToString('N') + '.tmp')
Copy-Item -LiteralPath $recoveryClaudePath -Destination $restoreTemp
Move-Item -LiteralPath $restoreTemp -Destination $targetPath -Force

$restoredHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
if ($restoredHash -ne [string] $state.previousBridgeSha256) {
    throw "Restored bridge-shim hash mismatch. Inspect recovery at $recoveryDirectory"
}

$state.status = 'restored'
$state | Add-Member -NotePropertyName restoredAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
Write-Utf8NoBom $StatePath ($state | ConvertTo-Json -Depth 8)
Write-Host "Original bridge-shim layer restored. SHA-256: $restoredHash"
