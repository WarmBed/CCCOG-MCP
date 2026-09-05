param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Configuration = "Release"
)

# Versioned install for the MCP Host (cccg-dispatch.exe), mirroring
# install-dispatch-worker.ps1. The Host cannot be hot-swapped under a live
# session (each Claude session holds its own Host process and its loaded
# DLLs open), so instead of overwriting anything in place this script:
#
#   1. publishes to artifacts\cccg-dispatch-host\<Version>\  (build scratch)
#   2. copies to   %LOCALAPPDATA%\CCCG\dispatch\hosts\<Version>\  (immutable)
#   3. re-points   %LOCALAPPDATA%\CCCG\dispatch\host-current  (a directory
#      JUNCTION) at that versioned directory, and records host-current.json
#
# Sessions already running keep executing the old versioned directory
# untouched; sessions that (re)connect after this pick up the new one. Point
# every .mcp.json at the junction path, NOT at a versioned directory:
#
#   "command": "C:\\Users\\<you>\\AppData\\Local\\CCCG\\dispatch\\host-current\\cccg-dispatch.exe"
#
# A junction (not a symlink) needs no elevation and can be replaced while
# processes run from its target, because those processes hold handles to
# the target's files, not to the link.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $repoRoot "artifacts\cccg-dispatch-host\$Version"
$dispatchRoot = Join-Path $env:LOCALAPPDATA "CCCG\dispatch"
$installRoot = Join-Path $dispatchRoot "hosts\$Version"
$junctionPath = Join-Path $dispatchRoot "host-current"
$descriptorPath = Join-Path $dispatchRoot "host-current.json"

dotnet publish (Join-Path $repoRoot "src\CCCG.Dispatch\CCCG.Dispatch.csproj") `
    -c $Configuration `
    --no-restore `
    -p:Version=$Version `
    -o $publishRoot

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installRoot -Recurse -Force
}

$hostPath = Join-Path $installRoot "cccg-dispatch.exe"
if (-not (Test-Path -LiteralPath $hostPath)) {
    throw "Published host is missing: $hostPath"
}

# Swap the junction. Remove-Item on a junction removes only the reparse
# point; -Recurse would descend into the (still in use) old target.
if (Test-Path -LiteralPath $junctionPath) {
    $existing = Get-Item -LiteralPath $junctionPath -Force
    if (-not ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$junctionPath exists and is a real directory, not a junction; refusing to replace it."
    }
    [IO.Directory]::Delete($junctionPath)
}
New-Item -ItemType Junction -Path $junctionPath -Target $installRoot | Out-Null

$descriptor = [ordered]@{
    schemaVersion = 1
    path = $hostPath
    junction = $junctionPath
    version = $Version
    sha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant()
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$temporary = "$descriptorPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporary,
        ($descriptor | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $descriptorPath -Force
}
finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

[ordered]@{
    installed = $true
    version = $Version
    host = $hostPath
    junction = $junctionPath
    launchCommand = (Join-Path $junctionPath "cccg-dispatch.exe")
    descriptor = $descriptorPath
    sha256 = $descriptor.sha256
    note = "Live sessions keep their current Host; sessions that (re)connect after this use $Version. Point .mcp.json at launchCommand."
} | ConvertTo-Json
