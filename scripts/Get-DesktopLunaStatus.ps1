[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$statePath = Join-Path $env:LOCALAPPDATA 'CCCG\desktop-luna\current.json'
$bridgeStatePath = Join-Path $env:LOCALAPPDATA 'ClaudeDesktopBridgeShim\current.json'
$running = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -ieq 'Claude.exe' -or $_.Name -like 'claude.anthropic-*.exe'
}

$result = [ordered]@{
    ClaudeProcessCount = @($running).Count
    ClaudeProcessIds = @($running | Select-Object -ExpandProperty ProcessId | Sort-Object)
    CccgStatePath = $statePath
    CccgStatus = 'not-installed'
    ActiveTargetSha256 = $null
    ExpectedRouterSha256 = $null
    BridgeStatePresent = Test-Path -LiteralPath $bridgeStatePath -PathType Leaf
}

if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $result.CccgStatus = [string] $state.status
    $result.ExpectedRouterSha256 = [string] $state.installedRouterSha256
    if (Test-Path -LiteralPath ([string] $state.targetPath) -PathType Leaf) {
        $result.ActiveTargetSha256 = (Get-FileHash -LiteralPath ([string] $state.targetPath) -Algorithm SHA256).Hash
    }
}

[pscustomobject] $result | Format-List

